// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;
using DiscForge.Core.Patch;

namespace DiscForge.Core.Rom;

/// <summary>The CRC-32, MD5 and SHA-1 of a ROM, computed over the same bytes No-Intro catalogues.</summary>
public sealed record RomHashSet
{
    public required uint Crc32 { get; init; }
    public required string Md5 { get; init; }
    public required string Sha1 { get; init; }

    /// <summary>The CRC-32 as No-Intro writes it: eight lowercase hex digits.</summary>
    public string Crc32Hex => Crc32.ToString("x8");
}

/// <summary>
/// Computes the No-Intro-style verification hashes for a cartridge ROM. No-Intro hashes the pure
/// cartridge data, so copier/interleave headers that are not part of the cartridge are stripped
/// first:
///
///   • SNES  — a 512-byte SMC/SWC copier header (present when <c>len % 1024 == 512</c>) is EXCLUDED.
///   • Genesis — an interleaved .smd image is de-interleaved to its flat form and its 512-byte SMD
///     header is EXCLUDED.
///   • NES   — the 16-byte iNES header is INCLUDED (it is part of the catalogued file).
///   • All other platforms — the file is hashed as-is (N64 dumps are hashed in their stored byte
///     order; No-Intro's canonical order is big-endian .z64, which callers can normalise upstream).
///
/// CRC-32 reuses the shared zlib/PNG implementation in <see cref="BpsPatch.Crc32"/>.
/// </summary>
public static class RomHashes
{
    public static RomHashSet Compute(byte[] rom, RomId id)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentNullException.ThrowIfNull(id);

        ReadOnlySpan<byte> data = StripHeaders(rom, id);
        byte[] payload = data.ToArray();

        return new RomHashSet
        {
            Crc32 = BpsPatch.Crc32(payload),
            Md5 = Hex(MD5.HashData(payload)),
            Sha1 = Hex(SHA1.HashData(payload)),
        };
    }

    /// <summary>The ROM bytes No-Intro hashes: the input with any excluded copier header removed.</summary>
    public static ReadOnlySpan<byte> StripHeaders(byte[] rom, RomId id)
    {
        if (id.Platform == "SNES")
        {
            int smc = SnesRom.SmcHeaderSize(rom.Length);
            if (smc > 0 && smc <= rom.Length) return rom.AsSpan(smc);
        }
        return rom;
    }

    private static string Hex(byte[] bytes) =>
        System.Convert.ToHexString(bytes).ToLowerInvariant();
}

/// <summary>The outcome of comparing a ROM's computed hashes against expected (No-Intro) values.</summary>
public sealed record RomVerifyResult
{
    public required string Name { get; init; }
    public required bool CrcMatch { get; init; }
    public required bool Md5Match { get; init; }
    public required bool Sha1Match { get; init; }
    public required RomHashSet Computed { get; init; }

    /// <summary>True when every hash the caller supplied matched.</summary>
    public required bool Verified { get; init; }
}

/// <summary>Compares a ROM against a single expected No-Intro entry (name + any of CRC/MD5/SHA-1).
/// Only the hashes the caller supplies are compared; a null expected value is treated as "not
/// provided" and does not fail the match.</summary>
public static class RomVerify
{
    public static RomVerifyResult Check(byte[] rom, RomId id, string name,
        uint? expectedCrc = null, string? expectedMd5 = null, string? expectedSha1 = null)
    {
        var h = RomHashes.Compute(rom, id);
        bool crc = expectedCrc is null || expectedCrc.Value == h.Crc32;
        bool md5 = expectedMd5 is null || string.Equals(expectedMd5, h.Md5, StringComparison.OrdinalIgnoreCase);
        bool sha1 = expectedSha1 is null || string.Equals(expectedSha1, h.Sha1, StringComparison.OrdinalIgnoreCase);

        return new RomVerifyResult
        {
            Name = name,
            CrcMatch = crc,
            Md5Match = md5,
            Sha1Match = sha1,
            Computed = h,
            Verified = crc && md5 && sha1,
        };
    }
}
