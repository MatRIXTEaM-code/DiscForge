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
/// The Disc Actuary's promise is one sentence — "re-dump these first, they're
/// dying fastest" — and these tests hold it to that: exponential error growth
/// must project a finite remaining life, stability must not, uncorrectable
/// errors must jump the queue, and the ranking must order a mixed shelf by
/// genuine urgency. Plus the plumbing: histories append in time order and
/// round-trip through the store.
/// </summary>
public class DiscActuaryTests
{
    private static ActuaryScan Scan(string when, double tier1, double cu = 0)
        => new(when, tier1, tier1 / 10, cu, "TEST-DRIVE", "test");

    private static DiscScanHistory Growing(string id, double start = 20, double factor = 1.6)
    {
        // Yearly scans with exponentially compounding BLER: textbook dye decay.
        var h = new DiscScanHistory { DiscId = id, Title = id };
        double v = start;
        foreach (var year in new[] { "2022", "2023", "2024", "2025", "2026" })
        {
            h = h.Append(Scan($"{year}-01-01T00:00:00Z", v));
            v *= factor;
        }
        return h;
    }

    private static DiscScanHistory Stable(string id) =>
        new DiscScanHistory { DiscId = id, Title = id }
            .Append(Scan("2022-01-01T00:00:00Z", 12))
            .Append(Scan("2024-01-01T00:00:00Z", 11))
            .Append(Scan("2026-01-01T00:00:00Z", 12));

    [Fact]
    public void ExponentialGrowth_ProjectsFiniteRemainingLife()
    {
        // 20→131 over four years, threshold 220: the crossing is near and the fit is exact.
        var v = DiscActuary.Assess(Growing("dying"));
        Assert.NotNull(v.Kinetics);
        Assert.True(v.Kinetics!.RSquared > 0.99, $"clean exponential should fit tightly, got {v.Kinetics.RSquared}");
        Assert.NotNull(v.YearsRemaining);
        Assert.InRange(v.YearsRemaining!.Value, 0.1, 6);
        Assert.Contains("year(s) of readable life", v.Headline);
    }

    [Fact]
    public void StableDisc_ProjectsNoCrossing()
    {
        var v = DiscActuary.Assess(Stable("steady"));
        Assert.NotNull(v.Kinetics);
        Assert.Null(v.YearsRemaining);
        Assert.False(v.AlreadyFailing);
        Assert.Contains("stable", v.Headline);
    }

    [Fact]
    public void TwoScans_IsHonestlyNotATrend()
    {
        var h = new DiscScanHistory { DiscId = "young" }
            .Append(Scan("2025-01-01T00:00:00Z", 30))
            .Append(Scan("2026-01-01T00:00:00Z", 60));
        var v = DiscActuary.Assess(h);
        Assert.Null(v.Kinetics);
        Assert.Contains("need 3+", v.Headline);
        Assert.Equal(0, v.Urgency);
    }

    [Fact]
    public void UncorrectableErrors_JumpTheQueue()
    {
        var failing = new DiscScanHistory { DiscId = "failing" }
            .Append(Scan("2026-01-01T00:00:00Z", 40, cu: 3));
        var v = DiscActuary.Assess(failing);
        Assert.True(v.AlreadyFailing);
        Assert.StartsWith("FAILING NOW", v.Headline);
        Assert.Equal(double.MaxValue, v.Urgency);
    }

    [Fact]
    public void Rank_OrdersAMixedShelfByUrgency()
    {
        var shelf = new[]
        {
            Stable("steady"),
            Growing("dying-fast", start: 10, factor: 2),      // 10→160: near the cliff, not over it
            new DiscScanHistory { DiscId = "failing" }.Append(Scan("2026-01-01T00:00:00Z", 50, cu: 1)),
            Growing("dying-slow", start: 5, factor: 1.3),
        };
        var ranked = DiscActuary.Rank(shelf);

        Assert.Equal("failing", ranked[0].DiscId);
        Assert.Equal("dying-fast", ranked[1].DiscId);
        Assert.Equal("steady", ranked[^1].DiscId);

        string triage = DiscActuary.RenderTriage(ranked);
        Assert.Contains("re-dump \"failing\" first", triage);
    }

    [Fact]
    public void Append_KeepsTimeOrder_RegardlessOfInsertionOrder()
    {
        var h = new DiscScanHistory { DiscId = "x" }
            .Append(Scan("2026-01-01T00:00:00Z", 30))
            .Append(Scan("2022-01-01T00:00:00Z", 10))
            .Append(Scan("2024-01-01T00:00:00Z", 20));
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, h.Scans.Select(s => s.Tier1Max));
    }

    [Fact]
    public void Store_RoundTrips_AndAccumulates()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dforge_act_" + Guid.NewGuid().ToString("N"));
        try
        {
            var h = DiscActuary.LoadOrNew(dir, "genome:abc123", "Wipeout 2097")
                .Append(Scan("2025-01-01T00:00:00Z", 25));
            h.Save(DiscActuary.PathFor(dir, "genome:abc123"));

            var again = DiscActuary.LoadOrNew(dir, "genome:abc123")
                .Append(Scan("2026-01-01T00:00:00Z", 45));
            again.Save(DiscActuary.PathFor(dir, "genome:abc123"));

            var all = DiscActuary.LoadAll(dir);
            var loaded = Assert.Single(all);
            Assert.Equal(2, loaded.Scans.Count);
            Assert.Equal("Wipeout 2097", loaded.Title);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
