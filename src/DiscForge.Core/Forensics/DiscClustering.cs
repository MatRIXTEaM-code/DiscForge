// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Forensics;

/// <summary>A disc reduced to what clustering needs: its volume id and the set of files it
/// carries, by path and by content hash. Two variants of one title share most of both sets;
/// two unrelated titles share almost none.</summary>
public sealed record DiscProfile
{
    public required string Source { get; init; }
    public required string VolumeId { get; init; }
    public required int FileCount { get; init; }
    public required long TotalBytes { get; init; }

    /// <summary>Lower-cased file paths present on the disc.</summary>
    internal HashSet<string> Paths { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>SHA-256 (hex) of every file's content — the offset-independent identity of each file.</summary>
    internal HashSet<string> ContentHashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>How alike two discs are, and why.</summary>
public sealed record DiscSimilarity(
    string A, string B, double Score, double PathJaccard, double ContentJaccard, bool VolumeIdRelated);

/// <summary>A group of discs judged to be the same title (its regions, revisions, re-releases).</summary>
public sealed record DiscCluster
{
    public required IReadOnlyList<string> Members { get; init; }
    /// <summary>Best guess at the shared title, from the members' volume ids.</summary>
    public required string Label { get; init; }
    /// <summary>The weakest pairwise similarity inside the cluster (1.0 for a singleton).</summary>
    public required double Cohesion { get; init; }
    public required string Rationale { get; init; }

    public bool IsSingleton => Members.Count == 1;
}

/// <summary>The clustering of a folder of dumps.</summary>
public sealed record ClusterReport
{
    public required IReadOnlyList<DiscCluster> Clusters { get; init; }
    /// <summary>The above-threshold similarities that linked discs together.</summary>
    public required IReadOnlyList<DiscSimilarity> Links { get; init; }
    public required double Threshold { get; init; }

    public int GroupCount => Clusters.Count(c => !c.IsSingleton);
    public int LonerCount => Clusters.Count(c => c.IsSingleton);

    public string Summary()
    {
        int discs = Clusters.Sum(c => c.Members.Count);
        if (discs == 0) return "No discs to cluster.";
        return $"{discs} disc(s) → {GroupCount} group(s) of related discs and {LonerCount} standalone.";
    }
}

/// <summary>
/// DAT-less content clustering — take a messy folder of un-identified dumps and group the ones
/// that are the same title (its regions, revisions, budget re-releases) without any external
/// DAT/database. It compares discs by what they actually contain: the set of files by path (two
/// region variants keep the same directory layout and filenames) and by content hash (they share
/// most of the same file bytes, differing only in the localised pieces). Variants of one title
/// score high and link; unrelated titles share almost nothing and stay apart. Identification by
/// self-similarity, not by a reference list — offset-invariant, and it needs nothing but the discs.
/// </summary>
public static class DiscClustering
{
    private const int SectorSize = 2048;
    private const double PathWeight = 0.6;
    private const double ContentWeight = 0.4;
    private const double VolumeIdBoost = 0.10;
    public const double DefaultThreshold = 0.50;

    /// <summary>Profile a cooked ISO 9660 image. A non-ISO or unreadable image yields an empty
    /// profile (0 files), so it simply never links to anything.</summary>
    public static DiscProfile Profile(string source, byte[] isoImage)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(isoImage);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string volumeId = "";
        long totalBytes = 0;
        int fileCount = 0;

        try
        {
            IsoDirectory dir;
            using (var ms = new MemoryStream(isoImage, writable: false))
                dir = IsoReader.Read(ms);
            volumeId = dir.VolumeId ?? "";

            foreach (var f in dir.Files)
            {
                paths.Add(f.Path);
                fileCount++;
                long start = (long)f.Extent * SectorSize;
                long len = f.Size;
                totalBytes += len;
                if (start < 0 || len < 0 || start + len > isoImage.Length) continue;   // truncated — skip content
                hashes.Add(System.Convert.ToHexString(
                    SHA256.HashData(isoImage.AsSpan((int)start, (int)len))));
            }
        }
        catch
        {
            // Not a readable ISO — leave the profile empty.
        }

        return new DiscProfile
        {
            Source = source,
            VolumeId = volumeId,
            FileCount = fileCount,
            TotalBytes = totalBytes,
            Paths = paths,
            ContentHashes = hashes,
        };
    }

    /// <summary>Score how alike two discs are, in [0, 1].</summary>
    public static DiscSimilarity Similarity(DiscProfile a, DiscProfile b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        double pathJ = Jaccard(a.Paths, b.Paths);
        double contentJ = Jaccard(a.ContentHashes, b.ContentHashes);
        bool volRelated = VolumeIdRelated(a.VolumeId, b.VolumeId);

        double score = PathWeight * pathJ + ContentWeight * contentJ;
        if (volRelated) score = Math.Min(1.0, score + VolumeIdBoost);

        return new DiscSimilarity(a.Source, b.Source, score, pathJ, contentJ, volRelated);
    }

    /// <summary>Cluster the profiles: build the graph of above-threshold similarities and take its
    /// connected components. Single-linkage — a disc joins a group if it is similar enough to any
    /// member — with the weakest in-group link reported as the group's cohesion.</summary>
    public static ClusterReport Cluster(IReadOnlyList<DiscProfile> discs, double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(discs);
        int n = discs.Count;

        var uf = new int[n];
        for (int i = 0; i < n; i++) uf[i] = i;

        var links = new List<DiscSimilarity>();
        // All pairwise similarities, once each.
        var simOf = new Dictionary<(int, int), double>();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                var s = Similarity(discs[i], discs[j]);
                simOf[(i, j)] = s.Score;
                if (s.Score >= threshold)
                {
                    Union(uf, i, j);
                    links.Add(s);
                }
            }

        // Group indices by their representative.
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(uf, i);
            (groups.TryGetValue(r, out var list) ? list : groups[r] = new List<int>()).Add(i);
        }

