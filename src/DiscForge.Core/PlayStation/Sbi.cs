// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;

namespace DiscForge.Core.PlayStation;

/// <summary>
/// SBI ("SubChannel Binary Information") — the small, portable file that carries
/// a PlayStation disc's LibCrypt subchannel so an image without a full .sub can
/// still reproduce it. It records, per affected sector, the exact Q subchannel
/// bytes the disc actually holds — including the deliberately wrong ones — keyed
/// by the sector's true absolute position.
///
/// This is preservation, not circumvention: it copies the disc's own protection
/// data verbatim so a faithful reproduction passes the game's check because the
/// data genuinely is there, exactly as it was on the original. It defeats
/// nothing — a disc without LibCrypt produces an empty SBI. It is the compact
/// counterpart to DiscForge's raw .sub sidecar: the .sub is every sector's full
/// 96-byte P–W, while the SBI is just the handful of Q frames that differ from a
/// clean disc, in the format emulators read.
///
/// Format (as read by the common PlayStation emulators): a 4-byte magic
/// "SBI\0", then a sequence of entries. Each entry is three BCD bytes of the
/// sector's absolute M:S:F, one type byte, and the type's payload. Type 1 —
/// the one LibCrypt needs — is followed by the ten Q bytes (control/ADR, track,
/// index, relative M:S:F, zero, absolute M:S:F) without the two CRC bytes.
/// </summary>
public static class Sbi
{
    /// <summary>"SBI\0".</summary>
    public static ReadOnlySpan<byte> Magic => new byte[] { 0x53, 0x42, 0x49, 0x00 };

    /// <summary>The one type LibCrypt uses: ten raw Q bytes (no CRC) follow.</summary>
    public const byte TypeQ10 = 0x01;

    /// <summary>Two seconds of lead-in: LBA 0 sits at absolute time 00:02:00.</summary>
    public const int LeadIn = 150;

    public sealed record Entry(int Minute, int Second, int Frame, byte Type, byte[] Data)
    {
        public long AbsSectors => (Minute * 60L + Second) * 75 + Frame;
        public override string ToString() => $"{Minute:D2}:{Second:D2}:{Frame:D2} type {Type}";
    }

    public sealed record Document(IReadOnlyList<Entry> Entries)
    {
        public bool IsEmpty => Entries.Count == 0;
    }

    // ---- build from a captured .sub ---------------------------------------

    /// <summary>
    /// Build an SBI from a raw sub-channel sidecar (96-byte interleaved P–W
    /// frames, LBA-ordered from <paramref name="startLba"/>). An entry is emitted
    /// for every sector whose Q is anomalous — a wrong CRC, or (for a position
    /// Q) an absolute address that doesn't match where the sector actually is.
    /// Those are the LibCrypt sectors; a clean disc yields an empty document.
    /// </summary>
    /// <param name="maxEntries">Refuse to emit more than this many — a flood of
    /// anomalies is read damage, not protection, and an SBI of thousands of
    /// entries would be meaningless.</param>
    public static Document FromSubchannel(ReadOnlySpan<byte> subcode, uint startLba = 0,
                                          int maxEntries = 100)
    {
        long frames = subcode.Length / RawSubchannel.FrameSize;
        var q = new byte[12];
        var entries = new List<Entry>();

        for (long i = 0; i < frames; i++)
        {
            var frame = subcode.Slice((int)(i * RawSubchannel.FrameSize), RawSubchannel.FrameSize);
            RawSubchannel.ExtractQ(frame, q);

            long lba = startLba + i;
            if (!IsAnomalous(q, lba)) continue;

            // The entry is keyed by the sector's TRUE position, so the emulator
            // matches it to the sector being read; the payload is the disc's own
            // (possibly wrong) Q, preserved verbatim.
            long abs = lba + LeadIn;
            int f = (int)(abs % 75);
            long s2 = abs / 75;
            int s = (int)(s2 % 60);
            int m = (int)(s2 / 60);

            var data = new byte[10];
            q.AsSpan(0, 10).CopyTo(data);
            entries.Add(new Entry(m, s, f, TypeQ10, data));

            if (entries.Count > maxEntries)
                throw new InvalidDataException(
                    $"Over {maxEntries} anomalous Q frames — this reads as disc damage, not " +
                    "LibCrypt. Refusing to write an SBI; the .sub sidecar preserves everything either way.");
        }

        return new Document(entries);
    }

