// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Rom;

/// <summary>
/// Game Boy Advance cartridge header (at 0x00). Identified by the fixed byte 0x96 at 0xB2 together
/// with the 156-byte Nintendo logo at 0x04 (a logo mismatch is a warning, not a rejection). The
/// reader reads the 12-byte title (0xA0), the 4-byte game code (0xAC), the 2-byte maker code
/// (0xB0), and recomputes the header checksum (0xBD): the two's-complement of the sum of bytes
/// 0xA0..0xBC, minus 0x19, masked to 8 bits — compared against the stored value.
/// </summary>
public static class GbaRom
{
    /// <summary>The 156-byte compressed Nintendo logo every genuine GBA cartridge carries at 0x04.</summary>
    public static readonly byte[] NintendoLogo =
    {
        0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A, 0xA2, 0x21, 0x3D, 0x84, 0x82, 0x0A, 0x84, 0xE4, 0x09, 0xAD,
        0x11, 0x24, 0x8B, 0x98, 0xC0, 0x81, 0x7F, 0x21, 0xA3, 0x52, 0xBE, 0x19, 0x93, 0x09, 0xCE, 0x20,
        0x10, 0x46, 0x4A, 0x4A, 0xF8, 0x27, 0x31, 0xEC, 0x58, 0xC7, 0xE8, 0x33, 0x82, 0xE3, 0xCE, 0xBF,
        0x85, 0xF4, 0xDF, 0x94, 0xCE, 0x4B, 0x09, 0xC1, 0x94, 0x56, 0x8A, 0xC0, 0x13, 0x72, 0xA7, 0xFC,
        0x9F, 0x84, 0x4D, 0x73, 0xA3, 0xCA, 0x9A, 0x61, 0x58, 0x97, 0xA3, 0x27, 0xFC, 0x03, 0x98, 0x76,
        0x23, 0x1D, 0xC7, 0x61, 0x03, 0x04, 0xAE, 0x56, 0xBF, 0x38, 0x84, 0x00, 0x40, 0xA7, 0x0E, 0xFD,
        0xFF, 0x52, 0xFE, 0x03, 0x6F, 0x95, 0x30, 0xF1, 0x97, 0xFB, 0xC0, 0x85, 0x60, 0xD6, 0x80, 0x25,
        0xA9, 0x63, 0xBE, 0x03, 0x01, 0x4E, 0x38, 0xE2, 0xF9, 0xA2, 0x34, 0xFF, 0xBB, 0x3E, 0x03, 0x44,
        0x78, 0x00, 0x90, 0xCB, 0x88, 0x11, 0x3A, 0x94, 0x65, 0xC0, 0x7C, 0x63, 0x87, 0xF0, 0x3C, 0xAF,
        0xD6, 0x25, 0xE4, 0x8B, 0x38, 0x0A, 0xAC, 0x72, 0x21, 0xD4, 0xF8, 0x07,
    };

    public static RomId? TryRead(byte[] rom)
    {
        if (rom.Length < 0xC0) return null;
        if (rom[0xB2] != 0x96) return null;             // the fixed-byte signature
        bool logoOk = LogoMatches(rom);

        byte stored = rom[0xBD];
        byte computed = ComputeHeaderChecksum(rom);

        var warnings = new List<string>();
        if (!logoOk) warnings.Add("Nintendo logo at 0x04 does not match — likely a bad dump or homebrew");
        if (stored != computed)
            warnings.Add($"header checksum mismatch: stored 0x{stored:X2}, computed 0x{computed:X2}");

        string gameCode = RomIdentify.Ascii(rom.AsSpan(0xAC, 4));
        var extra = new Dictionary<string, string>
        {
            ["Maker"] = RomIdentify.Ascii(rom.AsSpan(0xB0, 2)),
            ["FixedByte"] = $"0x{rom[0xB2]:X2}",
            ["HeaderChecksum"] = $"stored 0x{stored:X2}, computed 0x{computed:X2}",
        };

        return new RomId
        {
            Platform = "Game Boy Advance",
            Title = RomIdentify.Ascii(rom.AsSpan(0xA0, 12)),
            GameCode = gameCode,
            Region = Region(gameCode),
            Extra = extra,
            Warnings = warnings,
        };
    }

    public static bool LogoMatches(byte[] rom)
    {
        if (rom.Length < 0x04 + NintendoLogo.Length) return false;
        for (int i = 0; i < NintendoLogo.Length; i++)
            if (rom[0x04 + i] != NintendoLogo[i]) return false;
        return true;
    }

    /// <summary>Header checksum: <c>-(sum of 0xA0..0xBC) - 0x19</c>, masked to 8 bits.</summary>
    public static byte ComputeHeaderChecksum(byte[] rom)
    {
        int sum = 0;
        for (int i = 0xA0; i <= 0xBC; i++) sum += rom[i];
        return (byte)(-(sum + 0x19));
    }

    // The 4th char of the game code is the region ('E' USA, 'J' Japan, 'P' Europe, …).
    private static string Region(string gameCode) => gameCode.Length < 4 ? "" : gameCode[3] switch
    {
        'E' => "USA",
        'J' => "Japan",
        'P' => "Europe",
        'D' => "Germany",
        'F' => "France",
        'I' => "Italy",
        'S' => "Spain",
        'K' => "Korea",
        'C' => "China",
        _ => "",
    };
}
