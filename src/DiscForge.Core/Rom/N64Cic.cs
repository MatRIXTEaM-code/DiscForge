// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Util;

namespace DiscForge.Core.Rom;

/// <summary>The result of inspecting an N64 cartridge ROM's boot security (CIC) and boot checksums.</summary>
public sealed record N64CicInfo
{
    /// <summary>Detected on-disk byte order: "z64" (big-endian), "v64" (byte-swapped) or "n64" (little-endian).</summary>
    public required string ByteOrder { get; init; }
    /// <summary>The CRC-32 of the 4032-byte IPL3 bootcode that identifies the CIC.</summary>
    public required uint BootcodeCrc32 { get; init; }
    /// <summary>The CIC chip designation (e.g. "CIC-NUS-6102 / 7101"), or null when the bootcode is unrecognised.</summary>
    public string? Cic { get; init; }
    /// <summary>Region the CIC implies from its chip family (NTSC 61xx vs PAL 71xx), or null when unknown.</summary>
    public string? CicRegion { get; init; }
    /// <summary>CRC1 stored in the header at 0x10.</summary>
    public required uint Crc1Stored { get; init; }
    /// <summary>CRC2 stored in the header at 0x14.</summary>
    public required uint Crc2Stored { get; init; }
    /// <summary>Recomputed CRC1, or null when the CIC is unknown or the ROM is too small to check.</summary>
    public uint? Crc1Calc { get; init; }
    public uint? Crc2Calc { get; init; }
    /// <summary>True/false when both boot CRCs could be recomputed and compared; null when the check was not possible.</summary>
    public bool? CrcValid => Crc1Calc is { } c1 && Crc2Calc is { } c2 ? c1 == Crc1Stored && c2 == Crc2Stored : null;
}

/// <summary>
/// Identifies a Nintendo 64 cartridge's <b>CIC</b> boot-security chip and verifies its boot checksums — the
/// preservation fields <c>rom-info</c> stops short of. Every N64 ROM begins with a 4032-byte IPL3 bootcode
/// (0x40..0x1000) whose CRC-32 uniquely names the CIC variant (6101/6102/6103/6105/6106 and their PAL 71xx
/// twins). The two header words at 0x10/0x14 (CRC1/CRC2) are a boot checksum computed over the first ~1&#160;MB
/// with a seed that depends on that CIC; recomputing them confirms whether the ROM is intact or has been
/// modified (a hacked or truncated dump fails the check). This reads and verifies those fields — it computes,
/// reports, and changes nothing on the ROM.
/// </summary>
public static class N64Cic
{
    private const int HeaderSize = 0x40;
    private const int BootcodeSize = 0x1000 - HeaderSize;   // 4032 bytes of IPL3
    private const int ChecksumStart = 0x1000;
    private const int ChecksumLength = 0x0010_0000;          // 1 MiB

    // CRC-32 of the IPL3 bootcode → CIC. These constants are the long-published values every N64 tool uses.
    private static (uint Crc, string Cic, string Region, uint Seed)? MatchBootcode(uint crc) => crc switch
    {
        0x6170A4A1 => (crc, "CIC-NUS-6101", "NTSC", 0xF8CA4DDCu),
        0x90BB6CB5 => (crc, "CIC-NUS-6102 / 7101", "NTSC/PAL", 0xF8CA4DDCu),
        0x0B050EE0 => (crc, "CIC-NUS-6103 / 7103", "NTSC/PAL", 0xA3886759u),
        0x98BC2C86 => (crc, "CIC-NUS-6105 / 7105", "NTSC/PAL", 0xDF26F436u),
        0xACC8580A => (crc, "CIC-NUS-6106 / 7106", "NTSC/PAL", 0x1FEA617Au),
        0x009E9EA3 => (crc, "CIC-NUS-7102", "PAL", 0xF8CA4DDCu),
        _ => null,
    };

