// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Compression;

namespace DiscForge.Core.GameCube;

/// <summary>
/// Reconstructs a GameCube ISO from an RVZ/WIA container by walking the raw-data and group tables
/// and decompressing each group with its codec. The zstd blocker is solved
/// (<see cref="ZstdDecoder"/>), so Zstandard- and uncompressed-group RVZ files reconstruct here.
///
/// STATUS — the zstd data path is validated against hand-built RVZ containers (single- and
/// multi-group, non-packed and RVZ-packed) whose ISO is known: the container walk, table
/// decompression, group decompression, offset math and the data/junk unpack are proven byte-exact
/// in tests. What is NOT yet validated against a REAL RVZ file are the honest limits below:
///   1. RVZ "packed" groups interleave real data with Nintendo *junk* (disc padding). The junk is
///      produced by Nintendo's lagged-Fibonacci PRNG, which cannot be reproduced bit-exact without
///      a reference, and DiscForge will not ship a guessed PRNG. Junk runs are therefore
///      ZERO-FILLED and counted. The output is **data-exact** (the filesystem and every real byte
///      are correct, so files extract and the disc mounts) but **not bit-exact** where the disc was
///      scrubbed — it will not match a Redump hash until the LFG lands (needs a fixture to validate).
///   2. Only GameCube discs (disc_type 1) are handled. Wii (disc_type 2) needs the partition
///      hash-tree + AES layer and is declined.
///   3. Only the zstd and "none" group codecs are decoded; bzip2/purge/lzma/lzma2 are declined.
///
/// Clean-room from the public WIA/RVZ container description.
/// </summary>
public static class RvzDecoder
{
    private const int FileHeadSize = 0x48;
    private const int DiscStructOffset = FileHeadSize;   // wia_disc begins at 0x48

    public sealed record DecodeReport
    {
        public required long IsoBytes { get; init; }
        public required int Groups { get; init; }
        public required long JunkBytesZeroFilled { get; init; }
        public bool DataExact => true;
        public bool BitExact => JunkBytesZeroFilled == 0;
    }

    /// <summary>Reconstruct the GameCube ISO to <paramref name="output"/>. Returns a report noting
    /// how much junk was zero-filled (0 = bit-exact).</summary>
    public static DecodeReport Decode(byte[] rvz, Stream output)
    {
        ArgumentNullException.ThrowIfNull(rvz);
        ArgumentNullException.ThrowIfNull(output);

        var info = RvzReader.ReadInfo(new MemoryStream(rvz, false));
        if (info.Format is not (RvzFormat.Rvz or RvzFormat.Wia))
            throw new GameCubeFormatException("Not a WIA/RVZ file.");

        int ds = DiscStructOffset;
        uint discType = BE32(rvz, ds + 0x00);
        var compression = (RvzCompression)BE32(rvz, ds + 0x04);
        uint chunkSize = BE32(rvz, ds + 0x0C);
        var discId = rvz.AsSpan(ds + 0x10, 4).ToArray();     // for future junk regeneration

        if (discType != 1)
            throw new GameCubeFormatException(
                discType == 2
                    ? "This is a Wii RVZ. Reconstructing an encrypted Wii ISO needs the console common key and " +
                      "AES re-encryption over protected content — outside this toolkit's clean-room, " +
                      "no-circumvention boundary. Use `RvzDecoder.ReadWiiStructure` to map its partitions instead."
                    : $"Unsupported RVZ disc type {discType} (expected 1 = GameCube).");
        if (compression is not (RvzCompression.Zstd or RvzCompression.None))
            throw new GameCubeFormatException(
                $"This RVZ uses the '{compression}' group codec, which isn't decoded yet — only zstd and none are. " +
                "Recompress it as zstd (RVZ's default) with Dolphin/wit, or convert to ISO there.");

        uint numRawData = BE32(rvz, ds + 0xB4);
        ulong rawDataOffset = BE64(rvz, ds + 0xB8);
        uint rawDataSize = BE32(rvz, ds + 0xC0);
        uint numGroup = BE32(rvz, ds + 0xC4);
        ulong groupOffset = BE64(rvz, ds + 0xC8);
        uint groupSize = BE32(rvz, ds + 0xD0);

        bool isRvz = info.Format == RvzFormat.Rvz;
        int groupEntrySize = isRvz ? 12 : 8;

        // The raw-data and group tables are themselves compressed with the disc codec.
        byte[] rawTable = DecompressBlob(rvz, (long)rawDataOffset, (int)rawDataSize, compression,
                                         (int)(numRawData * 24));
        byte[] groupTable = DecompressBlob(rvz, (long)groupOffset, (int)groupSize, compression,
                                           (int)(numGroup * (uint)groupEntrySize));

        long isoSize = (long)info.IsoSize;
        long junkFilled = 0;
        int groupsDecoded = 0;

        // Walk each raw-data region; each maps a contiguous ISO range to a run of groups.
        for (int r = 0; r < numRawData; r++)
        {
            int ro = r * 24;
            long regionIsoOffset = (long)BE64(rawTable, ro + 0x00);
            long regionIsoSize = (long)BE64(rawTable, ro + 0x08);
            uint firstGroup = BE32(rawTable, ro + 0x10);
            uint groupCount = BE32(rawTable, ro + 0x14);

            for (uint g = 0; g < groupCount; g++)
            {
                uint gi = firstGroup + g;
                if (gi >= numGroup) throw new GameCubeFormatException("RVZ group index out of range.");
                long chunkIsoOffset = regionIsoOffset + (long)g * chunkSize;
                int chunkLen = (int)Math.Min(chunkSize, regionIsoOffset + regionIsoSize - chunkIsoOffset);
                if (chunkLen <= 0) break;

                byte[] chunk = DecodeGroup(rvz, groupTable, (int)gi, groupEntrySize, isRvz,
                                           compression, chunkLen, chunkIsoOffset, ref junkFilled);

                output.Seek(chunkIsoOffset, SeekOrigin.Begin);
                output.Write(chunk, 0, chunkLen);
                groupsDecoded++;
            }
        }

        // Ensure the ISO is the declared length (tail padding, if any, stays zero).
        if (output.Length < isoSize) { output.SetLength(isoSize); }

        return new DecodeReport
        {
            IsoBytes = isoSize,
            Groups = groupsDecoded,
            JunkBytesZeroFilled = junkFilled,
        };
    }