        var clusters = new List<DiscCluster>();
        foreach (var g in groups.Values)
        {
            var members = g.Select(i => discs[i].Source).ToList();
            double cohesion = 1.0;
            if (g.Count > 1)
            {
                cohesion = double.MaxValue;
                for (int x = 0; x < g.Count; x++)
                    for (int y = x + 1; y < g.Count; y++)
                    {
                        int lo = Math.Min(g[x], g[y]), hi = Math.Max(g[x], g[y]);
                        cohesion = Math.Min(cohesion, simOf[(lo, hi)]);
                    }
            }

            clusters.Add(new DiscCluster
            {
                Members = members,
                Label = LabelFor(g.Select(i => discs[i]).ToList()),
                Cohesion = cohesion,
                Rationale = g.Count == 1
                    ? "Shares too little with any other disc to be a variant — standalone."
                    : $"{g.Count} discs share most of their files (weakest link {cohesion:P0}) — same title, different variants.",
            });
        }

        // Stable, useful ordering: real groups first (largest first), then loners; ties by label.
        clusters = clusters
            .OrderByDescending(c => c.Members.Count)
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ClusterReport
        {
            Clusters = clusters,
            Links = links.OrderByDescending(l => l.Score).ToList(),
            Threshold = threshold,
        };
    }

    public static string Render(ClusterReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        int gi = 0;
        foreach (var c in r.Clusters.Where(c => !c.IsSingleton))
        {
            sb.AppendLine($"  Group {++gi}: {c.Label}  (cohesion {c.Cohesion:P0})");
            foreach (var m in c.Members) sb.AppendLine($"    - {m}");
        }
        var loners = r.Clusters.Where(c => c.IsSingleton).SelectMany(c => c.Members).ToList();
        if (loners.Count > 0)
        {
            sb.AppendLine("  Standalone:");
            foreach (var m in loners) sb.AppendLine($"    - {m}");
        }
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0.0;
        int inter = 0;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        foreach (var x in small) if (large.Contains(x)) inter++;
        int union = a.Count + b.Count - inter;
        return union == 0 ? 0.0 : inter / (double)union;
    }

    // Two volume ids look related if one is a prefix of the other or they share a long common
    // prefix once stripped to alphanumerics — "SCES12345" vs "SCUS12345" won't, but "GT2" vs
    // "GT2 PLATINUM" and "TOMBRAIDER" vs "TOMBRAIDER2" will.
    private static bool VolumeIdRelated(string a, string b)
    {
        string na = Normalize(a), nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na == nb) return true;
        if (na.StartsWith(nb, StringComparison.Ordinal) || nb.StartsWith(na, StringComparison.Ordinal)) return true;

        int common = 0, min = Math.Min(na.Length, nb.Length);
        while (common < min && na[common] == nb[common]) common++;
        return common >= 4 && common >= (int)(0.6 * min);
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.ToUpperInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static string LabelFor(List<DiscProfile> members)
    {
        var ids = members.Select(m => m.VolumeId).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (ids.Count == 0) return "(untitled group)";

        // The most common volume id, if there's a clear winner.
        var top = ids.GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First();
        if (top.Count() > 1 || ids.Count == 1) return top.Key;

        // Otherwise the longest common prefix of the ids, if it's substantial.
        string prefix = ids.Aggregate(CommonPrefix);
        prefix = prefix.Trim();
        return prefix.Length >= 3 ? prefix + "…" : top.Key;
    }

    private static string CommonPrefix(string a, string b)
    {
        int i = 0, min = Math.Min(a.Length, b.Length);
        while (i < min && char.ToUpperInvariant(a[i]) == char.ToUpperInvariant(b[i])) i++;
        return a[..i];
    }

    private static int Find(int[] uf, int x)
    {
        while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; }
        return x;
    }

    private static void Union(int[] uf, int a, int b)
    {
        int ra = Find(uf, a), rb = Find(uf, b);
        if (ra != rb) uf[ra] = rb;
    }
}
