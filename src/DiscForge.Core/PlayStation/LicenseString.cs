// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Files;

namespace DiscForge.Core.PlayStation;

/// <summary>Which regional license block sector 4 carries.</summary>
public enum LicenseRegion { Unknown, Japan, Europe, America }

/// <summary>What sector 4 of a PS1/PS2 disc's data track says about its region.</summary>
public sealed record LicenseStringResult
{
    public required LicenseRegion Region { get; init; }
    public required bool Line1Matches { get; init; }
    public required bool Line2Matches { get; init; }
    public required bool PaddingLooksStandard { get; init; }
    /// <summary>The decoded line-2 text, for display — even when it doesn't match anything known.</summary>
    public required string Line2Text { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }

    /// <summary>True when line 1, line 2 for the detected region, and the padding that follows
    /// all match what Sony's own mastering tools wrote.</summary>
    public bool WellFormed => Line1Matches && Line2Matches && PaddingLooksStandard && Region != LicenseRegion.Unknown;

    public string Summary() => Region == LicenseRegion.Unknown
        ? $"No recognised license text in sector {LicenseString.SectorIndex} — {string.Join("; ", Issues)}."
        : Line1Matches
            ? $"Standard {Region} license text (sector {LicenseString.SectorIndex})." +
              (PaddingLooksStandard ? "" : " Padding after the text is non-standard for this region.")
            : $"License text partially matches {Region} (line 2 correct, but {string.Join("; ", Issues)}).";
}

/// <summary>
/// license-check — read the fixed "Licensed by Sony Computer Entertainment..." text that Sony's
/// own mastering tools wrote into sector 4 of the data track on every first-party PS1/PS2 disc,
/// ahead of the ISO 9660 volume descriptors (which start at sector 16). It is a second, independent
/// region signal from SYSTEM.CNF: SYSTEM.CNF's BOOT path serial says what the publisher's
/// disc-authoring tools were told the region was, while this sector says what Sony's own mastering
/// process stamped into the boot area at glass-mastering time. A disc where the two disagree — a
/// reburned/rebuilt boot area, a mismatched bin/cue pairing, a relabelled image — is worth a second
/// look; that's the cross-check <see cref="CrossCheck"/> makes. Identification only: it reads a
/// fixed disc-format text field and compares it against another read-only source, the same as
/// SystemCnf's serial parsing.
///
/// SCOPE / HONESTY: the license-text layout (line lengths, byte offsets, and the three regional
/// strings) is documented on psx-spx (https://psx-spx.consoledev.net/cdromformat/, "Licence
/// String") and is corroborated internally — the documented offsets for line 1, line 2, and the
/// padding that follows sum to exactly 2048 bytes for both the Japan layout (32+33+1983) and the
/// Europe/America layout (32+38+1978), which would not hold if any of the three numbers were
/// copied wrong. Line 1 and each region's line 2 are checked exactly. The padding *content* after
/// line 2 (documented as all-zero for Europe/America, and a repeating fill pattern for Japan) is
/// checked too, but only informationally (<see cref="LicenseStringResult.PaddingLooksStandard"/>)
/// — that detail was not independently verified against a real disc dump this session, so a
/// mismatch is reported as a note, never as a reason to call the disc's region unrecognised.
/// </summary>
public static class LicenseString
{
    /// <summary>The sector (0-based, within the data track) that carries the license text.</summary>
    public const int SectorIndex = 4;
    private const int SectorBytes = 2048;

    private static readonly byte[] Line1 = Ascii(new string(' ', 10) + "Licensed" + new string(' ', 2) + "by" + new string(' ', 10));

    private static readonly byte[] Line2Japan = Concat(
        Ascii("Sony Computer Entertainment Inc."), new byte[] { 0x0A });
    private static readonly byte[] Line2Europe = Concat(
        Ascii("Sony Computer Entertainment Euro"), Ascii(" pe   "));
    private static readonly byte[] Line2America = Concat(
        Ascii("Sony Computer Entertainment Amer"), Ascii("  ica "));

