// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Recovery;

namespace DiscForge.Core.Forensics;

/// <summary>A sector range claimed by some structure of the image.</summary>
public sealed record CoverageRegion(long StartSector, long SectorCount, string Owner)
{
    public long EndSector => StartSector + SectorCount;   // exclusive
}

/// <summary>A run of sectors no structure accounts for — a silent gap.</summary>
public sealed record CoverageGap(long StartSector, long SectorCount)
{
    public long EndSector => StartSector + SectorCount;
    public override string ToString() =>
        SectorCount == 1 ? $"sector {StartSector}" : $"sectors {StartSector}–{EndSector - 1} (×{SectorCount})";
}

/// <summary>A run of sectors two structures both claim — a conflict (corruption or a mastering bug).</summary>
public sealed record CoverageOverlap(long StartSector, long SectorCount, string OwnerA, string OwnerB)
{
    public override string ToString() =>
        $"sectors {StartSector}–{StartSector + SectorCount - 1}: {OwnerA} vs {OwnerB}";
}

/// <summary>The coverage proof: whether the claimed regions PARTITION the image (no gaps, no overlaps).</summary>
public sealed record CoverageProof
{
    public required long TotalSectors { get; init; }
    public required IReadOnlyList<CoverageRegion> Regions { get; init; }
    public required IReadOnlyList<CoverageGap> Gaps { get; init; }
    public required IReadOnlyList<CoverageOverlap> Overlaps { get; init; }

    public long GapSectors => Gaps.Sum(g => g.SectorCount);
    public long AccountedSectors => TotalSectors - GapSectors;

    /// <summary>The image is fully accounted for iff every sector is claimed exactly once:
    /// no gaps AND no overlaps AND the accounted count equals the total.</summary>
    public bool Complete => Gaps.Count == 0 && Overlaps.Count == 0 && AccountedSectors == TotalSectors;

    public string Summary =>
        Complete
            ? $"COMPLETE — all {TotalSectors:N0} sector(s) are accounted for exactly once ({Regions.Count:N0} region(s), no gaps, no overlaps)."
            : $"INCOMPLETE — {AccountedSectors:N0}/{TotalSectors:N0} sector(s) accounted; " +
              $"{Gaps.Count:N0} gap(s) ({GapSectors:N0} sector(s)), {Overlaps.Count:N0} overlap(s).";
}

/// <summary>
/// coverage-proof — a formal check that every addressable sector of an image is accounted for exactly once.
/// Where <see cref="DumpCompleteness"/> reconciles COUNTS (cue vs bin vs subchannel) and SectorMatterMap
/// classifies what each block is MADE of, this proves a stronger, structural property: the regions the image's
/// structures claim must PARTITION [0, N) — no silent gap that belongs to nothing (an unresolved directory, a
/// hidden extent, trailing slack no descriptor mentions) and no OVERLAP where two structures claim the same
/// sector (a mastering bug or corruption). It reports every gap and every conflict, and passes only when the
/// coverage is an exact partition. Read-only analysis; it changes nothing.
/// </summary>
public static class PhysicalCoverage
{
    /// <summary>Prove that <paramref name="claimed"/> regions partition [0, <paramref name="totalSectors"/>).
    /// Detects both gaps (unclaimed runs) and overlaps (doubly-claimed runs).</summary>
    public static CoverageProof Prove(long totalSectors, IEnumerable<CoverageRegion> claimed)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        if (totalSectors < 0) throw new ArgumentOutOfRangeException(nameof(totalSectors));

        var regions = claimed
            .Where(r => r.SectorCount > 0)
            .OrderBy(r => r.StartSector).ThenBy(r => r.EndSector)
            .ToList();

        var gaps = new List<CoverageGap>();
        var overlaps = new List<CoverageOverlap>();

        long reached = 0;                 // highest sector covered so far (exclusive)
        string reachedOwner = "(start)";
        foreach (var r in regions)
        {
            long s = Math.Max(0, r.StartSector);
            long e = Math.Min(totalSectors, r.EndSector);
            if (e <= 0 || s >= totalSectors) continue;    // wholly outside the image

            if (s > reached)
                gaps.Add(new CoverageGap(reached, s - reached));      // unclaimed run before this region
            else if (s < reached)
            {
                long overlapEnd = Math.Min(e, reached);
                if (overlapEnd > s)
                    overlaps.Add(new CoverageOverlap(s, overlapEnd - s, reachedOwner, r.Owner));
            }

            if (e > reached) { reached = e; reachedOwner = r.Owner; }
        }
        if (reached < totalSectors)
            gaps.Add(new CoverageGap(reached, totalSectors - reached));   // trailing unclaimed slack

        return new CoverageProof
        {
            TotalSectors = totalSectors,
            Regions = regions,
            Gaps = gaps,
            Overlaps = overlaps,
        };
    }

    /// <summary>Prove coverage for an ISO 9660 image, deriving the claimed regions from its structure
    /// (system area, descriptors/path tables, directories, files, and reconstructable free tail). Any
    /// sector the classifier could not place (e.g. an unresolved secondary-namespace directory) shows up
    /// as a gap — which is the point.</summary>
    public static CoverageProof OfIso(byte[] image, int sectorSize = 2048)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length % sectorSize != 0)
            throw new ArgumentException("Image length is not a whole number of sectors.");
        long total = image.Length / sectorSize;
        var (roles, labels) = FilesystemConstrainedRecovery.BuildIsoMap(image, sectorSize);
        return Prove(total, RegionsFromRoles(roles, labels));
    }

    /// <summary>Coalesce a per-sector role/label map into contiguous claimed regions. Unknown-role
    /// sectors are deliberately left unclaimed so the proof surfaces them as gaps.</summary>
    public static IEnumerable<CoverageRegion> RegionsFromRoles(FsRole[] roles, string[] labels)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(labels);
        var regions = new List<CoverageRegion>();
        long i = 0;
        while (i < roles.Length)
        {
            if (roles[i] == FsRole.Unknown) { i++; continue; }        // leave unclaimed → a gap
            long start = i;
            string owner = OwnerOf(roles[i], labels[i]);
            while (i < roles.Length && roles[i] != FsRole.Unknown && OwnerOf(roles[i], labels[i]) == owner) i++;
            regions.Add(new CoverageRegion(start, i - start, owner));
        }
        return regions;
    }

    public static string Render(CoverageProof p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var sb = new StringBuilder(p.Summary);
        foreach (var g in p.Gaps.Take(20)) sb.Append($"\n  gap: {g}");
        foreach (var o in p.Overlaps.Take(20)) sb.Append($"\n  overlap: {o}");
        return sb.ToString();
    }

    private static string OwnerOf(FsRole role, string label) => role switch
    {
        FsRole.System => "system area",
        FsRole.FreeSpace => "free space",
        _ => string.IsNullOrEmpty(label) ? role.ToString() : label,
    };
}
