// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Audio;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The secure-rip grading and re-read planning. The key invariant pinned here: self-consistency can
/// never earn Verified — only an independent AccurateRip match can, because a drive that mis-reads
/// deterministically agrees with itself.
/// </summary>
public class SecureRipTests
{
    private static SecureRip.TrackEvidence Track(int sectors, int passes = 2,
        bool? ar = null, int arConf = 0, params (int at, SecureRip.SectorState s)[] marks)
    {
        var states = new byte[sectors];
        foreach (var (at, s) in marks) states[at] = (byte)s;
        return new SecureRip.TrackEvidence
        {
            Number = 1, Sectors = states, Passes = passes,
            AccurateRipMatch = ar, AccurateRipConfidence = arConf,
        };
    }

    [Fact]
    public void An_accuraterip_match_with_clean_sectors_is_verified()
    {
        var v = SecureRip.Grade(Track(1000, ar: true, arConf: 7));
        Assert.Equal(SecureRip.TrackGrade.Verified, v.Grade);
        Assert.Contains("confidence 7", v.Reason);
    }

    [Fact]
    public void Self_consistency_alone_is_only_consistent_never_verified()
    {
        var v = SecureRip.Grade(Track(1000, passes: 4, ar: null));
        Assert.Equal(SecureRip.TrackGrade.Consistent, v.Grade);
    }

    [Fact]
    public void An_accuraterip_mismatch_is_suspect_even_when_reads_agree()
    {
        var v = SecureRip.Grade(Track(1000, passes: 4, ar: false));
        Assert.Equal(SecureRip.TrackGrade.Suspect, v.Grade);
        Assert.Contains("NOT match", v.Reason);
    }

    [Fact]
    public void C2_flags_make_a_track_suspect_even_with_an_ar_match()
    {
        var v = SecureRip.Grade(Track(1000, ar: true, arConf: 3,
            marks: (500, SecureRip.SectorState.C2Flagged)));
        Assert.Equal(SecureRip.TrackGrade.Suspect, v.Grade);
        Assert.Equal(1, v.C2Flagged);
    }

    [Fact]
    public void Unreadable_sectors_fail_the_track_outright()
    {
        var v = SecureRip.Grade(Track(1000, ar: true,
            marks: (10, SecureRip.SectorState.Unreadable)));
        Assert.Equal(SecureRip.TrackGrade.Failed, v.Grade);
    }

    [Fact]
    public void A_single_pass_without_corroboration_is_suspect()
    {
        var v = SecureRip.Grade(Track(1000, passes: 1, ar: null));
        Assert.Equal(SecureRip.TrackGrade.Suspect, v.Grade);
    }

    [Fact]
    public void Reread_plan_pads_and_merges_ranges_and_escalates_passes()
    {
        // Two mismatches 3 sectors apart: padding ±2 makes them one merged range.
        var t = Track(100, marks: new[]
            { (10, SecureRip.SectorState.PassMismatch), (14, SecureRip.SectorState.PassMismatch) });
        var plan = SecureRip.PlanReread(t);
        var r = Assert.Single(plan.Ranges);
        Assert.Equal(8, r.StartSector);                 // 10 - 2
        Assert.Equal(9, r.Count);                       // 8..16 inclusive of 14 + 2 pad
        Assert.Equal(5, plan.SuggestedPasses);          // mismatch → best-of-5
        Assert.Contains("best-of-5", plan.Strategy);
    }

    [Fact]
    public void Clean_track_plans_no_reread()
    {
        var plan = SecureRip.PlanReread(Track(500));
        Assert.True(plan.Nothing);
        Assert.Equal(0, plan.SuggestedPasses);
    }

    [Fact]
    public void Worst_state_drives_the_strategy()
    {
        var t = Track(100, marks: new[]
            { (5, SecureRip.SectorState.C2Flagged), (50, SecureRip.SectorState.Unreadable) });
        var plan = SecureRip.PlanReread(t);
        Assert.Equal(7, plan.SuggestedPasses);
        Assert.Contains("never silently zero-filled", plan.Strategy);
    }

    [Fact]
    public void Zero_sectors_of_evidence_cannot_earn_verified()
    {
        // Even a claimed AccurateRip match grades Suspect with no per-sector evidence behind it.
        var v = SecureRip.Grade(Track(0, ar: true, arConf: 9));
        Assert.Equal(SecureRip.TrackGrade.Suspect, v.Grade);
        Assert.Contains("zero sectors", v.Reason);
    }

    [Fact]
    public void Offsets_combine_additively()
    {
        Assert.Equal(36, SecureRip.CombinedOffsetSamples(30, 6));
        Assert.Equal(-6, SecureRip.CombinedOffsetSamples(-6));
    }
}