    public static N64CicInfo Analyze(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        if (rom.Length < 0x1000)
            throw new ArgumentException("Too small to be an N64 ROM (need at least a 4 KiB boot region).", nameof(rom));

        string order = DetectOrder(rom) ?? throw new ArgumentException(
            "Not a recognised N64 ROM: the first four bytes match no known byte order.", nameof(rom));

        // Work on a big-endian normalised copy so every field and checksum reads natively.
        byte[] be = Normalize(rom, order);

        uint bootCrc = Crc32.Compute(be.AsSpan(HeaderSize, BootcodeSize));
        uint crc1Stored = U32Be(be, 0x10);
        uint crc2Stored = U32Be(be, 0x14);

        var match = MatchBootcode(bootCrc);
        uint? c1 = null, c2 = null;

        if (match is { } m && be.Length >= ChecksumStart + ChecksumLength)
        {
            var (calc1, calc2) = ComputeBootCrc(be, DetectCicNumber(m.Cic), m.Seed);
            c1 = calc1; c2 = calc2;
        }

        return new N64CicInfo
        {
            ByteOrder = order,
            BootcodeCrc32 = bootCrc,
            Cic = match?.Cic,
            CicRegion = match?.Region,
            Crc1Stored = crc1Stored,
            Crc2Stored = crc2Stored,
            Crc1Calc = c1,
            Crc2Calc = c2,
        };
    }

    /// <summary>
    /// The N64 boot-checksum algorithm (as popularised by n64crc): six accumulators are folded over the first
    /// megabyte after the bootcode, seeded from the CIC. 6105 mixes in a rolling window of the bootcode; 6106
    /// combines the accumulators differently. Returns the pair (CRC1, CRC2). <paramref name="be"/> must be
    /// big-endian and at least <c>0x101000</c> bytes.
    /// </summary>
    public static (uint Crc1, uint Crc2) ComputeBootCrc(byte[] be, int cicNumber, uint seed)
    {
        ArgumentNullException.ThrowIfNull(be);
        if (be.Length < ChecksumStart + ChecksumLength)
            throw new ArgumentException("ROM is too small to hold the 1 MiB boot-checksum region.", nameof(be));

        uint t1 = seed, t2 = seed, t3 = seed, t4 = seed, t5 = seed, t6 = seed;

        for (int i = ChecksumStart; i < ChecksumStart + ChecksumLength; i += 4)
        {
            uint d = U32Be(be, i);
            if (unchecked(t6 + d) < t6) t4++;
            t6 += d;
            t3 ^= d;
            uint r = Rol(d, (int)(d & 0x1F));
            t5 += r;
            if (t2 > d) t2 ^= r;
            else t2 ^= t6 ^ d;

            if (cicNumber == 6105)
                t1 += U32Be(be, HeaderSize + 0x0710 + (i & 0xFF)) ^ d;
            else
                t1 += t5 ^ d;
        }

        if (cicNumber == 6106)
            return (unchecked((t6 ^ t4) + t3), unchecked((t5 ^ t2) + t1));
        return (t6 ^ t4 ^ t3, t5 ^ t2 ^ t1);
    }

    private static int DetectCicNumber(string cic) =>
        cic.Contains("6101") ? 6101 :
        cic.Contains("6102") || cic.Contains("7101") || cic.Contains("7102") ? 6102 :
        cic.Contains("6103") ? 6103 :
        cic.Contains("6105") ? 6105 :
        cic.Contains("6106") ? 6106 : 6102;

    /// <summary>Detect the ROM's byte order from its first four bytes; null if it matches none.</summary>
    public static string? DetectOrder(byte[] r)
    {
        if (r.Length < 4) return null;
        if (r[0] == 0x80 && r[1] == 0x37 && r[2] == 0x12 && r[3] == 0x40) return "z64";
        if (r[0] == 0x37 && r[1] == 0x80 && r[2] == 0x40 && r[3] == 0x12) return "v64";
        if (r[0] == 0x40 && r[1] == 0x12 && r[2] == 0x37 && r[3] == 0x80) return "n64";
        return null;
    }

    /// <summary>Return a big-endian (.z64) copy of the ROM, whatever order it arrived in.</summary>
    public static byte[] Normalize(byte[] rom, string order)
    {
        var b = (byte[])rom.Clone();
        switch (order)
        {
            case "v64":
                for (int i = 0; i + 1 < b.Length; i += 2) (b[i], b[i + 1]) = (b[i + 1], b[i]);
                break;
            case "n64":
                for (int i = 0; i + 3 < b.Length; i += 4)
                {
                    (b[i], b[i + 3]) = (b[i + 3], b[i]);
                    (b[i + 1], b[i + 2]) = (b[i + 2], b[i + 1]);
                }
                break;
        }
        return b;
    }

    private static uint Rol(uint v, int b) => b == 0 ? v : (v << b) | (v >> (32 - b));

    private static uint U32Be(byte[] b, int off) =>
        (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);
}