    /// <summary>
    /// Reconstruct the UNENCRYPTED prefix [0, limit) of the disc from the RVZ's raw-data regions only —
    /// the disc header and (for Wii) the partition table, which live outside any encrypted partition.
    /// Works for GameCube and Wii alike because raw-data regions are never encrypted. It never touches a
    /// partition-data region, derives a key, or decrypts anything.
    /// </summary>
    public static byte[] DecodeUnencryptedPrefix(byte[] rvz, long limit)
    {
        ArgumentNullException.ThrowIfNull(rvz);
        if (limit <= 0) return Array.Empty<byte>();
        var info = RvzReader.ReadInfo(new MemoryStream(rvz, false));

        int ds = DiscStructOffset;
        var compression = (RvzCompression)BE32(rvz, ds + 0x04);
        uint chunkSize = BE32(rvz, ds + 0x0C);
        if (compression is not (RvzCompression.Zstd or RvzCompression.None))
            throw new GameCubeFormatException(
                $"This RVZ uses the '{compression}' group codec, which isn't decoded yet — only zstd and none are.");

        uint numRawData = BE32(rvz, ds + 0xB4);
        ulong rawDataOffset = BE64(rvz, ds + 0xB8);
        uint rawDataSize = BE32(rvz, ds + 0xC0);
        uint numGroup = BE32(rvz, ds + 0xC4);
        ulong groupOffset = BE64(rvz, ds + 0xC8);
        uint groupSize = BE32(rvz, ds + 0xD0);
        bool isRvz = info.Format == RvzFormat.Rvz;
        int groupEntrySize = isRvz ? 12 : 8;

        byte[] rawTable = DecompressBlob(rvz, (long)rawDataOffset, (int)rawDataSize, compression, (int)(numRawData * 24));
        byte[] groupTable = DecompressBlob(rvz, (long)groupOffset, (int)groupSize, compression,
                                           (int)(numGroup * (uint)groupEntrySize));

        limit = Math.Min(limit, (long)info.IsoSize);
        var prefix = new byte[limit];
        long junk = 0;
        for (int r = 0; r < numRawData; r++)
        {
            int ro = r * 24;
            long regionIsoOffset = (long)BE64(rawTable, ro + 0x00);
            long regionIsoSize = (long)BE64(rawTable, ro + 0x08);
            if (regionIsoOffset >= limit) continue;
            uint firstGroup = BE32(rawTable, ro + 0x10);
            uint groupCount = BE32(rawTable, ro + 0x14);

            for (uint g = 0; g < groupCount; g++)
            {
                long chunkIsoOffset = regionIsoOffset + (long)g * chunkSize;
                if (chunkIsoOffset >= limit) break;
                uint gi = firstGroup + g;
                if (gi >= numGroup) break;
                int chunkLen = (int)Math.Min(chunkSize, regionIsoOffset + regionIsoSize - chunkIsoOffset);
                if (chunkLen <= 0) break;
                byte[] chunk = DecodeGroup(rvz, groupTable, (int)gi, groupEntrySize, isRvz, compression,
                                           chunkLen, chunkIsoOffset, ref junk);
                int copyLen = (int)Math.Min(chunkLen, limit - chunkIsoOffset);
                Array.Copy(chunk, 0, prefix, (int)chunkIsoOffset, copyLen);
            }
        }
        return prefix;
    }

