// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>
/// Renders a disc's <see cref="ScratchRecovery"/> verdict as an SVG you can look at — the sibling to
/// <see cref="DiscHealthMap"/>, but coloured by what can be <i>done</i> about each damaged region rather
/// than by raw EDC state. Where the health map paints "intact / damaged / repaired", this paints the
/// recovery outlook: an audio burst CIRC corrects outright is green, one interpolation only conceals is
/// amber, one beyond concealment (an audible dropout) is red; a data lesion — no concealment, ECC or
/// re-read — is orange; and a deliberate protection pattern is purple, flagged to preserve, never
/// "repair". Clean sectors stay dark, so the eye goes straight to the regions that need a decision. It
/// visualises the advisory models; it recovers nothing itself.
/// </summary>
public static class RecoveryMap
{
    /// <summary>Paint one outlook per sector from a recovery report; null means an undamaged sector.
    /// Where advisories overlap, the more attention-worthy outlook wins (see <see cref="Rank"/>).</summary>
    public static RecoveryOutlook?[] Paint(RecoveryReport report, int totalSectors)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (totalSectors < 0) throw new ArgumentOutOfRangeException(nameof(totalSectors));
        var cells = new RecoveryOutlook?[totalSectors];
        foreach (var a in report.Advisories)
        {
            int start = Math.Max(0, a.StartLba);
            int end = Math.Min(totalSectors - 1, a.EndLba);
            for (int s = start; s <= end; s++)
                if (cells[s] is not { } cur || Rank(a.Outlook) > Rank(cur))
                    cells[s] = a.Outlook;
        }
        return cells;
    }

    /// <summary>Render a recovery report over a disc of <paramref name="totalSectors"/> sectors to a
    /// standalone SVG. When there are more sectors than <paramref name="maxCells"/>, adjacent sectors are
    /// grouped and the cell takes the group's most attention-worthy outlook, so damage never hides.</summary>
    public static string RenderSvg(RecoveryReport report, int totalSectors, string title,
                                   int columns = 256, int cellPx = 4, int maxCells = 32768)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (totalSectors < 1) totalSectors = 1;
        if (columns < 1) columns = 1;
        if (cellPx < 1) cellPx = 1;
        if (maxCells < columns) maxCells = columns;

        var perSector = Paint(report, totalSectors);

        int block = Math.Max(1, (totalSectors + maxCells - 1) / maxCells);
        int cells = (totalSectors + block - 1) / block;

        // Aggregate to cells (most attention-worthy outlook per block) and tally per-region totals.
        var cell = new RecoveryOutlook?[Math.Max(cells, 1)];
        for (int s = 0; s < totalSectors; s++)
        {
            if (perSector[s] is not { } o) continue;
            int c = s / block;
            if (cell[c] is not { } cur || Rank(o) > Rank(cur)) cell[c] = o;
        }

        // Region counts for the legend (one per advisory, not per sector).
        var regionCounts = new Dictionary<RecoveryOutlook, int>();
        foreach (var a in report.Advisories)
            regionCounts[a.Outlook] = regionCounts.GetValueOrDefault(a.Outlook) + 1;

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

        // Legend: "clean", then only the outlooks actually present, each with its region count.
        int lx = pad, ly = oy + gridH + 18;
        void Chip(string colour, string label)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{lx}\" y=\"{ly - 10}\" width=\"11\" height=\"11\" fill=\"{colour}\"/>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{lx + 15}\" y=\"{ly}\" fill=\"#c8c8c8\" font-size=\"11\">{Escape(label)}</text>\n");
            lx += 18 + label.Length * 7;
        }
        Chip(Colour(null), "clean");
        foreach (RecoveryOutlook o in new[]
        {
            RecoveryOutlook.Corrected, RecoveryOutlook.Concealed, RecoveryOutlook.Lost,
            RecoveryOutlook.DataRecoverable, RecoveryOutlook.Preserve,
        })
        {
            if (!regionCounts.TryGetValue(o, out int n)) continue;
            Chip(Colour(o), $"{Label(o)} {n}");
        }

        if (block > 1)
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{pad}\" y=\"{height - 6}\" fill=\"#7a7a7a\" font-size=\"10\">" +
                $"each cell = {block:N0} sectors (worst shown) · {totalSectors:N0} sectors total</text>\n");

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    // ---- internals ----------------------------------------------------------

    /// <summary>Aggregation priority — a higher rank wins a shared cell, so the outlook that most
    /// demands attention is the one the eye lands on. An audible loss outranks everything; a deliberate
    /// pattern outranks the benign audio tiers so a protection band never hides behind a "corrected" cell.</summary>
    private static int Rank(RecoveryOutlook o) => o switch
    {
        RecoveryOutlook.Lost => 5,
        RecoveryOutlook.DataRecoverable => 4,
        RecoveryOutlook.Preserve => 3,
        RecoveryOutlook.Concealed => 2,
        RecoveryOutlook.Corrected => 1,
        _ => 0,
    };

    private static string Colour(RecoveryOutlook? o) => o switch
    {
        null => "#263238",                          // dark slate (clean)
        RecoveryOutlook.Corrected => "#2e7d32",     // green — CIRC corrects it, faithful
        RecoveryOutlook.Concealed => "#f9a825",     // amber — interpolation conceals it
        RecoveryOutlook.Lost => "#c62828",          // red — audible dropout, re-read
        RecoveryOutlook.DataRecoverable => "#ef6c00",// orange — ECC / re-read / reconstruct
        RecoveryOutlook.Preserve => "#6a1b9a",      // purple — deliberate, preserve verbatim
        _ => "#37474f",
    };

    private static string Label(RecoveryOutlook o) => o switch
    {
        RecoveryOutlook.Corrected => "corrected",
        RecoveryOutlook.Concealed => "concealed",
        RecoveryOutlook.Lost => "audibly-lost",
        RecoveryOutlook.DataRecoverable => "data-recover",
        RecoveryOutlook.Preserve => "preserve",
        _ => "?",
    };

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
