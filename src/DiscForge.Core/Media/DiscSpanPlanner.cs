// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Media;

/// <summary>A target disc type for spanning, with its usable capacity in bytes.</summary>
public sealed record DiscMedium(string Key, string Name, long UsableBytes)
{
    private const long S = 2048;
    public static readonly DiscMedium Cd80  = new("cd",    "CD-R (80 min)",      BurnCapacity.Nominal.Cd80 * S);
    public static readonly DiscMedium Dvd5  = new("dvd5",  "DVD±R (4.7 GB)",     BurnCapacity.Nominal.Dvd5 * S);
    public static readonly DiscMedium Dvd9  = new("dvd9",  "DVD±R DL (8.5 GB)",  BurnCapacity.Nominal.Dvd9 * S);
    public static readonly DiscMedium Bd25  = new("bd25",  "BD-R (25 GB)",       BurnCapacity.Nominal.Bd25 * S);
    public static readonly DiscMedium Bd50  = new("bd50",  "BD-R DL (50 GB)",    BurnCapacity.Nominal.Bd50 * S);
    public static readonly DiscMedium Bd100 = new("bd100", "BD-R XL (100 GB)",   BurnCapacity.Nominal.Bd100 * S);

    public static readonly IReadOnlyList<DiscMedium> All = new[] { Cd80, Dvd5, Dvd9, Bd25, Bd50, Bd100 };

    public static DiscMedium? ByKey(string key)
    {
        key = key.Trim().ToLowerInvariant();
        foreach (var m in All) if (m.Key == key) return m;
        return null;
    }
}

/// <summary>A file to place on some disc. <see cref="Group"/> (e.g. its top-level folder) lets the
/// planner keep related files on the same disc when asked.</summary>
public sealed record SpanItem(string Path, long SizeBytes, string? Group = null);

/// <summary>One planned disc: which items land on it and how full it is.</summary>
public sealed record SpanDisc
{
    public required int Index { get; init; }               // 1-based
    public required IReadOnlyList<SpanItem> Items { get; init; }
    public required long UsedBytes { get; init; }          // payload after per-file rounding
    public required long CapacityBytes { get; init; }      // usable minus per-disc overhead
    public long FreeBytes => Math.Max(0, CapacityBytes - UsedBytes);
    public double FillPercent => CapacityBytes > 0 ? 100.0 * UsedBytes / CapacityBytes : 0;
}

/// <summary>The full spanning plan across N discs, plus any item too big for the chosen media.</summary>
public sealed record SpanPlan
{
    public required DiscMedium Medium { get; init; }
    public required IReadOnlyList<SpanDisc> Discs { get; init; }
    /// <summary>Items larger than a single disc's usable capacity — cannot be placed whole.</summary>
    public required IReadOnlyList<SpanItem> Oversized { get; init; }
    public required bool GroupsKept { get; init; }
    /// <summary>Groups that were too big to keep together and had to be split across discs.</summary>
    public required IReadOnlyList<string> SplitGroups { get; init; }

    public int DiscCount => Discs.Count;
    public long TotalPayloadBytes => Discs.Sum(d => d.UsedBytes);
    public double AverageFillPercent => Discs.Count > 0 ? Discs.Average(d => d.FillPercent) : 0;
}

/// <summary>
/// Plans how to split a set of files across the fewest optical discs — the "smart capacity
/// planning" that legacy burners never grew. Pure arithmetic (no hardware, fully testable): it
/// accounts for per-file 2048-byte sector rounding and a per-disc filesystem overhead, then packs
/// with First-Fit-Decreasing (a good, fast bin-packing approximation). With
/// <c>keepGroups</c> it keeps each group (e.g. a folder, a TV series) on one disc where it fits,
/// splitting only groups that are themselves larger than a disc.
/// </summary>
public static class DiscSpanPlanner
{
    private const long Sector = 2048;
    /// <summary>Default per-disc filesystem overhead (volume descriptors, path tables, root
    /// directory) — a conservative reservation so a "fits" plan really fits.</summary>
    public const long DefaultPerDiscOverheadBytes = 2 * 1024 * 1024;   // 2 MiB
    /// <summary>Default per-file overhead (directory record) beyond sector rounding.</summary>
    public const long DefaultPerFileOverheadBytes = 256;

    public static SpanPlan Plan(IEnumerable<SpanItem> items, DiscMedium medium,
                                bool keepGroups = false,
                                long? perDiscOverheadBytes = null,
                                long perFileOverheadBytes = DefaultPerFileOverheadBytes)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(medium);
        long overhead = perDiscOverheadBytes ?? DefaultPerDiscOverheadBytes;
        long capacity = medium.UsableBytes - overhead;
        if (capacity <= 0) throw new ArgumentException("Per-disc overhead exceeds the media capacity.");

        long Cost(SpanItem it) => RoundUp(it.SizeBytes, Sector) + perFileOverheadBytes;

        var all = items.ToList();
        var oversized = new List<SpanItem>();
        var splitGroups = new List<string>();

        // Units to pack: single files, or whole groups (when keeping groups and the group fits).
        var placeable = new List<SpanItem>();
        var groupUnits = new List<(string group, List<SpanItem> files, long cost)>();

        if (keepGroups)
        {
            foreach (var grp in all.Where(i => i.Group is not null).GroupBy(i => i.Group!))
            {
                long groupCost = grp.Sum(Cost);
                if (groupCost <= capacity)
                    groupUnits.Add((grp.Key, grp.ToList(), groupCost));
                else
                {
                    // Group can't stay whole — fall back to packing its files individually.
                    splitGroups.Add(grp.Key);
                    placeable.AddRange(grp);
                }
            }
            placeable.AddRange(all.Where(i => i.Group is null));
        }
        else
        {
            placeable.AddRange(all);
        }

        // First-Fit-Decreasing over (group-units + individual files), largest first.
        var units = new List<(long cost, List<SpanItem> files)>();
        units.AddRange(groupUnits.Select(g => (g.cost, g.files)));
        foreach (var f in placeable)
        {
            long c = Cost(f);
            if (c > capacity) { oversized.Add(f); continue; }
            units.Add((c, new List<SpanItem> { f }));
        }
        units.Sort((a, b) => b.cost.CompareTo(a.cost));

        var binItems = new List<List<SpanItem>>();
        var binUsed = new List<long>();
        foreach (var (cost, files) in units)
        {
            int target = -1;
            for (int i = 0; i < binUsed.Count; i++)
                if (binUsed[i] + cost <= capacity) { target = i; break; }
            if (target < 0)
            {
                binItems.Add(new List<SpanItem>());
                binUsed.Add(0);
                target = binItems.Count - 1;
            }
            binItems[target].AddRange(files);
            binUsed[target] += cost;
        }

        var discs = new List<SpanDisc>();
        for (int i = 0; i < binItems.Count; i++)
            discs.Add(new SpanDisc
            {
                Index = i + 1,
                Items = binItems[i],
                UsedBytes = binUsed[i],
                CapacityBytes = capacity,
            });

        return new SpanPlan
        {
            Medium = medium,
            Discs = discs,
            Oversized = oversized,
            GroupsKept = keepGroups,
            SplitGroups = splitGroups,
        };
    }

    private static long RoundUp(long value, long unit) => (value + unit - 1) / unit * unit;
}
