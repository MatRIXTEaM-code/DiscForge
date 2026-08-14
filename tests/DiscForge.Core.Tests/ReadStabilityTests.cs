// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// read-stability grades a disc from how consistently it reads across passes — a failing disc returns different
/// bytes for the same sector on different reads. The tests confirm: identical passes read STABLE with no unstable
/// sectors; a sector that differs on one of several passes is flagged (fewer-than-all agreement); and a sector
/// that disagrees across the majority of passes is graded severe / DEGRADING.
/// </summary>
public class ReadStabilityTests
{
    private const int SS = ReadStability.RawSector;
    private const int N = 20;

    private static byte[] Base()
    {
        var b = new byte[N * SS];
        for (int s = 0; s < N; s++) b.AsSpan(s * SS, SS).Fill((byte)((s * 7 + 3) % 256));
        return b;
    }

    [Fact]
    public void Identical_passes_read_stable()
    {
        var a = Base(); var b = Base(); var c = Base();
        var r = ReadStability.Analyze(new[] { a, b, c });
        Assert.Equal(DiscStability.Stable, r.Health);
        Assert.Equal(0, r.UnstableSectors);
        Assert.Equal(r.Sectors, r.StableSectors);
    }

    [Fact]
    public void A_sector_differing_on_one_pass_is_flagged_marginal()
    {
        var p1 = Base(); var p2 = Base(); var p3 = Base(); var p4 = Base();
        p3.AsSpan(12 * SS, SS).Fill(0x99);              // 3 of 4 agree
        var r = ReadStability.Analyze(new[] { p1, p2, p3, p4 });

        Assert.Equal(1, r.UnstableSectors);
        Assert.Equal(0, r.SeverelyUnstable);
        Assert.Equal(DiscStability.Marginal, r.Health);
        Assert.Contains(r.UnstableRuns, u => u.StartSector == 12 && u.WorstAgreement == 3);
    }

    [Fact]
    public void A_sector_disagreeing_across_the_majority_is_severe_and_degrading()
    {
        var passes = new[] { Base(), Base(), Base(), Base() };
        for (int k = 0; k < 4; k++) passes[k].AsSpan(5 * SS, SS).Fill((byte)(k + 10));  // all four differ
        var r = ReadStability.Analyze(passes);

        Assert.Equal(1, r.SeverelyUnstable);
        Assert.Equal(DiscStability.Degrading, r.Health);
        Assert.Contains(r.UnstableRuns, u => u.StartSector == 5 && u.WorstAgreement == 1);
    }

    [Fact]
    public void Needs_at_least_two_passes()
    {
        Assert.Throws<ArgumentException>(() => ReadStability.Analyze(new[] { Base() }));
    }
}
