// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Devices;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The read-rate aggregator behind the benchmark half of Discovery. The point of the profile is the
/// SLOWEST region — where a marginal disc drags before it fails — so the test pins that the slow
/// sample is found and that the average is total-bytes-over-total-time (not a mean of per-sample rates).
/// </summary>
public class ReadRateProfileTests
{
    [Fact]
    public void Summarize_computes_rates_and_finds_the_slowest_region()
    {
        // Same 100 sectors, but the second chunk takes 4× as long → it's the slow region.
        var samples = new[]
        {
            new ReadRateProfile.Sample(0, 100, 100.0),
            new ReadRateProfile.Sample(100, 100, 400.0),
        };

        var r = ReadRateProfile.Summarize(samples);

        Assert.Equal(200, r.SectorsRead);
        Assert.Equal(2, r.Samples);
        Assert.Equal(100, r.Slowest!.StartLba);              // the 400 ms chunk is slowest
        Assert.True(r.MaxMbps > r.MinMbps);

        // Average is total bytes / total time (0.5 s), not the mean of the two per-chunk rates.
        double expectedAvg = (200 * (double)ReadRateProfile.RawSectorBytes / (1024.0 * 1024.0)) / 0.5;
        Assert.Equal(expectedAvg, r.AvgMbps, 3);
    }

    [Fact]
    public void Empty_samples_report_nothing_read()
    {
        var r = ReadRateProfile.Summarize(System.Array.Empty<ReadRateProfile.Sample>());
        Assert.Equal(0, r.Samples);
        Assert.Equal(0, r.SectorsRead);
        Assert.Contains("No sectors", r.Summary);
    }
}
