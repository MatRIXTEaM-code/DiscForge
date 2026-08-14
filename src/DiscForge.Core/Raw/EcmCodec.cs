// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Raw;

/// <summary>
/// The ECM ("Error Code Modeler") container — the classic <c>.ecm</c> format that
/// shrinks a raw CD image by stripping the bytes that a decoder can regenerate:
/// the 12-byte sync, the mode byte, the EDC and the Reed-Solomon P/Q parity of every
/// sector. What remains is only the user data (and, for Mode 1, the sector address,
/// whose value participates in that sector's ECC and so cannot be re-derived).
///
/// This is a faithful, reversible transform — not compression of the data itself and
/// nothing protection-related. Decoding is deterministic: every stripped field is a
/// pure function of the retained bytes plus the sector's position, computed here by the
/// same <see cref="EdcEcc"/> machinery DiscForge already uses to build raw sectors.
///
/// File layout (all multi-byte integers little-endian):
///   "ECM\0"                         4-byte magic
///   repeated { typeCount, payload } records
///   typeCount = 0, count = 0xFFFFFFFF   end-of-stream marker
///   4-byte EDC over the whole reconstructed file
///
/// A <b>typeCount</b> is a variable-length integer. The low two bits of the first byte
/// are the record type; the remaining bits (5 in the first byte, then 7 per following
/// byte, high bit = "more") are the count, stored as <c>actual − 1</c>:
///   type 0  literal        — <c>count</c> raw bytes, copied verbatim
///   type 1  Mode 1         — <c>count</c> sectors, 3 address + 2048 data each
///   type 2  Mode 2 Form 1  — <c>count</c> sectors, 4 subheader + 2048 data each
///   type 3  Mode 2 Form 2  — <c>count</c> sectors, 4 subheader + 2324 data each
///
/// Address handling is the format's one subtlety. A Mode 1 sector's ECC is computed
/// over its header, so the three address bytes are stored. Mode 2's EDC/ECC exclude the
/// header, so the address is reconstructed from the running sector index (LBA n → the
/// absolute MSF of n + 150, i.e. the 2-second lead-in). The encoder only emits a
/// sector as type 1/2/3 when its full 2352-byte reconstruction matches the original
/// byte-for-byte, and otherwise falls back to a literal — so a round trip is always
/// exact regardless of how a particular disc was addressed.
/// </summary>
public static class EcmCodec
{
    private static readonly byte[] Magic = { (byte)'E', (byte)'C', (byte)'M', 0x00 };

    /// <summary>Thrown when the input is not a well-formed ECM stream.</summary>
    public sealed class EcmFormatException(string message) : Exception(message);

    // ---- decode ------------------------------------------------------------

    /// <summary>
    /// Decode an ECM stream to the original raw image. Returns the number of bytes
    /// written. If <paramref name="verifyEdc"/> is set (the default), the trailing
    /// whole-file EDC is checked and a mismatch throws.
    /// </summary>
    public static long Decode(Stream ecm, Stream output, bool verifyEdc = true)
    {
        ArgumentNullException.ThrowIfNull(ecm);
        ArgumentNullException.ThrowIfNull(output);

        Span<byte> magic = stackalloc byte[4];
        ReadExactly(ecm, magic);
        if (!magic.SequenceEqual(Magic))
            throw new EcmFormatException("Not an ECM file (missing \"ECM\\0\" magic).");

        long written = 0;
        uint fileEdc = 0;                       // running EDC over what we emit
        int runningLba = 0;                     // sector index, for Mode 2 addresses
        var sector = new byte[2352];
        var buf = new byte[2352];

        while (true)
        {
            (int type, long count) = ReadTypeCount(ecm);
            // The end marker is a type-0 record whose encoded count is 0xFFFFFFFF.
            if (type == 0 && count == 0x1_0000_0000L) break;

            if (count <= 0) throw new EcmFormatException("ECM record has a non-positive count.");

            if (type == 0)
            {
                long remaining = count;
                while (remaining > 0)
                {
                    int chunk = (int)Math.Min(remaining, buf.Length);
                    ReadExactly(ecm, buf.AsSpan(0, chunk));
                    output.Write(buf, 0, chunk);
                    fileEdc = EdcEcc.ComputeEdc(buf.AsSpan(0, chunk), fileEdc);
                    written += chunk;
                    remaining -= chunk;
                }
                continue;
            }

            for (long i = 0; i < count; i++)
            {
                Array.Clear(sector);
                RawSectorBuilder.WriteSync(sector);

                switch (type)
                {
                    case 1: // Mode 1: stored = 3 address + 2048 data
                        ReadExactly(ecm, sector.AsSpan(0x0C, 3));   // address, verbatim
                        sector[0x0F] = 0x01;
                        ReadExactly(ecm, sector.AsSpan(0x10, 2048));
                        EdcEcc.FillMode1(sector);
                        break;

                    case 2: // Mode 2 Form 1: stored = 4 subheader + 2048 data
                        WriteMode2Address(sector, runningLba);
                        ReadExactly(ecm, sector.AsSpan(0x10, 4));    // subheader copy 1
                        sector.AsSpan(0x10, 4).CopyTo(sector.AsSpan(0x14, 4)); // copy 2
                        ReadExactly(ecm, sector.AsSpan(0x18, 2048));
                        EdcEcc.FillMode2Form1(sector);
                        break;

                    case 3: // Mode 2 Form 2: stored = 4 subheader + 2324 data
                        WriteMode2Address(sector, runningLba);
                        ReadExactly(ecm, sector.AsSpan(0x10, 4));
                        sector.AsSpan(0x10, 4).CopyTo(sector.AsSpan(0x14, 4));
                        ReadExactly(ecm, sector.AsSpan(0x18, 2324));
                        EdcEcc.FillMode2Form2(sector);
                        break;

                    default:
                        throw new EcmFormatException($"Unknown ECM record type {type}.");
                }

                output.Write(sector, 0, 2352);
                fileEdc = EdcEcc.ComputeEdc(sector, fileEdc);
                written += 2352;
                runningLba++;
            }
        }

        // The whole-file EDC follows the end marker.
        Span<byte> edcBytes = stackalloc byte[4];
        ReadExactly(ecm, edcBytes);
        if (verifyEdc)
        {
            uint stored = (uint)edcBytes[0] | ((uint)edcBytes[1] << 8)
                        | ((uint)edcBytes[2] << 16) | ((uint)edcBytes[3] << 24);
            if (stored != fileEdc)
                throw new EcmFormatException(
                    "ECM trailing EDC does not match the reconstructed image — the file is corrupt " +
                    "or was produced by an incompatible encoder.");
        }
        return written;
    }

