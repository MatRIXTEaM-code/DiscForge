// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;
using DiscForge.Core.Raw;
using DiscForge.Core.Recovery;

namespace DiscForge.Core.Forensics;

/// <summary>Per-sector health, ordered by severity so a block can be summarised by
/// its worst member. The byte values are stable (used in maps and legends).</summary>
public enum SectorHealth : byte
{
    /// <summary>A data sector whose EDC validates — provably intact.</summary>
    Good = 0,
    /// <summary>No EDC to check: CD-DA audio or Mode 2 Form 2.</summary>
    NoEcc = 1,
    /// <summary>Recovered by single-read Reed-Solomon ECC repair.</summary>
    EccRepaired = 2,
    /// <summary>Recovered by a cross-copy majority vote.</summary>
    Voted = 3,
    /// <summary>A data sector whose EDC fails — damaged.</summary>
    Damaged = 4,
    /// <summary>A damaged sector no route could recover.</summary>
    Unrecovered = 5,
}

/// <summary>
/// Renders a disc's per-sector health as an SVG you can actually look at — so the
/// shape of the damage is visible at a glance. Clustered red is physical rot or a
/// scratch (try to recover); a thin structured band that repeats is more likely a
/// deliberate protection pattern (preserve it, don't fight it). The map is built
/// either from a straight EDC scan of one image, or from the per-sector provenance a
/// <see cref="DumpReconstruct"/> emits — the same picture, coloured by how each
/// sector was saved.
/// </summary>
public static class DiscHealthMap
{
    private const int RawSectorSize = 2352;

    /// <summary>EDC-scan a raw image, returning one <see cref="SectorHealth"/> per sector.</summary>
    public static SectorHealth[] Scan(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length == 0 || image.Length % RawSectorSize != 0)
            throw new ArgumentException($"Image length {image.Length:N0} is not a whole number of {RawSectorSize}-byte sectors.");

