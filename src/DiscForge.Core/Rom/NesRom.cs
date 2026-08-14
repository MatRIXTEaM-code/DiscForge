// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Rom;

/// <summary>
/// NES cartridge dump in the iNES / NES 2.0 container. The 16-byte header begins "NES\x1A".
/// PRG-ROM size is byte 4 × 16 KiB and CHR-ROM is byte 5 × 8 KiB; the mapper number is the low
/// nibble of byte 6 combined with the high nibble of byte 7. Mirroring (horizontal / vertical /
/// four-screen) and the trainer flag come from byte 6.
///
/// NES 2.0 is detected when <c>(byte7 &amp; 0x0C) == 0x08</c>; then byte 8 supplies mapper bits
/// 8..11 and byte 9 the high bits of the PRG/CHR sizes. The iNES header itself is part of the
/// file and — unlike an SNES copier header — is INCLUDED in No-Intro hashes.
/// </summary>
public static class NesRom
{
    public static RomId? TryRead(byte[] rom)
    {
        if (rom.Length < 16) return null;
        if (!(rom[0] == 0x4E && rom[1] == 0x45 && rom[2] == 0x53 && rom[3] == 0x1A)) return null;

        byte b4 = rom[4], b5 = rom[5], b6 = rom[6], b7 = rom[7];
        bool nes20 = (b7 & 0x0C) == 0x08;

        int mapper = (b6 >> 4) | (b7 & 0xF0);
        long prg = b4 * 16L * 1024;
        long chr = b5 * 8L * 1024;

        if (nes20)
        {
            mapper |= (rom[8] & 0x0F) << 8;                 // mapper bits 8..11
            int prgHi = rom[9] & 0x0F, chrHi = (rom[9] >> 4) & 0x0F;
            prg = ((prgHi << 8) | b4) * 16L * 1024;
            chr = ((chrHi << 8) | b5) * 8L * 1024;
        }

        string mirroring = (b6 & 0x08) != 0 ? "four-screen"
                          : (b6 & 0x01) != 0 ? "vertical" : "horizontal";
        bool trainer = (b6 & 0x04) != 0;
        bool battery = (b6 & 0x02) != 0;

        var extra = new Dictionary<string, string>
        {
            ["Container"] = nes20 ? "NES 2.0" : "iNES",
            ["Mapper"] = mapper.ToString(),
            ["PrgRom"] = $"{prg / 1024} KiB",
            ["ChrRom"] = chr == 0 ? "0 (CHR-RAM)" : $"{chr / 1024} KiB",
            ["Mirroring"] = mirroring,
            ["Trainer"] = trainer ? "present (512 bytes)" : "none",
            ["Battery"] = battery ? "yes" : "no",
        };

        return new RomId
        {
            Platform = "NES / Famicom",
            Title = "",                                     // iNES carries no title field
            Extra = extra,
        };
    }
}
