// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

using Evidence = DiscMri.Evidence;

public class DiscMriRereadPlanTests
{
    private static Evidence[] Ev(params Evidence[] e) => e;

    [Fact]
    public void An_all_clean_disc_has_nothing_to_reread()
    {
        var evidence = Ev(Evidence.DataGood, Evidence.DataGood, Evidence.Audio, Evidence.AudioSilence,
                          Evidence.Boundary, Evidence.NoEdc);
        var plan = DiscMri.PlanReread(evidence);
        Assert.True(plan.Nothing);
        Assert.Empty(plan.Ranges);
        Assert.Equal(0, plan.SuggestedPasses);
    }

    [Fact]
    public void Boundary_sectors_are_geometry_not_damage_and_are_left_alone()
    {
        // Boundary sits between EdcFailed's would-be neighbours in severity but is explicitly excluded.
        var evidence = Ev(Evidence.DataGood, Evidence.Boundary, Evidence.DataGood);
        var plan = DiscMri.PlanReread(evidence);
        Assert.True(plan.Nothing);
    }

    [Fact]
    public void A_single_damaged_sector_becomes_a_padded_range()
    {
        var evidence = new Evidence[10];
        Array.Fill(evidence, Evidence.DataGood);
        evidence[5] = Evidence.EdcFailed;

        var plan = DiscMri.PlanReread(evidence, padSectors: 2);

        Assert.False(plan.Nothing);
        var range = Assert.Single(plan.Ranges);
        Assert.Equal(3, range.StartSector);   // 5 - 2
        Assert.Equal(5, range.Count);         // sectors 3..7 inclusive
        Assert.Equal(Evidence.EdcFailed, range.Worst);
        Assert.Equal(3, plan.SuggestedPasses);
    }

    [Fact]
    public void Padding_clamps_at_the_start_and_end_of_the_disc()
    {
        var evidence = new Evidence[20];
        Array.Fill(evidence, Evidence.DataGood);
        evidence[0] = Evidence.Unreadable;
        evidence[19] = Evidence.Unreadable;

        var plan = DiscMri.PlanReread(evidence, padSectors: 2);

        Assert.Equal(2, plan.Ranges.Count);
        Assert.Equal(0, plan.Ranges[0].StartSector);              // clamped, not -2
        Assert.True(plan.Ranges[1].StartSector + plan.Ranges[1].Count <= 20);   // clamped, not past the end
    }

    [Fact]
    public void Adjacent_damaged_runs_merge_once_padding_makes_them_touch()
    {
        var evidence = new Evidence[20];
        Array.Fill(evidence, Evidence.DataGood);
        evidence[5] = Evidence.EdcFailed;
        evidence[9] = Evidence.EdcFailed;   // 4 sectors away — padding of 2 each side bridges the gap

        var plan = DiscMri.PlanReread(evidence, padSectors: 2);

        Assert.Single(plan.Ranges);
    }

    [Fact]
    public void Distant_damaged_runs_stay_separate_ranges()
    {
        var evidence = new Evidence[30];
        Array.Fill(evidence, Evidence.DataGood);
        evidence[2] = Evidence.EdcFailed;
        evidence[27] = Evidence.EdcFailed;

        var plan = DiscMri.PlanReread(evidence, padSectors: 2);

        Assert.Equal(2, plan.Ranges.Count);
    }

    [Theory]
    [InlineData(Evidence.EdcFailed, 3)]
    [InlineData(Evidence.SynclessVoid, 5)]
    [InlineData(Evidence.Unreadable, 7)]
    public void Pass_count_escalates_with_evidence_severity(Evidence worst, int expectedPasses)
    {
        var evidence = new[] { Evidence.DataGood, worst, Evidence.DataGood };
        var plan = DiscMri.PlanReread(evidence, padSectors: 0);
        Assert.Equal(expectedPasses, plan.SuggestedPasses);
    }

    [Fact]
    public void The_worst_evidence_in_a_merged_range_drives_the_whole_plans_escalation()
    {
        var evidence = new Evidence[10];
        Array.Fill(evidence, Evidence.DataGood);
        evidence[2] = Evidence.EdcFailed;      // mild
        evidence[7] = Evidence.Unreadable;     // severe, in a different range

        var plan = DiscMri.PlanReread(evidence, padSectors: 1);

        // Two separate ranges, but the plan's overall strategy escalates to the worst seen anywhere.
        Assert.Equal(2, plan.Ranges.Count);
        Assert.Equal(7, plan.SuggestedPasses);
        Assert.Contains("Unreadable", plan.Strategy);
    }

    [Fact]
    public void NeedsReread_matches_disc_mri_cmds_own_damage_threshold()
    {
        // disc-mri's CLI counts damage as >= EdcFailed; PlanReread must agree, or the two would disagree
        // about whether a disc needs attention.
        foreach (Evidence e in Enum.GetValues<Evidence>())
            Assert.Equal(e >= Evidence.EdcFailed, DiscMri.NeedsReread(e));
    }
}