    /// <summary>A Q frame is anomalous if its CRC is wrong, or if it is a
    /// position Q (ADR 1) whose stored absolute address is not where the sector
    /// physically is. LibCrypt uses both kinds.</summary>
    private static bool IsAnomalous(ReadOnlySpan<byte> q12, long lba)
    {
        if (!RawSubchannel.QCrcValid(q12)) return true;

        int adr = q12[0] & 0x0F;
        if (adr == 1)
        {
            int m = FromBcd(q12[7]);
            int s = FromBcd(q12[8]);
            int f = FromBcd(q12[9]);
            if (m < 0 || s < 0 || f < 0) return true;          // not valid BCD → anomalous
            long storedAbs = (m * 60L + s) * 75 + f;
            long trueAbs = lba + LeadIn;
            if (storedAbs != trueAbs) return true;
        }
        return false;
    }

    // ---- serialise / parse ------------------------------------------------

    public static byte[] Write(Document doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        using var ms = new MemoryStream();
        ms.Write(Magic);
        foreach (var e in doc.Entries)
        {
            ms.WriteByte(ToBcd(e.Minute));
            ms.WriteByte(ToBcd(e.Second));
            ms.WriteByte(ToBcd(e.Frame));
            ms.WriteByte(e.Type);
            ms.Write(e.Data, 0, e.Data.Length);
        }
        return ms.ToArray();
    }

    public static Document Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || !bytes[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Not an SBI file (missing the \"SBI\\0\" magic).");

        var entries = new List<Entry>();
        int p = 4;
        while (p < bytes.Length)
        {
            if (p + 4 > bytes.Length)
                throw new InvalidDataException("Truncated SBI entry header.");
            int m = FromBcd(bytes[p]);
            int s = FromBcd(bytes[p + 1]);
            int f = FromBcd(bytes[p + 2]);
            byte type = bytes[p + 3];
            p += 4;

            int payload = type switch
            {
                TypeQ10 => 10,   // ten Q bytes without CRC
                0x02 => 3,
                0x03 => 3,
                _ => throw new InvalidDataException($"Unknown SBI entry type 0x{type:X2}."),
            };
            if (p + payload > bytes.Length)
                throw new InvalidDataException("Truncated SBI entry payload.");

            var data = bytes.Slice(p, payload).ToArray();
            p += payload;
            entries.Add(new Entry(m, s, f, type, data));
        }
        return new Document(entries);
    }

    public static string Describe(Document doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.IsEmpty)
            return "Empty SBI — no LibCrypt subchannel found on this disc.";
        var lines = doc.Entries.Take(32).Select(e =>
            $"  {e.Minute:D2}:{e.Second:D2}:{e.Frame:D2}  type {e.Type}  " +
            string.Join(' ', e.Data.Select(b => b.ToString("X2"))));
        string body = string.Join('\n', lines);
        if (doc.Entries.Count > 32) body += $"\n  … and {doc.Entries.Count - 32} more";
        return $"SBI with {doc.Entries.Count} entry(ies):\n{body}";
    }

    // ---- BCD ---------------------------------------------------------------

    private static byte ToBcd(int v)
    {
        if (v is < 0 or > 99) throw new ArgumentOutOfRangeException(nameof(v), "BCD range is 0–99.");
        return (byte)(((v / 10) << 4) | (v % 10));
    }

    /// <summary>Decode a BCD byte, or -1 if either nibble is not a decimal digit.</summary>
    private static int FromBcd(byte b)
    {
        int hi = b >> 4, lo = b & 0x0F;
        if (hi > 9 || lo > 9) return -1;
        return hi * 10 + lo;
    }
}