    /// <summary>Parse an already-read 2048-byte sector 4 (Mode 2/Form 1 or Mode 1 user data —
    /// whichever layout the caller extracted).</summary>
    public static LicenseStringResult Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < SectorBytes)
            throw new ArgumentException($"Sector must be at least {SectorBytes} bytes.", nameof(sector));

        var issues = new List<string>();
        bool line1Ok = sector[..Line1.Length].SequenceEqual(Line1);
        if (!line1Ok) issues.Add("line 1 does not match the standard \"Licensed by\" text");

        (LicenseRegion region, byte[] bytes)[] candidates =
        {
            (LicenseRegion.Japan, Line2Japan),
            (LicenseRegion.Europe, Line2Europe),
            (LicenseRegion.America, Line2America),
        };

        LicenseRegion region = LicenseRegion.Unknown;
        bool line2Ok = false;
        bool paddingOk = false;
        string line2Text = Decode(sector.Slice(Line1.Length, Math.Min(38, SectorBytes - Line1.Length)));

        foreach (var (r, bytes) in candidates)
        {
            var slice = sector.Slice(Line1.Length, bytes.Length);
            if (!slice.SequenceEqual(bytes)) continue;
            region = r;
            line2Ok = true;
            line2Text = Decode(slice);
            var padding = sector[(Line1.Length + bytes.Length)..];
            paddingOk = PaddingLooksStandard(r, padding);
            break;
        }

        if (!line2Ok)
            issues.Add("line 2 does not match any known regional license text (Japan/Europe/America)");
        else if (!paddingOk)
            issues.Add($"padding after the license text is non-standard for {region} (informational — not independently verified)");

        return new LicenseStringResult
        {
            Region = region,
            Line1Matches = line1Ok,
            Line2Matches = line2Ok,
            PaddingLooksStandard = paddingOk,
            Line2Text = line2Text,
            Issues = issues,
        };
    }

    /// <summary>Read sector 4 out of a .cue/.bin (or bare .bin) image's data track and parse it.
    /// Returns null when the image has no data track, is too short, or can't be opened.</summary>
    public static LicenseStringResult? FromImage(string imagePath)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        RawTrackReader.Opened opened;
        try { opened = RawTrackReader.Open(imagePath); }
        catch { return null; }

        using (opened.Base)
        using (opened.View)
        {
            long offset = (long)SectorIndex * SectorBytes;
            if (opened.View.Length < offset + SectorBytes) return null;
            opened.View.Position = offset;

            var buf = new byte[SectorBytes];
            int total = 0;
            while (total < SectorBytes)
            {
                int n = opened.View.Read(buf, total, SectorBytes - total);
                if (n == 0) break;
                total += n;
            }
            if (total < SectorBytes) return null;
            return Parse(buf);
        }
    }

    /// <summary>Cross-check the license text's region against SYSTEM.CNF's serial-derived region
    /// (<see cref="SystemCnf.RegionOf"/>'s vocabulary). Returns null when they agree, or when the
    /// license text's region can't be mapped onto a SYSTEM.CNF region (Unknown, or a SYSTEM.CNF
    /// region — Korea/Asia/Unknown — that has no dedicated license block to compare against);
    /// otherwise a one-line description of the disagreement.</summary>
    public static string? CrossCheck(LicenseStringResult license, string systemCnfRegion)
    {
        ArgumentNullException.ThrowIfNull(systemCnfRegion);
        string? expected = license.Region switch
        {
            LicenseRegion.America => "USA (NTSC-U)",
            LicenseRegion.Europe => "Europe (PAL)",
            LicenseRegion.Japan => "Japan (NTSC-J)",
            _ => null,
        };
        if (expected is null) return null;
        // Only USA/Europe/Japan have a dedicated license block; Korea/Asia/Unknown discs are
        // known to reuse another region's block (commonly Japan's), so there is nothing of
        // their own to compare — don't report that as a disagreement.
        if (systemCnfRegion is not ("USA (NTSC-U)" or "Europe (PAL)" or "Japan (NTSC-J)")) return null;
        return string.Equals(expected, systemCnfRegion, StringComparison.Ordinal)
            ? null
            : $"Sector {SectorIndex}'s license text says {license.Region}, but SYSTEM.CNF's boot " +
              $"serial says {systemCnfRegion} — the boot area and the boot file disagree on region.";
    }

    // ---- padding (informational only — see the class-level SCOPE/HONESTY note) ----------------

    private static bool PaddingLooksStandard(LicenseRegion region, ReadOnlySpan<byte> padding)
    {
        if (region == LicenseRegion.Japan)
        {
            // Documented as a repeating 64-byte fill (62x0x30, 1x0x0A, 1x0x30) tiled across the
            // remaining space, truncated at the sector boundary. Checked loosely: the fill is
            // expected to be overwhelmingly 0x30 with occasional 0x0A, not mixed/random bytes.
            int other = 0;
            foreach (byte b in padding) if (b != 0x30 && b != 0x0A) other++;
            return padding.Length == 0 || other * 20 <= padding.Length; // allow up to 5% deviation
        }
        // Europe/America: documented as all 0x00.
        foreach (byte b in padding) if (b != 0x00) return false;
        return true;
    }

    private static string Decode(ReadOnlySpan<byte> s)
    {
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            byte b = s[i];
            chars[i] = b is >= 0x20 and < 0x7F ? (char)b : '.';
        }
        return new string(chars).TrimEnd();
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
