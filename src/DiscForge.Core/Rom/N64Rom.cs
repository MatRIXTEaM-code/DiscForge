// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Rom;

/// <summary>
/// Nintendo 64 cartridge header. A dump can be in one of three byte orders, told apart by the
/// first four bytes of the ROM:
///
///   80 37 12 40  big-endian, native (.z64)
///   37 80 40 12  byte-swapped every 16-bit word (.v64)
///   40 12 37 80  little-endian, every 32-bit word reversed (.n64)
///
/// The reader normalises the header to big-endian in memory, then reads the fixed fields:
/// the 20-byte internal name at 0x20 (latin-1), the 4-byte game code at 0x3B, the country
/// byte at 0x3E (region), and CRC1/CRC2 (the boot checksums) at 0x10/0x14. The detected byte
/// order is reported in <see cref="RomId.Extra"/>.
/// </summary>
public static class N64Rom
{
    private enum Order { BigEndian, ByteSwapped, LittleEndian }

    public static RomId? TryRead(byte[] rom)
    {
        if (rom.Length < 0x40) return null;
        Order? order = Detect(rom);
        if (order is null) return null;

        // Normalise just the header region (0x00..0x40) to big-endian. The swaps are local to
        // 2- or 4-byte groups aligned from offset 0, so the header can be normalised on its own.
        byte[] h = new byte[0x40];
        System.Array.Copy(rom, h, 0x40);
        Normalize(h, order.Value);

        string name = RomIdentify.Latin1(h.AsSpan(0x20, 20));
        string gameCode = RomIdentify.Ascii(h.AsSpan(0x3B, 4));
        char country = (char)h[0x3E];
        uint crc1 = RomIdentify.U32Be(h, 0x10);
        uint crc2 = RomIdentify.U32Be(h, 0x14);

        var extra = new Dictionary<string, string>
        {
            ["ByteOrder"] = order.Value switch
            {
                Order.BigEndian => "big-endian (.z64)",
                Order.ByteSwapped => "byte-swapped (.v64)",
                _ => "little-endian (.n64)",
            },
            ["CRC1"] = $"{crc1:X8}",
            ["CRC2"] = $"{crc2:X8}",
            ["CountryByte"] = $"0x{(byte)country:X2} '{country}'",
        };

        return new RomId
        {
            Platform = "Nintendo 64",
            Title = name,
            GameCode = gameCode,
            Region = Region(country),
            Extra = extra,
        };
    }

    private static Order? Detect(byte[] r)
    {
        if (r[0] == 0x80 && r[1] == 0x37 && r[2] == 0x12 && r[3] == 0x40) return Order.BigEndian;
        if (r[0] == 0x37 && r[1] == 0x80 && r[2] == 0x40 && r[3] == 0x12) return Order.ByteSwapped;
        if (r[0] == 0x40 && r[1] == 0x12 && r[2] == 0x37 && r[3] == 0x80) return Order.LittleEndian;
        return null;
    }

    private static void Normalize(byte[] h, Order order)
    {
        switch (order)
        {
            case Order.ByteSwapped: // swap each 16-bit word (AB CD -> BA DC)
                for (int i = 0; i + 1 < h.Length; i += 2)
                    (h[i], h[i + 1]) = (h[i + 1], h[i]);
                break;
            case Order.LittleEndian: // reverse each 32-bit word
                for (int i = 0; i + 3 < h.Length; i += 4)
                {
                    (h[i], h[i + 3]) = (h[i + 3], h[i]);
                    (h[i + 1], h[i + 2]) = (h[i + 2], h[i + 1]);
                }
                break;
        }
    }

    private static string Region(char country) => country switch
    {
        'E' => "USA",
        'J' => "Japan",
        'P' => "Europe",
        'D' => "Germany",
        'F' => "France",
        'I' => "Italy",
        'S' => "Spain",
        'U' => "Australia",
        'A' => "Asia (NTSC)",
        'B' => "Brazil",
        'C' => "China",
        'H' => "Netherlands",
        'K' => "Korea",
        'N' => "Canada",
        'X' or 'Y' => "Europe",
        '7' => "Beta",
        _ => "",
    };
}
