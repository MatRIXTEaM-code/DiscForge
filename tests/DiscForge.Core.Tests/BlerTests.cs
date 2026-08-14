// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class BlerTests
{
    [Fact]
    public void C1_classification_follows_the_rs_capacity()
    {
        Assert.Equal(C1Outcome.Ok, Bler.ClassifyC1(0));
        Assert.Equal(C1Outcome.E11, Bler.ClassifyC1(1));
        Assert.Equal(C1Outcome.E21, Bler.ClassifyC1(2));
        Assert.Equal(C1Outcome.E31, Bler.ClassifyC1(3));   // beyond the 2-error C1 capacity
        Assert.Equal(2, Bler.C1Correct);
    }

    [Fact]
    public void C2_classification_respects_erasure_flags()
    {
        Assert.Equal(C2Outcome.Ok, Bler.ClassifyC2(0));
        Assert.Equal(C2Outcome.E12, Bler.ClassifyC2(1));
        Assert.Equal(C2Outcome.E22, Bler.ClassifyC2(2));
        Assert.Equal(C2Outcome.E32, Bler.ClassifyC2(3));                       // 2-error limit without flags
        Assert.Equal(C2Outcome.E22, Bler.ClassifyC2(4, erasureFlagged: true)); // 4 erasures still corrected
        Assert.Equal(C2Outcome.E32, Bler.ClassifyC2(5, erasureFlagged: true)); // beyond erasure capacity
    }

    [Fact]
    public void A_clean_scan_passes_red_book_with_a_high_grade()
    {
        var samples = new List<BlerSample>();
        for (int s = 0; s < 100; s++) samples.Add(new BlerSample(s, E11: 5, 0, 0, E12: 1, 0, 0));
        var r = Bler.Analyze(samples);

        Assert.True(r.RedBookPass);
        Assert.Equal("A", r.Grade());
        Assert.Equal(5, r.MaxBler);
        Assert.Equal(5.0, r.AvgBler, 3);
        Assert.Equal(500, r.TotalE11);
    }

    [Fact]
    public void An_uncorrectable_error_fails_red_book_regardless_of_bler()
    {
        var samples = new List<BlerSample>
        {
            new(0, 3, 0, 0, 0, 0, 0),
            new(1, 2, 0, 0, 0, 0, E32: 1),   // one uncorrectable
        };
        var r = Bler.Analyze(samples);
        Assert.False(r.RedBookPass);
        Assert.Equal("F", r.Grade());
        Assert.Equal(1, r.TotalE32);
    }

    [Fact]
    public void Peak_bler_over_the_limit_fails_red_book()
    {
        var samples = new List<BlerSample>
        {
            new(0, 10, 0, 0, 0, 0, 0),
            new(1, 200, 30, 0, 0, 0, 0),   // BLER = 230 > 220
        };
        var r = Bler.Analyze(samples);
        Assert.Equal(230, r.MaxBler);
        Assert.False(r.RedBookPass);
    }

    [Fact]
    public void The_longest_error_burst_is_measured()
    {
        var samples = new List<BlerSample>
        {
            new(0, 0, 0, 0, 0, 0, 0),
            new(1, 4, 0, 0, 0, 0, 0),
            new(2, 6, 0, 0, 0, 0, 0),
            new(3, 5, 0, 0, 0, 0, 0),   // 3-second burst
            new(4, 0, 0, 0, 0, 0, 0),
            new(5, 2, 0, 0, 0, 0, 0),   // 1-second burst
        };
        var r = Bler.Analyze(samples);
        Assert.Equal(3, r.MaxBurstSeconds);
    }

    [Fact]
    public void Percentile_ignores_a_lone_spike()
    {
        var samples = new List<BlerSample>();
        for (int s = 0; s < 99; s++) samples.Add(new BlerSample(s, 10, 0, 0, 0, 0, 0));
        samples.Add(new BlerSample(99, 500, 0, 0, 0, 0, 0));   // single outlier
        var r = Bler.Analyze(samples);
        Assert.Equal(500, r.MaxBler);
        Assert.Equal(10, r.Bler95);   // the typical worst, not the spike
    }

    [Fact]
    public void Csv_with_a_header_maps_columns_by_name()
    {
        string csv = "second,e11,e21,e31,e12,e22,e32\n0,5,1,0,2,0,0\n1,7,0,0,0,1,0\n";
        var samples = Bler.ParseCsv(csv);
        Assert.Equal(2, samples.Count);
        Assert.Equal(6, samples[0].Bler);   // 5 + 1
        Assert.Equal(1, samples[1].E22);
    }

    [Fact]
    public void Csv_minimal_form_records_the_aggregate_bler()
    {
        string csv = "second,bler,cu\n0,12,0\n1,300,2\n";
        var samples = Bler.ParseCsv(csv);
        Assert.Equal(12, samples[0].Bler);
        Assert.Equal(300, samples[1].Bler);
        Assert.Equal(2, samples[1].E32);
        var r = Bler.Analyze(samples);
        Assert.False(r.RedBookPass);   // 300 > 220 and E32 present
    }

    [Fact]
    public void Headerless_seven_column_rows_parse()
    {
        string csv = "# comment\n0,5,1,0,2,0,0\n1,4,0,0,0,0,0\n";
        var samples = Bler.ParseCsv(csv);
        Assert.Equal(2, samples.Count);
        Assert.Equal(6, samples[0].Bler);
    }

    [Fact]
    public void An_empty_scan_is_safe()
    {
        var r = Bler.Analyze(new List<BlerSample>());
        Assert.Equal(0, r.Seconds);
        Assert.Contains("No C1/C2 samples", r.Summary());
    }
}
