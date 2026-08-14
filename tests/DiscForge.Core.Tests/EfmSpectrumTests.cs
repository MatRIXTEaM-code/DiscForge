// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class EfmSpectrumTests
{
    private static byte[] Pattern(int n, Func<int, byte> f)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = f(i);
        return b;
    }

    [Fact]
    public void A_conforming_stream_has_no_illegal_runs()
    {
        var r = EfmSpectrum.Analyze(Pattern(1024, i => (byte)(i * 37 + 5)));
        Assert.True(r.ConstraintOk, $"{r.ConstraintViolations} illegal runs");
        Assert.Equal(0, r.ConstraintViolations);
        // Every legal run must fall in 3T..11T.
        for (int t = 0; t < 3; t++) Assert.Equal(0, r.RunHistogram[t]);
        Assert.True(r.RunHistogram[3] > 0);   // short runs are always present
    }

    [Fact]
    public void The_histogram_sums_to_the_run_count()
    {
        var r = EfmSpectrum.Analyze(Pattern(2048, i => (byte)(i * 91 + 17)));
        int sum = 0;
        for (int t = 0; t < r.RunHistogram.Count; t++) sum += r.RunHistogram[t];
        Assert.Equal(r.TotalRuns, sum);
        Assert.True(r.TotalRuns > 0);
    }

    [Fact]
    public void Duty_asymmetry_and_entropy_are_in_range()
    {
        var r = EfmSpectrum.Analyze(Pattern(4096, i => (byte)(i * 181 + 3)));
        Assert.InRange(r.DutyAsymmetry, -1.0, 1.0);
        Assert.InRange(r.SpectralEntropy, 0.0, 1.0);
        // Real data spreads across many run lengths → healthy entropy.
        Assert.True(r.SpectralEntropy > 0.5, $"entropy {r.SpectralEntropy}");
    }

    [Fact]
    public void Pit_and_land_time_account_for_all_constrained_run_time()
    {
        var r = EfmSpectrum.Analyze(Pattern(1500, i => (byte)(i * 13 + 200)));
        long runTime = 0;
        for (int t = 0; t < r.RunHistogram.Count; t++) runTime += (long)t * r.RunHistogram[t];
        Assert.Equal(runTime, r.PitTimeT + r.LandTimeT);
    }

    [Fact]
    public void A_healthy_stream_grades_well_and_is_deterministic()
    {
        var data = Pattern(3000, i => (byte)(i * 101 + 29));
        var a = EfmSpectrum.Analyze(data);
        var b = EfmSpectrum.Analyze(data);
        Assert.Contains(a.Grade(), new[] { "A", "B", "C" });   // conforming, not failing
        Assert.Equal(a.Grade(), b.Grade());
        Assert.Equal(a.DutyAsymmetry, b.DutyAsymmetry, 12);
        Assert.Equal(a.SpectralEntropy, b.SpectralEntropy, 12);
    }

    [Fact]
    public void Empty_input_is_safe()
    {
        var r = EfmSpectrum.Analyze(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0, r.TotalRuns);
        Assert.Equal(0.0, r.DutyAsymmetry);
        Assert.Equal(0.0, r.SpectralEntropy);
        Assert.Contains("No EFM runs", r.Summary());
    }

    [Fact]
    public void Render_draws_a_histogram_row_per_legal_length()
    {
        var r = EfmSpectrum.Analyze(Pattern(800, i => (byte)(i * 7 + 1)));
        string text = EfmSpectrum.Render(r);
        for (int t = EfmSpectrum.MinT; t <= EfmSpectrum.MaxT; t++)
            Assert.Contains($"{t,2}T", text);
    }

    [Fact]
    public void Mean_run_length_sits_inside_the_legal_window()
    {
        var r = EfmSpectrum.Analyze(Pattern(2000, i => (byte)(i * 53 + 61)));
        Assert.InRange(r.MeanRunT, EfmSpectrum.MinT, EfmSpectrum.MaxT);
    }
}
