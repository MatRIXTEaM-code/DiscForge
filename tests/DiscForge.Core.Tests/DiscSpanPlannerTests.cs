// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Media;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>Multi-disc spanning planner — bin-packing, grouping, oversize handling. Pure logic.</summary>
public class DiscSpanPlannerTests
{
    private const long GB = 1024L * 1024 * 1024;
    private const long MB = 1024L * 1024;

    [Fact]
    public void PacksIntoFewestDiscs_FirstFitDecreasing()
    {
        var items = new[]
        {
            new SpanItem("a", 20 * GB), new SpanItem("b", 10 * GB), new SpanItem("c", 6 * GB),
            new SpanItem("d", 5 * GB), new SpanItem("e", 4 * GB),
        };
        var plan = DiscSpanPlanner.Plan(items, DiscMedium.Bd25);
        Assert.Equal(3, plan.DiscCount);
        Assert.Empty(plan.Oversized);
        // Every item placed exactly once.
        Assert.Equal(items.Length, plan.Discs.Sum(d => d.Items.Count));
        // No disc exceeds its capacity.
        Assert.All(plan.Discs, d => Assert.True(d.UsedBytes <= d.CapacityBytes));
    }

    [Fact]
    public void KeepsGroupsTogether_WhenTheyFit()
    {
        var items = new[]
        {
            new SpanItem("S1/e1", 3 * GB, "S1"), new SpanItem("S1/e2", 3 * GB, "S1"), new SpanItem("S1/e3", 3 * GB, "S1"),
            new SpanItem("S2/e1", 8 * GB, "S2"), new SpanItem("S2/e2", 8 * GB, "S2"),
        };
        var plan = DiscSpanPlanner.Plan(items, DiscMedium.Bd25, keepGroups: true);
        Assert.Empty(plan.SplitGroups);
        // Each group's files all land on a single disc.
        foreach (var grp in new[] { "S1", "S2" })
        {
            var discsWithGroup = plan.Discs.Where(d => d.Items.Any(i => i.Group == grp)).ToList();
            Assert.Single(discsWithGroup);
        }
    }

    [Fact]
    public void SplitsGroup_WhenLargerThanOneDisc()
    {
        var items = new[]
        {
            new SpanItem("big/a", 20 * GB, "big"), new SpanItem("big/b", 20 * GB, "big"),
        };
        var plan = DiscSpanPlanner.Plan(items, DiscMedium.Bd25, keepGroups: true);
        Assert.Contains("big", plan.SplitGroups);
        Assert.Equal(2, plan.DiscCount);   // split across two discs
    }

    [Fact]
    public void FlagsOversizedItem_TooBigForMedia()
    {
        var items = new[] { new SpanItem("huge.iso", 2 * GB), new SpanItem("ok.txt", 10 * MB) };
        var plan = DiscSpanPlanner.Plan(items, DiscMedium.Cd80);
        Assert.Single(plan.Oversized);
        Assert.Equal("huge.iso", plan.Oversized[0].Path);
        // The placeable file still gets a disc.
        Assert.Equal(1, plan.DiscCount);
    }

    [Fact]
    public void AchievesOptimalDiscCount_ForUniformFiles()
    {
        var items = Enumerable.Range(0, 50).Select(i => new SpanItem($"f{i}", 1 * GB)).ToArray();
        var plan = DiscSpanPlanner.Plan(items, DiscMedium.Bd25);
        // 23 × 1GB fit per BD25 (after overhead), so 50 files need 3 discs.
        Assert.Equal(3, plan.DiscCount);
    }

    [Fact]
    public void AccountsForSectorRounding()
    {
        // A 1-byte file still costs a full 2048-byte sector.
        var items = new[] { new SpanItem("tiny", 1) };
        var plan = DiscSpanPlanner.Plan(items, DiscMedium.Cd80);
        Assert.Equal(1, plan.DiscCount);
        Assert.True(plan.Discs[0].UsedBytes >= 2048);
    }

    [Fact]
    public void MediumLookup_IsCaseInsensitive()
    {
        Assert.Equal(DiscMedium.Bd25, DiscMedium.ByKey("BD25"));
        Assert.Equal(DiscMedium.Dvd9, DiscMedium.ByKey(" dvd9 "));
        Assert.Null(DiscMedium.ByKey("floppy"));
    }
}
