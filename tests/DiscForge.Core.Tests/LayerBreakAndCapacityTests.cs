// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Media;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The dual-layer break planner and the burn-capacity check — the pure decision logic
/// behind ImgBurn's layer-break picker and its overburn/underburn handling.
/// </summary>
public class LayerBreakAndCapacityTests
{
    // ---- layer break -------------------------------------------------------

    [Fact]
    public void Picks_the_cell_boundary_nearest_the_middle()
    {
        long total = 4_000_000;                         // ~ a DVD-9 image
        var cells = new long[] { 0, 1_000_000, 1_960_000, 2_040_000, 3_000_000 };
        var plan = LayerBreakPlanner.Pick(total, cells);

        // Ideal is 2,000,000; the nearest legal ECC-aligned cell is 2,040,000
        // (1,960,000 is equidistant-ish but 2,040,000 keeps L0 ≥ L1 by preference).
        Assert.True(plan.OnCandidateBoundary);
        Assert.Equal(2_040_000, plan.BreakSector);
        Assert.Equal(total - 2_040_000, plan.Layer1Sectors);
        Assert.True(plan.Layer0Sectors >= plan.Layer1Sectors);
    }

    [Fact]
    public void Rejects_cells_that_would_overflow_a_layer_and_uses_a_legal_one()
    {
        long total = 4_000_000;
        // Only two candidates; 100,000 would leave L1 = 3,900,000 > layer capacity.
        var cells = new long[] { 100_000, 2_000_000 };
        var plan = LayerBreakPlanner.Pick(total, cells);
        Assert.Equal(2_000_000, plan.BreakSector);       // the only one where both layers fit
        Assert.True(plan.ExactMatch);
    }

    [Fact]
    public void Falls_back_to_the_nearest_ecc_boundary_for_plain_data_dl()
    {
        long total = 4_000_000;
        var plan = LayerBreakPlanner.Pick(total);        // no candidates → data DL
        Assert.False(plan.OnCandidateBoundary);
        Assert.Equal(0, plan.BreakSector % 16);          // ECC-aligned
        Assert.Equal(2_000_000, plan.BreakSector);       // exact midpoint is already aligned
    }

    [Fact]
    public void Honours_an_explicit_target_and_the_seamless_flag()
    {
        // For a 4,000,000-sector image the legal break window is [1,913,088 .. 2,086,912]
        // (each layer must fit 2,086,912). A target biased toward the outer layer should
        // win over the midpoint-nearest candidate.
        long total = 4_000_000;
        var cells = new long[] { 1_960_000, 2_040_000, 2_080_000 };
        var plan = LayerBreakPlanner.Pick(total, cells,
            new LayerBreakOptions { TargetSector = 2_070_000, Seamless = true });
        Assert.Equal(2_080_000, plan.BreakSector);       // nearest cell to the 2.07M target
        Assert.True(plan.Seamless);
        Assert.True(plan.OnCandidateBoundary);
    }

    [Fact]
    public void An_image_too_big_for_dual_layer_is_refused()
    {
        // Both layers would each need > MaxLayerSectors.
        Assert.Throws<LayerBreakException>(() =>
            LayerBreakPlanner.Pick(5_000_000));          // 2×2.086M < 5M → impossible
    }

    // ---- capacity ----------------------------------------------------------

    [Fact]
    public void Underburn_fits_with_space_to_spare()
    {
        var c = BurnCapacity.Check(1_000_000, BurnCapacity.Nominal.Dvd5);
        Assert.Equal(CapacityFit.Underburn, c.Fit);
        Assert.True(c.CanBurn);
        Assert.True(c.FreeSectors > 0);
        Assert.Equal(0, c.OverburnSectors);
    }

    [Fact]
    public void Oversize_is_refused_unless_overburn_is_enabled()
    {
        long over = BurnCapacity.Nominal.Cd80 + 5_000;   // 5000 sectors past 700 MB

        var refused = BurnCapacity.Check(over, BurnCapacity.Nominal.Cd80, allowOverburn: false);
        Assert.False(refused.CanBurn);
        Assert.Equal(CapacityFit.Overburn, refused.Fit);   // within tolerance, just not enabled

        var allowed = BurnCapacity.Check(over, BurnCapacity.Nominal.Cd80, allowOverburn: true);
        Assert.True(allowed.CanBurn);
        Assert.Equal(CapacityFit.Overburn, allowed.Fit);
        Assert.Equal(5_000, allowed.OverburnSectors);
    }

    [Fact]
    public void Beyond_tolerance_is_too_large_even_with_overburn()
    {
        long wayOver = (long)(BurnCapacity.Nominal.Cd80 * 1.20);   // 20% over, tolerance 5%
        var c = BurnCapacity.Check(wayOver, BurnCapacity.Nominal.Cd80, allowOverburn: true);
        Assert.False(c.CanBurn);
        Assert.Equal(CapacityFit.TooLarge, c.Fit);
    }

    [Fact]
    public void An_exact_fill_reports_fits()
    {
        var c = BurnCapacity.Check(BurnCapacity.Nominal.Dvd5, BurnCapacity.Nominal.Dvd5);
        Assert.Equal(CapacityFit.Fits, c.Fit);
        Assert.True(c.CanBurn);
    }
}
