// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using DiscForge.Core.Cue;
using DiscForge.Core.Preservation;

namespace DiscForge.Core.Collection;

/// <summary>One incomplete (or complete) copy considered for salvage.</summary>
public sealed record SalvageCopy
{
    public required string Name { get; init; }
    public required string RelPath { get; init; }
    public required int TotalSectors { get; init; }
    /// <summary>Absolute LBAs this copy could not read. Empty for a clean copy.</summary>
    public required IReadOnlyList<long> Holes { get; init; }
    public int HoleCount => Holes.Count;
}

/// <summary>A set of copies believed to be the same disc, and whether merging them yields a complete image.</summary>
public sealed record SalvageGroup
{
    public required string TitleKey { get; init; }
    public required int TotalSectors { get; init; }
    public required IReadOnlyList<SalvageCopy> Copies { get; init; }

    /// <summary>A copy that already reads clean, if any — no salvage needed.</summary>
    public string? CompleteCopy { get; init; }
    /// <summary>The fewest holes any single copy has (the best starting point).</summary>
    public required int BestSingleHoles { get; init; }
    /// <summary>Sectors unreadable in EVERY copy — the intersection of all hole sets; these cannot be recovered by merging.</summary>
    public required int UnrecoverableSectors { get; init; }
    /// <summary>Sectors that at least one copy holed but merging recovers.</summary>
    public required int RecoveredBySalvage { get; init; }

    public bool FullySalvageable => CompleteCopy is null && UnrecoverableSectors == 0 && RecoveredBySalvage > 0;
    public bool HasOpportunity => CompleteCopy is null && Copies.Count >= 2 && RecoveredBySalvage > 0;

    public string Recommendation()
    {
        if (CompleteCopy is not null)
            return $"A complete copy already exists ({CompleteCopy}); no salvage needed.";
        if (Copies.Count < 2)
            return "Only one copy — a second dump (another drive / another disc) is needed to salvage the holes.";
        if (FullySalvageable)
            return $"FULLY SALVAGEABLE — merging these {Copies.Count} copies fills every hole. " +
                   $"Combine their equal-length raw images with merge-cert: {string.Join(", ", Copies.Select(c => c.RelPath))}";
        if (RecoveredBySalvage > 0)
            return $"PARTIAL — merging recovers {RecoveredBySalvage:N0} sector(s), but {UnrecoverableSectors:N0} remain " +
                   "unreadable in every copy; another dump is still needed for those.";
        return $"No gain from merging — the same {UnrecoverableSectors:N0} sector(s) are holed in all copies.";
    }
}

public sealed record SalvageReport
{
    public required string Folder { get; init; }
    public required IReadOnlyList<SalvageGroup> Groups { get; init; }

    public int Opportunities => Groups.Count(g => g.HasOpportunity);
    public int FullySalvageable => Groups.Count(g => g.FullySalvageable);

    public string Summary()
    {
        if (Groups.Count == 0) return "No same-title groups of incomplete dumps found.";
        return $"{Groups.Count} title group(s) with an incomplete copy: {FullySalvageable} fully salvageable by merging, " +
               $"{Opportunities - FullySalvageable} partially.";
    }
}

/// <summary>
/// salvage-plan — find where several unreadable dumps can rescue each other. A preservationist often has two or
/// three copies of a disc that each fail to read, but in different places; merged, their good sectors can form a
/// complete image. Nothing surfaces that opportunity today. This groups a collection's dumps by title (matching
/// disc geometry plus a boot-area anchor), intersects each group's unreadable-sector maps, and reports whether a
/// merge would fill every hole, fill some, or none — with the exact <c>merge-cert</c> command to run. It reads
/// the bad-sector maps and disc layout only; it moves and merges nothing itself.
/// </summary>
public static class SalvagePlanner
{
    private const int CdSector = 2352;

    public static SalvageReport Analyze(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"'{folder}' is not a folder.");

        var copies = new List<(string key, SalvageCopy copy)>();
        foreach (var cue in Directory.EnumerateFiles(folder, "*.cue", SearchOption.AllDirectories).OrderBy(p => p))
        {
            var c = FromCue(cue, folder);
            if (c is not null) copies.Add(c.Value);
        }

