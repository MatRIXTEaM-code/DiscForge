// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Packing;

/// <summary>A file waiting to be archived.</summary>
public sealed record PackItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required long Bytes { get; init; }
    /// <summary>Files that must travel together — a folder, an album, a set of
    /// related documents. Null means the file may go anywhere.</summary>
    public string? Group { get; init; }
}

/// <summary>One disc's worth of files.</summary>
public sealed record PackedDisc
{
    public required int Number { get; init; }
    public required IReadOnlyList<PackItem> Items { get; init; }
    public required long CapacityBytes { get; init; }

    public long UsedBytes => Items.Sum(i => i.Bytes);
    public long FreeBytes => CapacityBytes - UsedBytes;
    public double FillFraction => CapacityBytes == 0 ? 0 : (double)UsedBytes / CapacityBytes;
}

public sealed record PackResult
{
    public required IReadOnlyList<PackedDisc> Discs { get; init; }
    /// <summary>Files too large for any single disc.</summary>
    public required IReadOnlyList<PackItem> Oversized { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public long TotalBytes => Discs.Sum(d => d.UsedBytes);
    public long WastedBytes => Discs.Sum(d => d.FreeBytes);
    public double AverageFill => Discs.Count == 0 ? 0 : Discs.Average(d => d.FillFraction);
}

/// <summary>Common blank media capacities, in bytes of usable data.</summary>
public static class DiscCapacity
{
    /// <summary>
    /// Usable capacity, not the marketing figure. A "700 MB" CD-R holds 333,000
    /// sectors of 2048 bytes — 681 MiB — and a "4.7 GB" DVD holds 4.377 GiB.
    /// Packing to the marketing number produces a set of discs that don't fit,
    /// which is the failure this whole exercise exists to avoid.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, long Bytes)> Common = new[]
    {
        ("CD-R 650 MB", 681_984_000L),        // 333,000 sectors × 2048
        ("CD-R 700 MB", 737_280_000L),        // 360,000 sectors × 2048
        ("CD-R 800 MB", 829_440_000L),        // 405,000 sectors × 2048
        ("DVD±R 4.7 GB", 4_700_372_992L),
        ("DVD±R DL 8.5 GB", 8_547_991_552L),
        ("BD-R 25 GB", 25_025_314_816L),
        ("BD-R DL 50 GB", 50_050_629_632L),
    };

    /// <summary>
    /// Space a filesystem costs before any content is written: volume
    /// descriptors, path tables, directory records. Varies with the number of
    /// files and the depth of the tree, so this is a working allowance rather
    /// than an exact figure — but ignoring it entirely is how a disc that
    /// "just fits" turns out not to.
    /// </summary>
    public static long FilesystemOverhead(int fileCount, int directoryCount) =>
        // 16 empty sectors, volume descriptors, path tables, then roughly a
        // sector per 20 directory entries.
        (16 + 8) * 2048L
        + Math.Max(1, (fileCount + directoryCount) / 20) * 2048L
        + directoryCount * 2048L;
}

/// <summary>
/// Works out which files go on which disc so as to waste the least space.
///
/// This is bin packing, which is NP-hard in general — there is no method that
/// always finds the best answer without trying every arrangement. But the
/// first-fit-decreasing heuristic gets within about 22% of optimal in the worst
/// case and is usually far closer, which for filling discs is more than enough:
/// nobody minds the last disc being 3% emptier than it theoretically could be.
///
/// The order matters more than the cleverness. Placing large files first leaves
/// small ones to fill the gaps; placing them last leaves large files with
/// nowhere to go and starts a new disc for each. That single decision accounts
/// for most of the difference between a good packing and a poor one.
///
/// Groups complicate it: files that must travel together are packed as a unit,
/// which can force a new disc where individual files would have fitted. That is
/// usually what people want — an album split across two discs is worse than a
/// disc that is 80% full.
/// </summary>
public static class DiscPacker
{
    public sealed record Options
    {
        public required long CapacityBytes { get; init; }
        /// <summary>Reserve room for the filesystem's own structures.</summary>
        public bool AccountForOverhead { get; init; } = true;
        /// <summary>Keep grouped files on one disc, even at the cost of space.</summary>
        public bool RespectGroups { get; init; } = true;
        /// <summary>Extra bytes to hold back per disc — for a readme, a
        /// checksum file, or simply not filling media to the last sector.</summary>
        public long ReserveBytes { get; init; }
    }