        int sectors = image.Length / RawSectorSize;
        var health = new SectorHealth[sectors];
        for (int s = 0; s < sectors; s++)
        {
            var sec = image.AsSpan(s * RawSectorSize, RawSectorSize);
            bool? valid = Validate(sec);
            health[s] = valid switch
            {
                true => SectorHealth.Good,
                false => SectorHealth.Damaged,
                null => SectorHealth.NoEcc,
            };
        }
        return health;
    }

    /// <summary>Map a reconstruction provenance array (bytes of <see cref="SectorProvenance"/>)
    /// to health codes.</summary>
    public static SectorHealth[] FromProvenance(IReadOnlyList<byte> provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var health = new SectorHealth[provenance.Count];
        for (int i = 0; i < provenance.Count; i++)
            health[i] = ((SectorProvenance)provenance[i]) switch
            {
                SectorProvenance.Agreed => SectorHealth.Good,
                SectorProvenance.EdcVerifiedCopy => SectorHealth.Good,
                SectorProvenance.EccRepairedCopy => SectorHealth.EccRepaired,
                SectorProvenance.VoteEccRepaired => SectorHealth.EccRepaired,
                SectorProvenance.VoteVerified => SectorHealth.Voted,
                SectorProvenance.VoteBestEffort => SectorHealth.NoEcc,
                SectorProvenance.Unrecovered => SectorHealth.Unrecovered,
                _ => SectorHealth.NoEcc,
            };
        return health;
    }

    /// <summary>Render a health array to a standalone SVG document. When there are more
    /// sectors than <paramref name="maxCells"/>, adjacent sectors are grouped and the
    /// cell takes the group's <i>worst</i> health, so damage never hides.</summary>
    public static string RenderSvg(IReadOnlyList<SectorHealth> health, string title,
                                   int columns = 256, int cellPx = 4, int maxCells = 32768)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (columns < 1) columns = 1;
        if (cellPx < 1) cellPx = 1;
        if (maxCells < columns) maxCells = columns;

        int sectors = health.Count;
        int block = Math.Max(1, (sectors + maxCells - 1) / maxCells);
        int cells = (sectors + block - 1) / block;

        // Aggregate to cells (worst health per block) and tally the full totals.
        var cell = new SectorHealth[Math.Max(cells, 1)];
        var totals = new long[6];
        for (int s = 0; s < sectors; s++)
        {
            totals[(int)health[s]]++;
            int c = s / block;
            if (health[s] > cell[c]) cell[c] = health[s];
        }

        int rows = Math.Max(1, (cells + columns - 1) / columns);
        int gridW = columns * cellPx;
        int gridH = rows * cellPx;
        const int pad = 16, titleH = 28, legendH = 26;
        int width = gridW + pad * 2;
        int height = titleH + gridH + legendH + pad * 2;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" " +
            $"viewBox=\"0 0 {width} {height}\" font-family=\"system-ui,Segoe UI,sans-serif\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{width}\" height=\"{height}\" fill=\"#0f1115\"/>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{pad}\" y=\"20\" fill=\"#e6e6e6\" font-size=\"14\" font-weight=\"600\">{Escape(title)}</text>\n");

        int ox = pad, oy = titleH + pad;
        for (int c = 0; c < cells; c++)
        {
            int cx = ox + (c % columns) * cellPx;
            int cy = oy + (c / columns) * cellPx;
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{cx}\" y=\"{cy}\" width=\"{cellPx}\" height=\"{cellPx}\" fill=\"{Colour(cell[c])}\"/>\n");
        }

        // Legend: only the health classes actually present, with counts.
        int lx = pad, ly = oy + gridH + 18;
        foreach (SectorHealth h in Enum.GetValues<SectorHealth>())
        {
            if (totals[(int)h] == 0) continue;
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{lx}\" y=\"{ly - 10}\" width=\"11\" height=\"11\" fill=\"{Colour(h)}\"/>\n");
            string label = $"{Label(h)} {totals[(int)h]:N0}";
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{lx + 15}\" y=\"{ly}\" fill=\"#c8c8c8\" font-size=\"11\">{Escape(label)}</text>\n");
            lx += 18 + label.Length * 7;
        }

        if (block > 1)
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{pad}\" y=\"{height - 6}\" fill=\"#7a7a7a\" font-size=\"10\">" +
                $"each cell = {block:N0} sectors (worst shown) · {sectors:N0} sectors total</text>\n");

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    /// <summary>Convenience: EDC-scan an image and render it in one call.</summary>
    public static string RenderImage(byte[] image, string title, int columns = 256, int cellPx = 4)
        => RenderSvg(Scan(image), title, columns, cellPx);

    // ---- internals ----------------------------------------------------------

    private static string Colour(SectorHealth h) => h switch
    {
        SectorHealth.Good => "#2e7d32",         // green
        SectorHealth.NoEcc => "#546e7a",        // blue-grey (audio / no EDC)
        SectorHealth.EccRepaired => "#f9a825",  // amber (healed from parity)
        SectorHealth.Voted => "#ef6c00",        // orange (healed by vote)
        SectorHealth.Damaged => "#c62828",      // red
        SectorHealth.Unrecovered => "#b71c1c",  // deep red
        _ => "#37474f",
    };

    private static string Label(SectorHealth h) => h switch
    {
        SectorHealth.Good => "intact",
        SectorHealth.NoEcc => "audio/no-EDC",
        SectorHealth.EccRepaired => "ECC-repaired",
        SectorHealth.Voted => "vote-recovered",
        SectorHealth.Damaged => "damaged",
        SectorHealth.Unrecovered => "unrecovered",
        _ => "?",
    };

    private static bool? Validate(ReadOnlySpan<byte> sector)
    {
        if (sector.Length != RawSectorSize || !HasSync(sector)) return null;
        byte mode = sector[15];
        if (mode == 1) return EdcEcc.VerifyMode1(sector).EdcOk;
        if (mode == 2)
        {
            if ((sector[18] & 0x20) != 0) return null;
            return EdcEcc.VerifyMode2Form1(sector).EdcOk;
        }
        return null;
    }

    private static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0x00 || s[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
