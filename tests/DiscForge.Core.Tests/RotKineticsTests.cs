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
/// Tests for the first-order rot-kinetics model: it should recover a known exponential growth constant
/// from a synthetic error history, report no crossing for a stable disc, and scale the survival forecast
/// correctly with the storage-environment Arrhenius/Eyring factor.
/// </summary>
public class RotKineticsTests
{
    private static readonly DateTimeOffset T0 = new(2018, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static List<RotSample> Exponential(double e0, double k, int years)
    {
        var h = new List<RotSample>();
        for (int y = 0; y <= years; y++)
        {
            double noise = y % 2 == 0 ? 1.03 : 0.97;      // slight noise so a confidence band exists
            h.Add(new RotSample(T0.AddDays(y * 365.25), e0 * Math.Exp(k * y) * noise));
        }
        return h;
    }

    [Fact]
    public void Recovers_a_known_exponential_growth_constant()
    {
        var r = RotKinetics.Fit(Exponential(10, 0.5, 6), threshold: 220);
        Assert.Equal(0.5, r.GrowthPerYear, 2);
        Assert.Equal(10, r.InitialRate, 0);
        Assert.True(r.RSquared > 0.99);
        Assert.NotNull(r.ThresholdDate);
        Assert.NotNull(r.Band);
    }

    [Fact]
    public void A_stable_disc_projects_no_crossing()
    {
        var flat = new List<RotSample>
        {
            new(T0, 15), new(T0.AddDays(365), 16), new(T0.AddDays(730), 15),
        };
        var r = RotKinetics.Fit(flat);
        Assert.Null(r.YearsToThreshold);
        Assert.True(r.GrowthPerYear < 0.05);
    }

    [Fact]
    public void Harsher_storage_accelerates_and_shortens_time_to_threshold()
    {
        Assert.True(RotKinetics.AccelFactor(new StorageEnvironment(35, 80)) > 1.5);
        Assert.True(RotKinetics.AccelFactor(new StorageEnvironment(10, 30)) < 0.7);
        Assert.Equal(1.0, RotKinetics.AccelFactor(new StorageEnvironment(25, 50)), 3);   // reference

        var hist = Exponential(10, 0.5, 6);
        var baseline = RotKinetics.Fit(hist, 220);
        var hot = RotKinetics.Fit(hist, 220, new StorageEnvironment(35, 80));
        Assert.True(hot.YearsToThreshold < baseline.YearsToThreshold);
    }

    [Fact]
    public void An_already_failing_disc_is_flagged_immediately()
    {
        var h = new List<RotSample> { new(T0, 200), new(T0.AddDays(365), 260) };
        var r = RotKinetics.Fit(h, threshold: 220);
        Assert.True(r.AlreadyFailing);
        Assert.Contains("immediately", r.Assessment);
    }
}
