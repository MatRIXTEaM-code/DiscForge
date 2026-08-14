// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Mmc;

/// <summary>
/// The DVD "book type" (disc category) 4-bit code carried in a disc's physical
/// format information. "Bitsetting" is changing this field on a recordable disc
/// — most usefully to <see cref="DvdRom"/> so a fussy set-top player treats a
/// DVD+R as a pressed DVD-ROM. These codes are public (DVD spec / MMC physical
/// format info); DiscForge uses them only to *read* the current book type and to
/// *label* a bitsetting command learned from a drive trace — it never fabricates
/// the vendor command that sets them.
/// </summary>
public enum BookType
{
    DvdRom = 0x0,
    DvdRam = 0x1,
    DvdR = 0x2,
    DvdRw = 0x3,
    HdDvdRom = 0x4,
    HdDvdRam = 0x5,
    HdDvdR = 0x6,
    HdDvdRw = 0x7,
    DvdPlusRw = 0x9,
    DvdPlusR = 0xA,
    DvdPlusRwDl = 0xD,
    DvdPlusRDl = 0xE,
    Unknown = 0xFF,
}

public static class BookTypes
{
    public static BookType FromNibble(int code) => (code & 0x0F) switch
    {
        0x0 => BookType.DvdRom,
        0x1 => BookType.DvdRam,
        0x2 => BookType.DvdR,
        0x3 => BookType.DvdRw,
        0x4 => BookType.HdDvdRom,
        0x5 => BookType.HdDvdRam,
        0x6 => BookType.HdDvdR,
        0x7 => BookType.HdDvdRw,
        0x9 => BookType.DvdPlusRw,
        0xA => BookType.DvdPlusR,
        0xD => BookType.DvdPlusRwDl,
        0xE => BookType.DvdPlusRDl,
        _ => BookType.Unknown,
    };

    public static string Name(this BookType b) => b switch
    {
        BookType.DvdRom => "DVD-ROM",
        BookType.DvdRam => "DVD-RAM",
        BookType.DvdR => "DVD-R",
        BookType.DvdRw => "DVD-RW",
        BookType.HdDvdRom => "HD DVD-ROM",
        BookType.HdDvdRam => "HD DVD-RAM",
        BookType.HdDvdR => "HD DVD-R",
        BookType.HdDvdRw => "HD DVD-RW",
        BookType.DvdPlusRw => "DVD+RW",
        BookType.DvdPlusR => "DVD+R",
        BookType.DvdPlusRwDl => "DVD+RW DL",
        BookType.DvdPlusRDl => "DVD+R DL",
        _ => "unknown",
    };

    /// <summary>Parse a friendly name/code the CLI accepts (e.g. "DVD-ROM", "+R", "0xA").</summary>
    public static BookType? Parse(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int hex))
            return FromNibble(hex);
        return s.ToUpperInvariant().Replace("-", "").Replace("_", "").Replace(" ", "") switch
        {
            "DVDROM" or "ROM" => BookType.DvdRom,
            "DVDRAM" => BookType.DvdRam,
            "DVDR" => BookType.DvdR,
            "DVDRW" => BookType.DvdRw,
            "DVD+RW" or "+RW" or "PLUSRW" => BookType.DvdPlusRw,
            "DVD+R" or "+R" or "PLUSR" => BookType.DvdPlusR,
            "DVD+RWDL" or "+RWDL" => BookType.DvdPlusRwDl,
            "DVD+RDL" or "+RDL" or "PLUSRDL" => BookType.DvdPlusRDl,
            _ => null,
        };
    }
}