    public static PackResult Pack(IReadOnlyList<PackItem> items, Options options)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        if (options.CapacityBytes <= 0)
            throw new ArgumentException("Capacity must be positive.", nameof(options));

        var notes = new List<string>();
        long capacity = options.CapacityBytes - options.ReserveBytes;

        if (capacity <= 0)
            throw new ArgumentException(
                "The reserve leaves no room for content.", nameof(options));

        // Group first: a set that must stay together is packed as one unit, and
        // its size is the sum of its parts.
        var units = options.RespectGroups
            ? BuildGroupedUnits(items)
            : items.Select(i => new Unit(i.Group ?? i.Path, new[] { i }, i.Bytes)).ToList();

        // Anything that cannot fit alone is hopeless, and saying so up front is
        // kinder than silently omitting it.
        var oversized = new List<PackItem>();
        var packable = new List<Unit>();
        foreach (var u in units)
        {
            long need = u.Bytes + (options.AccountForOverhead
                ? DiscCapacity.FilesystemOverhead(u.Items.Count, CountDirectories(u.Items))
                : 0);

            if (need > capacity)
            {
                oversized.AddRange(u.Items);
                if (u.Items.Count > 1)
                    notes.Add($"The group '{u.Key}' totals {Format(u.Bytes)}, which exceeds one " +
                              "disc. Untick \"keep groups together\" to split it, or use larger media.");
            }
            else
            {
                packable.Add(u);
            }
        }

        // First-fit decreasing: place the largest unit that hasn't been placed
        // into the first disc it fits, opening a new disc only when none does.
        packable.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        var bins = new List<List<Unit>>();
        var used = new List<long>();

        foreach (var u in packable)
        {
            int chosen = -1;
            for (int i = 0; i < bins.Count; i++)
            {
                long overhead = options.AccountForOverhead
                    ? DiscCapacity.FilesystemOverhead(
                        bins[i].Sum(x => x.Items.Count) + u.Items.Count,
                        bins[i].Count + 1)
                    : 0;

                if (used[i] + u.Bytes + overhead <= capacity)
                {
                    chosen = i;
                    break;
                }
            }

            if (chosen < 0)
            {
                bins.Add(new List<Unit> { u });
                used.Add(u.Bytes);
            }
            else
            {
                bins[chosen].Add(u);
                used[chosen] += u.Bytes;
            }
        }

        var discs = new List<PackedDisc>();
        for (int i = 0; i < bins.Count; i++)
        {
            var contents = bins[i]
                .SelectMany(u => u.Items)
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            discs.Add(new PackedDisc
            {
                Number = i + 1,
                Items = contents,
                CapacityBytes = options.CapacityBytes,
            });
        }

        if (discs.Count > 1)
        {
            var last = discs[^1];
            if (last.FillFraction < 0.25)
                notes.Add($"Disc {last.Number} is only {last.FillFraction:P0} full. That is " +
                          "usually unavoidable — the remainder has to go somewhere — but a " +
                          "smaller disc for the last one would waste less.");
        }

        return new PackResult { Discs = discs, Oversized = oversized, Notes = notes };
    }

    /// <summary>A file or a set of files that must stay together.</summary>
    private sealed record Unit(string Key, IReadOnlyList<PackItem> Items, long Bytes);

    private static List<Unit> BuildGroupedUnits(IReadOnlyList<PackItem> items)
    {
        var units = new List<Unit>();

        foreach (var group in items.Where(i => i.Group is not null)
                                   .GroupBy(i => i.Group!, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            units.Add(new Unit(group.Key, list, list.Sum(i => i.Bytes)));
        }

        foreach (var loose in items.Where(i => i.Group is null))
            units.Add(new Unit(loose.Path, new[] { loose }, loose.Bytes));

        return units;
    }

    private static int CountDirectories(IReadOnlyList<PackItem> items) =>
        items.Select(i => Path.GetDirectoryName(i.Path) ?? "")
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .Count();

    public static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):N2} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
        >= 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes} bytes",
    };
}