    /// <summary>
    /// Read a WII RVZ's partition STRUCTURE (game id, and the data/update/channel partitions with their
    /// offsets) from the unencrypted regions only, via <see cref="WiiDisc"/>. This lets a Wii RVZ be
    /// understood without any keys or decryption. It does NOT reconstruct the encrypted ISO — that needs
    /// the console common key + AES re-encryption over protected content, which is outside this toolkit's
    /// clean-room, no-circumvention boundary (see docs/RVZ.md).
    /// </summary>
    public static WiiVolume ReadWiiStructure(byte[] rvz)
    {
        ArgumentNullException.ThrowIfNull(rvz);
        uint discType = BE32(rvz, DiscStructOffset + 0x00);
        if (discType != 2)
            throw new GameCubeFormatException($"Not a Wii RVZ (disc type {discType}; 2 = Wii).");
        // 0x60000 covers the disc header and the partition-group tables, all in unencrypted raw-data.
        var prefix = DecodeUnencryptedPrefix(rvz, 0x60000);
        return WiiDisc.Read(new MemoryStream(prefix, false));
    }

    private static byte[] DecodeGroup(byte[] rvz, byte[] groupTable, int gi, int entrySize, bool isRvz,
                                      RvzCompression comp, int chunkLen, long chunkIsoOffset, ref long junkFilled)
    {
        int go = gi * entrySize;
        uint dataOffsetUnits = BE32(groupTable, go + 0x00);
        uint dataSizeFlag = BE32(groupTable, go + 0x04);
        uint rvzPacked = isRvz ? BE32(groupTable, go + 0x08) : 0;

        long dataOffset = (long)dataOffsetUnits * 4;
        int storedSize = (int)(dataSizeFlag & 0x7FFFFFFF);
        bool compressed = (dataSizeFlag & 0x80000000) != 0;

        // A zero-size group is an all-junk (or all-zero) chunk.
        if (storedSize == 0)
        {
            junkFilled += chunkLen;
            return new byte[chunkLen];
        }
        if (dataOffset < 0 || dataOffset + storedSize > rvz.Length)
            throw new GameCubeFormatException("RVZ group points outside the file.");

        var stored = rvz.AsSpan((int)dataOffset, storedSize);
        byte[] payload = compressed
            ? (comp == RvzCompression.Zstd ? ZstdDecoder.Decompress(stored) : stored.ToArray())
            : stored.ToArray();

        if (rvzPacked == 0)
        {
            // Not packed: the payload IS the chunk data (padded/truncated to chunkLen).
            var outc = new byte[chunkLen];
            Array.Copy(payload, 0, outc, 0, Math.Min(payload.Length, chunkLen));
            return outc;
        }

        // RVZ-packed: a run of [u32 size] entries; MSB set = a junk run (zero-filled here, see
        // class note), else a literal-data run copied from the payload.
        var result = new byte[chunkLen];
        int inPos = 0, outPos = 0;
        while (outPos < chunkLen && inPos + 4 <= payload.Length)
        {
            uint size = BE32(payload, inPos); inPos += 4;
            bool junk = (size & 0x80000000) != 0;
            int runLen = (int)(size & 0x7FFFFFFF);
            if (runLen <= 0) break;
            int take = Math.Min(runLen, chunkLen - outPos);
            if (junk)
            {
                // Zero-fill junk (Nintendo LFG not reproduced — see class documentation).
                junkFilled += take;
                outPos += take;
                // Junk runs carry no bytes in the payload stream.
            }
            else
            {
                if (inPos + take > payload.Length) take = Math.Max(0, payload.Length - inPos);
                Array.Copy(payload, inPos, result, outPos, take);
                inPos += runLen;      // advance by the full run even if truncated at chunk end
                outPos += take;
            }
        }
        return result;
    }

    private static byte[] DecompressBlob(byte[] rvz, long offset, int size, RvzCompression comp, int expected)
    {
        if (offset < 0 || offset + size > rvz.Length)
            throw new GameCubeFormatException("RVZ table points outside the file.");
        var span = rvz.AsSpan((int)offset, size);
        byte[] outb = comp == RvzCompression.Zstd ? ZstdDecoder.Decompress(span) : span.ToArray();
        if (outb.Length < expected)
            throw new GameCubeFormatException(
                $"RVZ table decompressed to {outb.Length} bytes, expected at least {expected}.");
        return outb;
    }

    private static uint BE32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
    private static uint BE32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o, 4));
    private static ulong BE64(byte[] b, int o) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o, 8));
}
