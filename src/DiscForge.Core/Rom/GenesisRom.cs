// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Rom;

/// <summary>
/// Sega Mega Drive / Genesis cartridge header. The console-name field at 0x100 (16 bytes) begins
/// "SEGA " (e.g. "SEGA MEGA DRIVE", "SEGA GENESIS"). The reader reads the domestic title (0x120,
/// 48 bytes), overseas title (0x150, 48), serial/product code (0x180, 14), the ROM checksum
/// (u16 big-endian at 0x18E) and the region field (0x1F0, up to 3 chars of J/U/E).
///
/// The interleaved SMD copier format — a 512-byte SMD header followed by blocks whose even and
/// odd bytes are split apart — is detected and de-interleaved so the underlying header can be
/// read; the result is flagged "SMD interleaved" in <see cref="RomId.Extra"/>.
/// </summary>
public static class GenesisRom
{
    public static RomId? TryRead(byte[] rom)
    {
        bool smd = false;
        byte[] data = rom;

        // A .smd dump: 512-byte header, then 16 KiB blocks split into a 8 KiB even half followed
        // by a 8 KiB odd half. The header's byte 8 = 0xAA, byte 9 = 0xBB is the documented marker.
        if (rom.Length > 0x200 && rom[8] == 0xAA && rom[9] == 0xBB)
        {
            var deint = DeinterleaveSmd(rom);
            if (deint is not null && RomIdentify.AsciiEquals(deint, 0x100, "SEGA "))
            {
                data = deint;
                smd = true;
            }
        }

        if (data.Length < 0x1F0 + 3) return null;
        if (!RomIdentify.AsciiEquals(data, 0x100, "SEGA ")) return null;

        string console = RomIdentify.Ascii(data.AsSpan(0x100, 16));
        string domestic = RomIdentify.Ascii(data.AsSpan(0x120, 48));
        string overseas = RomIdentify.Ascii(data.AsSpan(0x150, 48));
        string serial = RomIdentify.Ascii(data.AsSpan(0x180, 14));
        ushort checksum = RomIdentify.U16Be(data, 0x18E);
        string region = RomIdentify.Ascii(data.AsSpan(0x1F0, 3));

        var extra = new Dictionary<string, string>
        {
            ["Console"] = console,
            ["OverseasTitle"] = overseas,
            ["Serial"] = serial,
            ["Checksum"] = $"{checksum:X4}",
            ["RegionField"] = region,
        };
        if (smd) extra["Interleave"] = "SMD interleaved (de-interleaved for parsing)";

        return new RomId
        {
            Platform = "Sega Mega Drive / Genesis",
            Title = domestic.Length > 0 ? domestic : overseas,
            GameCode = serial,
            Region = Region(region),
            Extra = extra,
        };
    }

    // De-interleave a 512-byte-headered SMD image into a flat big-endian ROM. Each 16 KiB block
    // holds its odd bytes in the first 8 KiB and its even bytes in the second 8 KiB.
    private static byte[]? DeinterleaveSmd(byte[] rom)
    {
        int body = rom.Length - 512;
        if (body <= 0 || body % 0x4000 != 0) return null;
        var outp = new byte[body];
        int blocks = body / 0x4000;
        for (int b = 0; b < blocks; b++)
        {
            int src = 512 + b * 0x4000;
            int dst = b * 0x4000;
            for (int i = 0; i < 0x2000; i++)
            {
                outp[dst + i * 2 + 1] = rom[src + i];            // odd bytes
                outp[dst + i * 2] = rom[src + 0x2000 + i];       // even bytes
            }
        }
        return outp;
    }

    private static string Region(string field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        var regions = new List<string>();
        foreach (char c in field)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'J': if (!regions.Contains("Japan")) regions.Add("Japan"); break;
                case 'U': if (!regions.Contains("USA")) regions.Add("USA"); break;
                case 'E': if (!regions.Contains("Europe")) regions.Add("Europe"); break;
            }
        }
        return string.Join(", ", regions);
    }
}
