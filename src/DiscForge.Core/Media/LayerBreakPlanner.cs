// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Media;

/// <summary>Options that shape where a dual-layer break is placed.</summary>
public sealed record LayerBreakOptions
{
    /// <summary>Preferred break sector. Null means "balance the layers" (disc middle).</summary>
    public long? TargetSector { get; init; }

    /// <summary>
    /// Maximum sectors a single layer can hold. Both layers must fit within it. The
    /// standard DVD±R DL usable capacity per layer is 2,086,912 sectors (≈4.27 GB
    /// across both); that is the default. Pass a drive/media-specific value to override.
    /// </summary>
    public long MaxLayerSectors { get; init; } = 2_086_912;

    /// <summary>
    /// Require the break to sit on a 16-sector ECC block boundary. DVD data is written
    /// in 16-sector ECC blocks, so a layer transition has to fall on one. On by default;
    /// only turn it off for analysis.
    /// </summary>
    public bool RequireEccAligned { get; init; } = true;

    /// <summary>
    /// Prefer L0 ≥ L1 (break at or past the midpoint), the usual DVD-Video convention so
    /// the outer layer is at least as long as the inner. When two candidates are
    /// equidistant from the target this decides the tie toward the larger-L0 side.
    /// </summary>
    public bool PreferLayer0AtLeastHalf { get; init; } = true;

    /// <summary>Mark the transition seamless in the authored navigation (DVD-Video).</summary>
    public bool Seamless { get; init; }
}

/// <summary>The chosen dual-layer break.</summary>
public sealed record LayerBreakPlan
{
    /// <summary>The sector at which layer 0 ends and layer 1 begins.</summary>
    public required long BreakSector { get; init; }
    /// <summary>Sectors on layer 0 (= <see cref="BreakSector"/>).</summary>
    public long Layer0Sectors => BreakSector;
    /// <summary>Sectors on layer 1.</summary>
    public required long Layer1Sectors { get; init; }
    /// <summary>True when the break fell exactly on the requested/ideal sector.</summary>
    public required bool ExactMatch { get; init; }
    /// <summary>True when the break came from a supplied cell/candidate boundary
    /// (DVD-Video), false when it's just the nearest legal ECC boundary (plain data DL).</summary>
    public required bool OnCandidateBoundary { get; init; }
    public required bool Seamless { get; init; }
}

/// <summary>Raised when no legal layer break exists for the image and options.</summary>
public sealed class LayerBreakException(string message) : Exception(message);

/// <summary>
/// Chooses the layer-break position for a dual-layer DVD burn — pure logic, no
/// hardware. Two modes:
///
///  • <b>DVD-Video</b>: given the legal cell/VOBU start sectors (candidate boundaries),
///    pick the one nearest the balance point so the layer transition lands on a real
///    navigation boundary (what ImgBurn's picker does).
///  • <b>Plain data DL</b>: no candidates — snap to the nearest 16-sector ECC boundary.
///
/// In both modes the break must leave both layers within a single layer's capacity.
/// This is the decision half; an engine performs the actual DL write.
/// </summary>
public static class LayerBreakPlanner
{
    /// <summary>Plan a break for a DVD-Video image from its legal cell-start sectors.</summary>
    public static LayerBreakPlan Pick(long totalSectors, IReadOnlyList<long> candidateBoundaries,
                                      LayerBreakOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(candidateBoundaries);
        options ??= new LayerBreakOptions();
        if (totalSectors <= 0) throw new ArgumentOutOfRangeException(nameof(totalSectors));

        long ideal = options.TargetSector ?? (totalSectors + 1) / 2;

        // A break is legal only if both layers fit within one layer's capacity.
        bool Fits(long b) => b > 0 && b < totalSectors
            && b <= options.MaxLayerSectors
            && (totalSectors - b) <= options.MaxLayerSectors;

        var legal = candidateBoundaries
            .Where(Fits)
            .Where(b => !options.RequireEccAligned || b % 16 == 0)
            .Distinct()
            .ToList();

        if (legal.Count > 0)
        {
            long best = ChooseNearest(legal, ideal, options.PreferLayer0AtLeastHalf);
            return new LayerBreakPlan
            {
                BreakSector = best,
                Layer1Sectors = totalSectors - best,
                ExactMatch = best == ideal,
                OnCandidateBoundary = true,
                Seamless = options.Seamless,
            };
        }

        // No usable cell boundary — fall back to the nearest legal ECC boundary
        // (correct for a plain data DL image, where any ECC boundary is a legal break).
        return SnapToEcc(totalSectors, ideal, options);
    }

    /// <summary>Plan a break for a plain data DL image (no navigation boundaries).</summary>
    public static LayerBreakPlan Pick(long totalSectors, LayerBreakOptions? options = null)
        => SnapToEcc(totalSectors, (options ?? new LayerBreakOptions()).TargetSector
                                    ?? (totalSectors + 1) / 2, options ?? new LayerBreakOptions());

    private static LayerBreakPlan SnapToEcc(long totalSectors, long ideal, LayerBreakOptions options)
    {
        // Round the ideal to a 16-sector boundary, then walk outward to the nearest
        // boundary that leaves both layers within capacity.
        long start = options.RequireEccAligned ? (ideal / 16) * 16 : ideal;
        bool Fits(long b) => b > 0 && b < totalSectors
            && b <= options.MaxLayerSectors && (totalSectors - b) <= options.MaxLayerSectors;

        int step = options.RequireEccAligned ? 16 : 1;
        for (long delta = 0; delta <= totalSectors; delta += step)
        {
            // Prefer the ≥-ideal side first when L0≥L1 is preferred.
            long hi = start + delta, lo = start - delta;
            long first = options.PreferLayer0AtLeastHalf ? hi : lo;
            long second = options.PreferLayer0AtLeastHalf ? lo : hi;
            if (Fits(first))
                return Result(first, totalSectors, ideal, options.Seamless);
            if (delta != 0 && Fits(second))
                return Result(second, totalSectors, ideal, options.Seamless);
        }
        throw new LayerBreakException(
            $"No legal layer break for {totalSectors:N0} sectors within a {options.MaxLayerSectors:N0}-sector " +
            "layer capacity — the image is too large for this dual-layer media.");
    }

    private static LayerBreakPlan Result(long b, long total, long ideal, bool seamless) => new()
    {
        BreakSector = b,
        Layer1Sectors = total - b,
        ExactMatch = b == ideal,
        OnCandidateBoundary = false,
        Seamless = seamless,
    };

    // Nearest candidate to the ideal; on a tie, favour the larger-L0 (≥ ideal) side
    // when requested, else the smaller sector for determinism.
    private static long ChooseNearest(List<long> legal, long ideal, bool preferLargeL0)
    {
        long best = legal[0];
        long bestDist = Math.Abs(best - ideal);
        foreach (long c in legal)
        {
            long d = Math.Abs(c - ideal);
            if (d < bestDist || (d == bestDist && Better(c, best, ideal, preferLargeL0)))
            {
                best = c;
                bestDist = d;
            }
        }
        return best;
    }

    private static bool Better(long c, long cur, long ideal, bool preferLargeL0)
    {
        if (preferLargeL0)
        {
            bool cGe = c >= ideal, curGe = cur >= ideal;
            if (cGe != curGe) return cGe;          // the ≥-ideal side wins the tie
        }
        return c < cur;                             // otherwise deterministic: smaller sector
    }
}