        var groups = new List<SalvageGroup>();
        foreach (var g in copies.GroupBy(x => x.key))
        {
            var members = g.Select(x => x.copy).OrderBy(c => c.HoleCount).ToList();
            // Only interesting when at least one copy has holes.
            if (members.All(m => m.HoleCount == 0)) continue;

            string? complete = members.FirstOrDefault(m => m.HoleCount == 0)?.Name;
            int bestSingle = members.Min(m => m.HoleCount);

            // Intersection of hole sets = sectors no copy could read; union that at least one holed = salvage set.
            HashSet<long>? intersection = null;
            var holedCopies = members.Where(m => m.HoleCount > 0).ToList();
            foreach (var m in holedCopies)
            {
                var set = new HashSet<long>(m.Holes);
                if (intersection is null) intersection = set;
                else intersection.IntersectWith(set);
            }
            int unrec = complete is not null ? 0 : (intersection?.Count ?? 0);

            // Sectors recovered by merging = (holes in the best single copy) that are NOT unreadable-in-all.
            // With a complete copy present, salvage is moot.
            int recovered;
            if (complete is not null) recovered = 0;
            else
            {
                var bestCopy = holedCopies.OrderBy(m => m.HoleCount).First();
                var bestHoles = new HashSet<long>(bestCopy.Holes);
                bestHoles.ExceptWith(intersection ?? new HashSet<long>());
                recovered = bestHoles.Count;
            }

            groups.Add(new SalvageGroup
            {
                TitleKey = g.Key,
                TotalSectors = members[0].TotalSectors,
                Copies = members,
                CompleteCopy = complete,
                BestSingleHoles = bestSingle,
                UnrecoverableSectors = unrec,
                RecoveredBySalvage = recovered,
            });
        }

        // Most actionable first: fully salvageable, then partial, then the rest.
        groups = groups
            .OrderByDescending(g => g.FullySalvageable)
            .ThenByDescending(g => g.RecoveredBySalvage)
            .ThenBy(g => g.TitleKey, StringComparer.Ordinal)
            .ToList();
        return new SalvageReport { Folder = folder, Groups = groups };
    }

    /// <summary>Build a salvage copy from a cue: its total sector count, a boot-area anchor, and its hole map.</summary>
    private static (string key, SalvageCopy copy)? FromCue(string cuePath, string root)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cuePath))!;
            var sheet = CueSheet.Parse(File.ReadAllText(cuePath));
            var bins = sheet.Tracks.Select(t => t.File).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct()
                .Select(f => Path.Combine(dir, f)).Where(File.Exists).ToList();
            if (bins.Count == 0) return null;

            long totalBytes = bins.Sum(b => new FileInfo(b).Length);
            int totalSectors = (int)(totalBytes / CdSector);

            // Title key: disc geometry + a hash of the first 64 KiB of the first bin (the boot area, normally
            // intact). Two dumps of the same title share both; unrelated same-size discs almost never do.
            string anchor = BootAnchor(bins[0]);
            string key = $"{totalSectors}:{anchor}";

            var holes = new List<long>();
            var sidecar = BadSectorMap.SidecarPath(cuePath);
            if (File.Exists(sidecar))
            {
                try
                {
                    var bad = BadSectorMap.Load(sidecar);
                    // Only genuine damage counts as a hole to salvage; boundary padding is not lost data.
                    var boundary = new HashSet<long>(bad.BoundaryLba);
                    holes = bad.UnreadableLba.Where(l => !boundary.Contains(l)).ToList();
                }
                catch { /* unreadable sidecar → treat as clean, best-effort */ }
            }

            return (key, new SalvageCopy
            {
                Name = Path.GetFileName(cuePath),
                RelPath = Path.GetRelativePath(root, cuePath),
                TotalSectors = totalSectors,
                Holes = holes,
            });
        }
        catch { return null; }
    }

    private static string BootAnchor(string binPath)
    {
        using var fs = File.OpenRead(binPath);
        int n = (int)Math.Min(65536, fs.Length);
        var buf = new byte[n];
        fs.ReadExactly(buf, 0, n);
        return System.Convert.ToHexString(SHA256.HashData(buf))[..16].ToLowerInvariant();
    }
}
