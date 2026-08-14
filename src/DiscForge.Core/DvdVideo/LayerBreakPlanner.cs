// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.DvdVideo;

/// <summary>
/// Recommends a DVD9 (dual-layer) LAYER-BREAK position — the authoring calculation ImgBurn is known
/// for, and the piece <c>dvd-layerbreak</c> (which only reads/verifies an existing break) was
/// missing. Given the real VOBU/cell boundary LBAs of a title (from <see cref="VtsVobuAdmap"/>) and
/// the disc's total sector count, it applies the physical constraints of an OTP (opposite-track-path)
/// dual-layer disc and picks a break that is BOTH legal and balanced:
///
/// <list type="bullet">
/// <item>The break must fall on a real VOBU boundary — a mid-VOBU break breaks seamless playback.</item>
/// <item>Layer 0 must be at least as long as layer 1 (<c>L0 ≥ L1</c>, i.e. break ≥ half the data):
/// on an OTP disc layer 1 is written outer-to-inner and cannot be longer than layer 0.</item>
/// <item>Layer 0 cannot exceed the physical layer-0 capacity of the media.</item>
/// </list>
///
/// Of the boundaries that satisfy all three, the recommended one is the closest to the half-way point
/// (the most balanced split, minimum layer-0 padding); the largest legal boundary is also reported as
/// the "fill layer 0" alternative. Pure and unit-tested; the CLI supplies the boundary LBAs and total.
/// </summary>
public static class LayerBreakPlanner
{
    /// <summary>Conventional maximum sectors on layer 0 of a DVD9. ECMA-267 dual-layer media carry
    /// ~2.08M user sectors per layer; this is the ceiling common tools use. It is a property of the
    /// media, not a hard fact of every disc, so callers may override it.</summary>
    public const long Dvd9MaxLayer0Sectors = 2_086_912;

    public sealed record Candidate(long Lba, long Layer0Sectors, long Layer1Sectors, double PercentOfTotal)
    {
        /// <summary>The break's distance (in sectors) from a perfectly balanced 50/50 split.</summary>
        public long SectorsFromMidpoint => Math.Abs(2 * Lba - (Layer0Sectors + Layer1Sectors));
    }

    public sealed record Plan
    {
        public required long TotalSectors { get; init; }
        public required long MinLayer0 { get; init; }   // ceil(total/2): the OTP L0 ≥ L1 floor
        public required long MaxLayer0 { get; init; }
        public required IReadOnlyList<Candidate> Candidates { get; init; }
        public Candidate? Recommended { get; init; }    // closest to the midpoint (balanced)
        public Candidate? MaxFill { get; init; }         // largest legal boundary (fills layer 0)
        public required string Summary { get; init; }
        public bool HasBreak => Recommended is not null;
    }

    /// <summary>
    /// Recommend a layer break from candidate boundary LBAs (absolute, in 2048-byte sectors) and the
    /// disc's total sector count. <paramref name="maxLayer0"/> caps layer 0 (default
    /// <see cref="Dvd9MaxLayer0Sectors"/>).
    /// </summary>
    public static Plan Recommend(IReadOnlyList<long> boundaryLbas, long totalSectors,
                                 long maxLayer0 = Dvd9MaxLayer0Sectors)
    {
        ArgumentNullException.ThrowIfNull(boundaryLbas);
        if (totalSectors <= 0)
            throw new ArgumentException("Total sectors must be positive.", nameof(totalSectors));

        long minL0 = (totalSectors + 1) / 2;                 // ceil(total/2) — OTP requires L0 ≥ L1
        long capL0 = Math.Min(maxLayer0, totalSectors);

        var valid = boundaryLbas
            .Where(lba => lba >= minL0 && lba <= capL0)
            .Distinct()
            .OrderBy(lba => lba)
            .Select(lba => new Candidate(lba, lba, totalSectors - lba, 100.0 * lba / totalSectors))
            .ToList();

        // Balanced = the smallest legal break (closest to the ≥half floor); fill = the largest.
        Candidate? recommended = valid.Count == 0 ? null
            : valid.OrderBy(c => c.SectorsFromMidpoint).ThenBy(c => c.Lba).First();
        Candidate? maxFill = valid.Count == 0 ? null : valid[^1];

        string summary = valid.Count == 0
            ? $"No VOBU boundary lies in the legal layer-break window [{minL0:N0}, {capL0:N0}] of " +
              $"{totalSectors:N0} sectors — the title would need a padding cell (as ImgBurn also warns). " +
              "Layer 0 must be ≥ half the data and ≤ the layer-0 capacity."
            : $"Recommended layer break at LBA {recommended!.Lba:N0} " +
              $"(layer 0 = {recommended.Layer0Sectors:N0} sectors, layer 1 = {recommended.Layer1Sectors:N0}, " +
              $"{recommended.PercentOfTotal:F1}% of the disc); {valid.Count} legal VOBU boundary(ies) available. " +
              "The physical break rounds to the drive's 16-sector ECC block.";

        return new Plan
        {
            TotalSectors = totalSectors,
            MinLayer0 = minL0,
            MaxLayer0 = capL0,
            Candidates = valid,
            Recommended = recommended,
            MaxFill = maxFill,
            Summary = summary,
        };
    }
}
