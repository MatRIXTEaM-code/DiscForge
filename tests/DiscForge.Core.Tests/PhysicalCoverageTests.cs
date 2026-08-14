// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The physical-coverage proof asserts a stronger property than count reconciliation: the claimed
/// regions must PARTITION the image — every sector accounted for exactly once. These check the two
/// failure modes it exists to catch (a silent gap, a doubly-claimed overlap), the passing partition,
/// and that an ISO whose secondary-namespace directory the classifier can't place surfaces as a gap
/// rather than passing silently.
/// </summary>
public class PhysicalCoverageTests
{
    private static CoverageRegion R(long start, long count, string owner) => new(start, count, owner);

    [Fact]
    public void A_perfect_partition_is_complete()
    {
        var p = PhysicalCoverage.Prove(100, new[]
        {
            R(0, 16, "system"), R(16, 4, "descriptors"), R(20, 70, "file"), R(90, 10, "free"),
        });
        Assert.True(p.Complete);
        Assert.Equal(100, p.AccountedSectors);
        Assert.Empty(p.Gaps);
        Assert.Empty(p.Overlaps);
    }

    [Fact]
    public void A_missing_middle_range_is_a_gap()
    {
        var p = PhysicalCoverage.Prove(100, new[] { R(0, 40, "a"), R(50, 50, "b") });   // 40..50 missing
        Assert.False(p.Complete);
        var gap = Assert.Single(p.Gaps);
        Assert.Equal(40, gap.StartSector);
        Assert.Equal(10, gap.SectorCount);
    }

    [Fact]
    public void A_trailing_unclaimed_run_is_a_gap()
    {
        var p = PhysicalCoverage.Prove(100, new[] { R(0, 80, "a") });   // 80..100 unaccounted
        var gap = Assert.Single(p.Gaps);
        Assert.Equal(80, gap.StartSector);
        Assert.Equal(20, gap.SectorCount);
    }

    [Fact]
    public void Two_structures_claiming_the_same_sectors_is_an_overlap()
    {
        var p = PhysicalCoverage.Prove(100, new[]
        {
            R(0, 50, "FILE_A"), R(40, 60, "FILE_B"),   // 40..50 claimed by both
        });
        Assert.False(p.Complete);
        var o = Assert.Single(p.Overlaps);
        Assert.Equal(40, o.StartSector);
        Assert.Equal(10, o.SectorCount);
        Assert.Contains("FILE", o.OwnerA + o.OwnerB);
    }

    [Fact]
    public void An_iso_with_an_unresolved_secondary_directory_surfaces_it_as_a_gap()
    {
        // A default IsoBuilder image is Joliet: the classifier resolves one namespace, so the other
        // namespace's directory sector is not claimed — the proof must report it as a gap rather than
        // silently pass. (This is the honest value: an unaccounted sector is never hidden.)
        var iso = IsoBuilder.Build("DISC", new[] { new IsoBuilder.FileEntry("A.TXT", Encoding.ASCII.GetBytes("hi")) }).Image;
        var p = PhysicalCoverage.OfIso(iso);

        Assert.False(p.Complete);
        Assert.NotEmpty(p.Gaps);
        Assert.Empty(p.Overlaps);      // no structure double-claims; the issue is coverage, not conflict
    }

    [Fact]
    public void Overlapping_and_gapping_can_be_reported_together()
    {
        var p = PhysicalCoverage.Prove(100, new[]
        {
            R(0, 30, "a"), R(20, 30, "b"),   // overlap 20..30
            // gap 50..70
            R(70, 30, "c"),
        });
        Assert.NotEmpty(p.Overlaps);
        Assert.NotEmpty(p.Gaps);
        Assert.Equal(50, p.Gaps[0].StartSector);
    }
}
