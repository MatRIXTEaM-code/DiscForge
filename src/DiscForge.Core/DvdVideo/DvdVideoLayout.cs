// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;

namespace DiscForge.Core.DvdVideo;

/// <summary>What a file in a VIDEO_TS folder is.</summary>
public enum DvdVideoRole
{
    /// <summary>VIDEO_TS.IFO — the Video Manager control file.</summary>
    VmgIfo,
    /// <summary>VIDEO_TS.VOB — the Video Manager menu (optional).</summary>
    VmgMenu,
    /// <summary>VIDEO_TS.BUP — the Video Manager backup control file.</summary>
    VmgBup,
    /// <summary>VTS_nn_0.IFO — a Video Title Set control file.</summary>
    VtsIfo,
    /// <summary>VTS_nn_0.VOB — a title set's menu (optional).</summary>
    VtsMenu,
    /// <summary>VTS_nn_1.VOB … VTS_nn_9.VOB — a title set's video.</summary>
    VtsTitle,
    /// <summary>VTS_nn_0.BUP — a title set's backup control file.</summary>
    VtsBup,
    /// <summary>A file that doesn't belong in a DVD-Video VIDEO_TS folder.</summary>
    Unknown,
}

/// <summary>One classified VIDEO_TS file.</summary>
public sealed record DvdVideoFile
{
    public required string Name { get; init; }
    public required DvdVideoRole Role { get; init; }
    /// <summary>Title-set number 1–99 (0 for VMG / unknown).</summary>
    public required int TitleSet { get; init; }
    /// <summary>The _n part: 0 = control/menu, 1–9 = title VOB (−1 for VMG/unknown).</summary>
    public required int Part { get; init; }
    public required long Size { get; init; }
}

/// <summary>The result of planning a VIDEO_TS folder into a burnable layout.</summary>
public sealed record DvdVideoPlan
{
    /// <summary>Files in the exact on-disc order a DVD-Video disc requires.</summary>
    public required IReadOnlyList<DvdVideoFile> OrderedFiles { get; init; }
    /// <summary>Title-set numbers found, ascending.</summary>
    public required IReadOnlyList<int> TitleSets { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    /// <summary>Fatal problems that make the folder non-conformant (empty if OK).</summary>
    public required IReadOnlyList<string> Errors { get; init; }
    public bool IsValid => Errors.Count == 0;
    public long TotalBytes => OrderedFiles.Sum(f => f.Size);
}

/// <summary>
/// Plans a DVD-Video <c>VIDEO_TS</c> folder into the exact file order a conformant
/// disc requires, and validates the set — pure logic over filenames + sizes, no I/O.
///
/// DVD-Video mandates a strict on-disc order (ECMA-267 / the DVD-Video spec): the
/// Video Manager first (VIDEO_TS.IFO, its optional menu VOB, then VIDEO_TS.BUP),
/// followed by each Video Title Set 01..99 in turn — VTS_nn_0.IFO, the optional menu
/// VTS_nn_0.VOB, the title VOBs VTS_nn_1.VOB..VTS_nn_9.VOB, then VTS_nn_0.BUP. The
/// control IFO leads and its backup BUP trails, separated by the VOBs, so a surface
/// defect can't take out both. This planner produces exactly that order; an assembler
/// then feeds it to the ISO+UDF bridge builder.
/// </summary>
public static class DvdVideoLayout
{
    /// <summary>Classify one VIDEO_TS filename (case-insensitive).</summary>
    public static DvdVideoFile Classify(string name, long size)
    {
        ArgumentNullException.ThrowIfNull(name);
        string up = name.ToUpperInvariant();

        DvdVideoRole role = DvdVideoRole.Unknown;
        int ts = 0, part = -1;

        if (up == "VIDEO_TS.IFO") role = DvdVideoRole.VmgIfo;
        else if (up == "VIDEO_TS.VOB") role = DvdVideoRole.VmgMenu;
        else if (up == "VIDEO_TS.BUP") role = DvdVideoRole.VmgBup;
        else if (up.StartsWith("VTS_", StringComparison.Ordinal) && up.Length == 12 && up[6] == '_')
        {
            // VTS_nn_p.EXT  →  positions 4-5 = nn, 7 = p, 8 = '.', 9-11 = EXT
            if (int.TryParse(up.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int nn)
                && int.TryParse(up.AsSpan(7, 1), NumberStyles.None, CultureInfo.InvariantCulture, out int p)
                && up[8] == '.')
            {
                string ext = up[9..];
                ts = nn; part = p;
                role = ext switch
                {
                    "IFO" when p == 0 => DvdVideoRole.VtsIfo,
                    "BUP" when p == 0 => DvdVideoRole.VtsBup,
                    "VOB" when p == 0 => DvdVideoRole.VtsMenu,
                    "VOB" when p is >= 1 and <= 9 => DvdVideoRole.VtsTitle,
                    _ => DvdVideoRole.Unknown,
                };
            }
        }

        if (role == DvdVideoRole.Unknown) { ts = 0; part = -1; }
        return new DvdVideoFile { Name = name, Role = role, TitleSet = ts, Part = part, Size = size };
    }

