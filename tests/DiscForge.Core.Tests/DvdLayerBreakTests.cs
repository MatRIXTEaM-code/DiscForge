// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.DvdVideo;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The DVD9 layer-break recommender: read real VOBU boundaries from a VTS_VOBU_ADMAP, then pick a
/// legal, balanced break. The tests pin the constraints that make a break correct — it lands on a
/// VOBU boundary, layer 0 ≥ layer 1 (OTP), and layer 0 ≤ capacity — and that an impossible layout is
/// reported as "no break" rather than a bad guess.
/// </summary>
public class DvdLayerBreakTests
{
    private static void PutU32Be(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    [Fact]
    public void Vobu_admap_reads_the_title_vobu_start_sectors()
    {
        // VTSI_MAT sector 0 with the VOBU_ADMAP pointer (0xE4) → sector 1; three VOBUs there.
        var ifo = new byte[4096];
        PutU32Be(ifo, 0xE4, 1);                 // admap at sector 1
        int b = 2048;
        PutU32Be(ifo, b, 15);                   // end address: 4 header + 3×4 entries − 1
        PutU32Be(ifo, b + 4, 0);
        PutU32Be(ifo, b + 8, 500);
        PutU32Be(ifo, b + 12, 1000);

        var vobus = VtsVobuAdmap.ReadTitleVobuStarts(ifo);
        Assert.Equal(new uint[] { 0, 500, 1000 }, vobus);
    }

    [Fact]
    public void Vobu_admap_is_empty_when_the_pointer_is_zero()
        => Assert.Empty(VtsVobuAdmap.ReadTitleVobuStarts(new byte[4096]));

    [Fact]
    public void Recommends_the_balanced_break_on_a_vobu_boundary()
    {
        // Boundaries below half (200,480) and above capacity (1200) are illegal; of {520,600,900}
        // the most balanced (closest to the 500-sector midpoint) is 520.
        var plan = LayerBreakPlanner.Recommend(new long[] { 200, 480, 520, 600, 900, 1200 }, totalSectors: 1000);

        Assert.True(plan.HasBreak);
        Assert.Equal(520, plan.Recommended!.Lba);
        Assert.Equal(520, plan.Recommended.Layer0Sectors);
        Assert.Equal(480, plan.Recommended.Layer1Sectors);
        Assert.True(plan.Recommended.Layer0Sectors >= plan.Recommended.Layer1Sectors);  // OTP
        Assert.Equal(3, plan.Candidates.Count);
        Assert.All(plan.Candidates, c => Assert.InRange(c.Lba, 500, 1000));
        Assert.Equal(900, plan.MaxFill!.Lba);                                            // fill layer 0
    }

    [Fact]
    public void No_boundary_past_half_means_no_legal_break()
    {
        var plan = LayerBreakPlanner.Recommend(new long[] { 100, 200, 300 }, totalSectors: 1000);
        Assert.False(plan.HasBreak);
        Assert.Empty(plan.Candidates);
        Assert.Contains("padding cell", plan.Summary);
    }

    [Fact]
    public void Layer0_capacity_cap_is_honoured()
    {
        // Valid window becomes [500, 650]; only 600 qualifies.
        var plan = LayerBreakPlanner.Recommend(new long[] { 600, 700, 800 }, totalSectors: 1000, maxLayer0: 650);
        Assert.Single(plan.Candidates);
        Assert.Equal(600, plan.Recommended!.Lba);
    }
}