    // ---- encode ------------------------------------------------------------

    /// <summary>
    /// Encode a raw CD image (2352-byte sectors, optionally with trailing non-sector
    /// bytes) to ECM. Every sector that reconstructs exactly is stored stripped;
    /// anything else is preserved verbatim as a literal, so <c>Decode(Encode(x)) == x</c>
    /// for any input.
    /// </summary>
    public static void Encode(Stream input, Stream ecm)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ecm);

        ecm.Write(Magic, 0, 4);

        // Read the whole input; we need random-ish look-ahead per sector and the
        // whole-file EDC anyway. Callers pass file or memory streams.
        byte[] data = ReadAll(input);
        uint fileEdc = EdcEcc.ComputeEdc(data);

        int pos = 0, runningLba = 0;
        // A pending literal run we grow until we hit an encodable sector.
        int litStart = 0, litLen = 0;

        var probe = new byte[2352];

        while (pos + 2352 <= data.Length)
        {
            int type = Classify(data, pos, runningLba, probe, out _);
            if (type < 0)
            {
                // Not a reconstructable sector — extend the literal run by one byte
                // (not a whole sector: the sync could resync mid-way).
                litLen++;
                pos++;
                continue;
            }

            // Coalesce a run of identical-type sectors.
            int runCount = 0;
            while (pos + 2352 <= data.Length && Classify(data, pos, runningLba, probe, out _) == type)
            {
                runCount++;
                pos += 2352;
                runningLba++;
            }

            FlushLiteral(ecm, data, ref litStart, ref litLen, pos - runCount * 2352);
            WriteTypeCount(ecm, type, runCount);
            WriteStrippedSectors(ecm, data, pos - runCount * 2352, type, runCount);
            litStart = pos;
        }

        // Any trailing bytes (partial sector or unencodable tail) are literal.
        litLen += data.Length - pos;
        FlushLiteral(ecm, data, ref litStart, ref litLen, data.Length);

        // End-of-stream marker: type 0, encoded count 0xFFFFFFFF.
        WriteTypeCountRaw(ecm, 0, 0xFFFF_FFFFu);

        Span<byte> edc = stackalloc byte[4];
        edc[0] = (byte)fileEdc; edc[1] = (byte)(fileEdc >> 8);
        edc[2] = (byte)(fileEdc >> 16); edc[3] = (byte)(fileEdc >> 24);
        ecm.Write(edc);
    }

    // Decide whether the 2352 bytes at data[pos] reconstruct exactly from their
    // stripped form; return the ECM type (1/2/3) or -1 if they must stay literal.
    private static int Classify(byte[] data, int pos, int runningLba, byte[] probe, out int form)
    {
        form = 0;
        var s = data.AsSpan(pos, 2352);

        // Must have the standard sync and a plausible mode byte.
        if (s[0] != 0x00) return -1;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return -1;
        if (s[11] != 0x00) return -1;
        byte mode = s[0x0F];

        Array.Clear(probe);
        RawSectorBuilder.WriteSync(probe);

        if (mode == 0x01)
        {
            s.Slice(0x0C, 3).CopyTo(probe.AsSpan(0x0C, 3));   // stored address
            probe[0x0F] = 0x01;
            s.Slice(0x10, 2048).CopyTo(probe.AsSpan(0x10, 2048));
            EdcEcc.FillMode1(probe);
            return s.SequenceEqual(probe) ? 1 : -1;
        }

        if (mode == 0x02)
        {
            // Form is carried in the subheader submode byte (bit 0x20 = Form 2).
            bool form2 = (s[0x12] & 0x20) != 0;
            WriteMode2Address(probe, runningLba);
            s.Slice(0x10, 4).CopyTo(probe.AsSpan(0x10, 4));
            s.Slice(0x10, 4).CopyTo(probe.AsSpan(0x14, 4));
            if (!form2)
            {
                s.Slice(0x18, 2048).CopyTo(probe.AsSpan(0x18, 2048));
                EdcEcc.FillMode2Form1(probe);
                if (s.SequenceEqual(probe)) { form = 1; return 2; }
                return -1;
            }
            else
            {
                s.Slice(0x18, 2324).CopyTo(probe.AsSpan(0x18, 2324));
                EdcEcc.FillMode2Form2(probe);
                if (s.SequenceEqual(probe)) { form = 2; return 3; }
                return -1;
            }
        }

        return -1;
    }

    private static void WriteStrippedSectors(Stream ecm, byte[] data, int pos, int type, int count)
    {
        for (int i = 0; i < count; i++, pos += 2352)
        {
            var s = data.AsSpan(pos, 2352);
            switch (type)
            {
                case 1:
                    ecm.Write(s.Slice(0x0C, 3));      // address
                    ecm.Write(s.Slice(0x10, 2048));   // data
                    break;
                case 2:
                    ecm.Write(s.Slice(0x10, 4));       // subheader
                    ecm.Write(s.Slice(0x18, 2048));    // data
                    break;
                case 3:
                    ecm.Write(s.Slice(0x10, 4));       // subheader
                    ecm.Write(s.Slice(0x18, 2324));    // data
                    break;
            }
        }
    }

    private static void FlushLiteral(Stream ecm, byte[] data, ref int litStart, ref int litLen, int upTo)
    {
        // The literal run is [litStart, litStart+litLen); it always ends at upTo.
        if (litLen <= 0) { litStart = upTo; return; }
        WriteTypeCount(ecm, 0, litLen);
        ecm.Write(data, litStart, litLen);
        litStart = upTo;
        litLen = 0;
    }

    // ---- Mode 2 address ----------------------------------------------------

    private static void WriteMode2Address(byte[] sector, int lba)
    {
        int a = lba + 150;                        // 2-second lead-in
        sector[0x0C] = Bcd.From(a / (60 * 75));
        sector[0x0D] = Bcd.From(a / 75 % 60);
        sector[0x0E] = Bcd.From(a % 75);
        sector[0x0F] = 0x02;
    }

    // ---- variable-length type/count ---------------------------------------

    private static (int Type, long Count) ReadTypeCount(Stream s)
    {
        int b = s.ReadByte();
        if (b < 0) throw new EcmFormatException("Unexpected end of ECM stream.");
        int type = b & 0x03;
        long num = (b >> 2) & 0x1F;               // 5 count bits in the first byte
        int shift = 5;
        while ((b & 0x80) != 0)
        {
            b = s.ReadByte();
            if (b < 0) throw new EcmFormatException("Unexpected end of ECM stream.");
            num |= (long)(b & 0x7F) << shift;
            shift += 7;
            // Up to five bytes (5 + 7×4 = 33 bit-positions) carry a full 32-bit count;
            // a sixth continuation byte means the field is malformed.
            if (shift > 35) throw new EcmFormatException("ECM count field is too long.");
        }
        // Encoded value is (actual − 1); the end marker encodes 0xFFFFFFFF → actual 2^32.
        return (type, num + 1);
    }

    // Write a record whose ACTUAL count is `count` (encoded as count − 1).
    private static void WriteTypeCount(Stream s, int type, long count)
        => WriteTypeCountRaw(s, type, (uint)(count - 1));

    // Write a record with the raw ENCODED count value (already actual − 1).
    private static void WriteTypeCountRaw(Stream s, int type, uint encoded)
    {
        int first = (type & 0x03) | (int)((encoded & 0x1F) << 2);
        uint rest = encoded >> 5;
        if (rest != 0) first |= 0x80;
        s.WriteByte((byte)first);
        while (rest != 0)
        {
            int b = (int)(rest & 0x7F);
            rest >>= 7;
            if (rest != 0) b |= 0x80;
            s.WriteByte((byte)b);
        }
    }

    // ---- stream helpers ----------------------------------------------------

    private static void ReadExactly(Stream s, Span<byte> dst)
    {
        int off = 0;
        while (off < dst.Length)
        {
            int n = s.Read(dst[off..]);
            if (n <= 0) throw new EcmFormatException("Unexpected end of ECM stream.");
            off += n;
        }
    }

    private static byte[] ReadAll(Stream s)
    {
        if (s is MemoryStream ms) return ms.ToArray();
        using var buf = new MemoryStream();
        s.CopyTo(buf);
        return buf.ToArray();
    }
}
