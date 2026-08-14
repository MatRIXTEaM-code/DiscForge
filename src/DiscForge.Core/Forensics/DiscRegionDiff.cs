// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text;
using DiscForge.Core.Preservation;

namespace DiscForge.Core.Forensics;

/// <summary>A contiguous region of one image whose content is absent from the other — a place the two
/// dumps genuinely diverge, in that image's own byte coordinates.</summary>
public sealed record DiffRegion(long Offset, long Length);

/// <summary>A structure-level comparison of two disc images: whether they are identical, and — when not —
/// where they diverge, expressed as regions rather than a byte-for-byte wall.</summary>
public sealed record DiscRegionDiffResult
{
    public required bool Identical { get; init; }
    public required long LengthA { get; init; }
    public required long LengthB { get; init; }
    public required int SharedChunks { get; init; }
    public required int ChunksOnlyInA { get; init; }
    public required int ChunksOnlyInB { get; init; }
    public required long ChangedBytesA { get; init; }
    public required long ChangedBytesB { get; init; }
    public required IReadOnlyList<DiffRegion> RegionsA { get; init; }
    public required IReadOnlyList<DiffRegion> RegionsB { get; init; }

    /// <summary>Fraction of image A (by bytes) that is shared with B — a similarity measure robust to
    /// insertions/deletions, because content-defined chunking realigns after a shift.</summary>
    public double SimilarityA => LengthA == 0 ? 1 : 1 - ChangedBytesA / (double)LengthA;

    public string Summary()
    {
        if (Identical) return "identical — the two images are byte-for-byte the same.";
        var sb = new StringBuilder(
            $"differ: {SharedChunks} shared chunk(s); A has {RegionsA.Count} changed region(s) " +
            $"({ChangedBytesA:N0} B), B has {RegionsB.Count} ({ChangedBytesB:N0} B); " +
            $"~{SimilarityA:P1} of A is shared.");
        foreach (var r in RegionsA.Take(12))
            sb.Append($"\n  A @ 0x{r.Offset:X}..0x{r.Offset + r.Length:X}  ({r.Length:N0} B)");
        if (RegionsA.Count > 12) sb.Append($"\n  … and {RegionsA.Count - 12} more region(s) in A.");
        return sb.ToString();
    }
}

/// <summary>
/// disc-semdiff — compare two disc images at the region level instead of the byte level. A byte diff of
/// two dumps is useless: scrambling and ECC turn one logical change into megabytes of churn, and a single
/// inserted sector shifts every following byte. This chunks both images with content-defined chunking (so
/// boundaries realign after an insertion), then reports which regions of each image are genuinely absent
/// from the other — "A and B share 98%; A differs in one 40 KB region near 0x1200000" — the answer a
/// preservationist actually wants when asking how two pressings, or an original and a re-dump, relate.
/// Comparison only; it reads and reports, and changes nothing.
/// </summary>
public static class DiscRegionDiff
{
    public static DiscRegionDiffResult Compare(byte[] a, byte[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.LongLength == b.LongLength && SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b)))
            return new DiscRegionDiffResult
            {
                Identical = true, LengthA = a.LongLength, LengthB = b.LongLength,
                SharedChunks = 0, ChunksOnlyInA = 0, ChunksOnlyInB = 0,
                ChangedBytesA = 0, ChangedBytesB = 0,
                RegionsA = Array.Empty<DiffRegion>(), RegionsB = Array.Empty<DiffRegion>(),
            };

        var ca = ContentChunking.Split(a);
        var cb = ContentChunking.Split(b);
        var setA = ca.Select(c => System.Convert.ToHexString(c.Sha256)).ToHashSet();
        var setB = cb.Select(c => System.Convert.ToHexString(c.Sha256)).ToHashSet();

        int shared = ca.Count(c => setB.Contains(System.Convert.ToHexString(c.Sha256)));
        var (regionsA, changedA) = DiffRegions(ca, setB);
        var (regionsB, changedB) = DiffRegions(cb, setA);

        return new DiscRegionDiffResult
        {
            Identical = false,
            LengthA = a.LongLength,
            LengthB = b.LongLength,
            SharedChunks = shared,
            ChunksOnlyInA = ca.Count - shared,
            ChunksOnlyInB = cb.Count(c => !setA.Contains(System.Convert.ToHexString(c.Sha256))),
            ChangedBytesA = changedA,
            ChangedBytesB = changedB,
            RegionsA = regionsA,
            RegionsB = regionsB,
        };
    }

    public static DiscRegionDiffResult CompareFiles(string pathA, string pathB)
    {
        ArgumentNullException.ThrowIfNull(pathA);
        ArgumentNullException.ThrowIfNull(pathB);
        return Compare(File.ReadAllBytes(pathA), File.ReadAllBytes(pathB));
    }

    // Merge the runs of chunks whose hash is absent from the other image into contiguous byte regions.
    private static (IReadOnlyList<DiffRegion>, long) DiffRegions(IReadOnlyList<Chunk> chunks, HashSet<string> other)
    {
        var regions = new List<DiffRegion>();
        long changed = 0;
        long runStart = -1, runEnd = -1;
        foreach (var c in chunks)
        {
            bool differs = !other.Contains(System.Convert.ToHexString(c.Sha256));
            if (differs)
            {
                changed += c.Length;
                if (runStart < 0) { runStart = c.Offset; runEnd = c.Offset + c.Length; }
                else if (c.Offset == runEnd) runEnd = c.Offset + c.Length;
                else { regions.Add(new DiffRegion(runStart, runEnd - runStart)); runStart = c.Offset; runEnd = c.Offset + c.Length; }
            }
            else if (runStart >= 0)
            {
                regions.Add(new DiffRegion(runStart, runEnd - runStart));
                runStart = -1;
            }
        }
        if (runStart >= 0) regions.Add(new DiffRegion(runStart, runEnd - runStart));
        return (regions, changed);
    }
}