    /// <summary>Plan the on-disc order and validate a set of VIDEO_TS files.</summary>
    public static DvdVideoPlan Plan(IEnumerable<(string Name, long Size)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var classified = files.Select(f => Classify(f.Name, f.Size)).ToList();
        var warnings = new List<string>();
        var errors = new List<string>();

        foreach (var f in classified.Where(f => f.Role == DvdVideoRole.Unknown))
            warnings.Add($"'{f.Name}' is not a DVD-Video file and will be skipped.");

        var known = classified.Where(f => f.Role != DvdVideoRole.Unknown).ToList();

        // The Video Manager: VIDEO_TS.IFO is mandatory; BUP strongly recommended.
        var vmgIfo = known.FirstOrDefault(f => f.Role == DvdVideoRole.VmgIfo);
        var vmgMenu = known.FirstOrDefault(f => f.Role == DvdVideoRole.VmgMenu);
        var vmgBup = known.FirstOrDefault(f => f.Role == DvdVideoRole.VmgBup);
        if (vmgIfo is null) errors.Add("VIDEO_TS.IFO is missing — not a DVD-Video folder.");
        if (vmgBup is null) warnings.Add("VIDEO_TS.BUP is missing (the DVD-Video spec requires a backup IFO).");

        var titleSets = known.Where(f => f.TitleSet > 0).Select(f => f.TitleSet).Distinct().OrderBy(n => n).ToList();
        if (titleSets.Count == 0) errors.Add("No Video Title Sets (VTS_nn_*.*) found.");

        var ordered = new List<DvdVideoFile>();
        if (vmgIfo is not null) ordered.Add(vmgIfo);
        if (vmgMenu is not null) ordered.Add(vmgMenu);
        if (vmgBup is not null) ordered.Add(vmgBup);

        foreach (int ts in titleSets)
        {
            var set = known.Where(f => f.TitleSet == ts).ToList();
            var ifo = set.FirstOrDefault(f => f.Role == DvdVideoRole.VtsIfo);
            var menu = set.FirstOrDefault(f => f.Role == DvdVideoRole.VtsMenu);
            var bup = set.FirstOrDefault(f => f.Role == DvdVideoRole.VtsBup);
            var titles = set.Where(f => f.Role == DvdVideoRole.VtsTitle).OrderBy(f => f.Part).ToList();

            if (ifo is null) errors.Add($"VTS_{ts:D2}_0.IFO is missing for title set {ts}.");
            if (bup is null) warnings.Add($"VTS_{ts:D2}_0.BUP is missing for title set {ts}.");
            if (titles.Count == 0) warnings.Add($"Title set {ts} has no title VOBs (VTS_{ts:D2}_1.VOB…).");

            // Title VOBs must be contiguous from 1 (a gap breaks the IFO's cell pointers).
            for (int i = 0; i < titles.Count; i++)
                if (titles[i].Part != i + 1)
                {
                    errors.Add($"Title set {ts} VOBs are not contiguous from 1 " +
                               $"(found _{titles[i].Part} where _{i + 1} was expected).");
                    break;
                }

            if (ifo is not null) ordered.Add(ifo);
            if (menu is not null) ordered.Add(menu);
            ordered.AddRange(titles);
            if (bup is not null) ordered.Add(bup);
        }

        // A DVD-Video VOB may not exceed 1 GB (1,073,741,824 bytes) — the classic ceiling.
        foreach (var v in ordered.Where(f => f.Role is DvdVideoRole.VtsTitle or DvdVideoRole.VtsMenu or DvdVideoRole.VmgMenu))
            if (v.Size > 1_073_741_824L)
                errors.Add($"'{v.Name}' is {v.Size:N0} bytes — a DVD-Video VOB may not exceed 1 GB.");

        return new DvdVideoPlan
        {
            OrderedFiles = ordered,
            TitleSets = titleSets,
            Warnings = warnings,
            Errors = errors,
        };
    }
}
