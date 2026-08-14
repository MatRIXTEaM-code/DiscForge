// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System;

namespace DiscForge.Core.Devices;

/// <summary>
/// Recognises the Hitachi-LG "GDR-816x" DVD-ROM family (GDR-8161B / 8162B / 8163B /
/// 8164B) from its INQUIRY strings — a pure mapping from INQUIRY vendor/product to a
/// verdict, so it can be unit-tested off-hardware.
///
/// This is used only to label a drive in the <c>raw-dump</c> diagnostic. DiscForge
/// reads raw sectors and reports them as-is; it does not descramble or decode
/// console (GameCube/Wii/GD-ROM) disc formats, and nothing here circumvents a
/// protection measure.
/// </summary>
public static class RawDumpDrive
{
    /// <summary>The Hitachi-LG vendor id these drives report in INQUIRY.</summary>
    public const string Vendor = "HL-DT-ST";

    /// <summary>
    /// True if the INQUIRY vendor + product identify a supported GDR-816x. Matches
    /// the family whether the product id carries the hyphen ("GDR-8164B") or not
    /// ("GDR8164B"), and is case- and whitespace-insensitive.
    /// </summary>
    public static bool IsSupported(string? vendor, string? product)
    {
        if (vendor is null || product is null) return false;
        bool hitachiLg = vendor.Trim().Equals(Vendor, StringComparison.OrdinalIgnoreCase);
        string p = product.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
        // GDR8161 / GDR8162 / GDR8163 / GDR8164 — the whole raw-dump-capable family.
        return hitachiLg && p.Contains("GDR816", StringComparison.Ordinal);
    }

    /// <summary>A one-line human verdict for a drive's INQUIRY strings.</summary>
    public static string Describe(string? vendor, string? product)
    {
        string id = $"{vendor?.Trim()} {product?.Trim()}".Trim();
        return IsSupported(vendor, product)
            ? $"{id} — recognised Hitachi-LG GDR-816x DVD-ROM."
            : $"{id} — not a recognised Hitachi-LG GDR-816x DVD-ROM.";
    }
}
