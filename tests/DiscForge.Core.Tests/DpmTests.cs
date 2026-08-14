// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class DpmTests
{
    // A smooth CAV-like ramp: speed rises steadily from inner to outer radius.
    private static List<DpmSample> Ramp(int n = 300, double from = 1.0, double to = 3.0)
    {
        var list = new List<DpmSample>(n);
        for (int i = 0; i < n; i++)
            list.Add(new DpmSample(i * 10, from + (to - from) * i / (n - 1)));
        return list;
    }

    [Fact]
    public void A_smooth_profile_has_no_anomalies()
    {
        var r = Dpm.Analyze(Ramp());
        Assert.Empty(r.Anomalies);
        Assert.Equal(DpmVerdict.Clean, r.Verdict);
        Assert.True(r.MaxSpeed > r.MinSpeed);
    }

    [Fact]
    public void A_sharp_narrow_slowdown_reads_as_a_ring()
    {
        var s = Ramp();
        for (int i = 150; i < 156; i++) s[i] = new DpmSample(s[i].Lba, s[i].Speed * 0.4);   // deep narrow dip
        var r = Dpm.Analyze(s);
        Assert.Equal(DpmVerdict.RingLike, r.Verdict);
        Assert.Single(r.Anomalies);
        Assert.True(r.Anomalies[0].SampleCount <= Dpm.RingMaxSpan);
        Assert.True(r.Anomalies[0].DepthFraction > 0.25);
    }

    [Fact]
    public void Many_slowdowns_read_as_damage()
    {
        var s = Ramp();
        foreach (int c in new[] { 40, 90, 140, 190, 240 })
            for (int i = c; i < c + 5; i++) s[i] = new DpmSample(s[i].Lba, s[i].Speed * 0.4);
        var r = Dpm.Analyze(s);
        Assert.Equal(DpmVerdict.DamageLike, r.Verdict);
        Assert.True(r.Anomalies.Count >= 4);
    }

    [Fact]
    public void The_fingerprint_is_scale_invariant()
    {
        var slow = Ramp();
        // Same disc read on a 2× faster drive: every speed doubles, the shape is identical.
        var fast = slow.Select(x => new DpmSample(x.Lba, x.Speed * 2.0)).ToList();

        var a = Dpm.Analyze(slow);
        var b = Dpm.Analyze(fast);
        Assert.NotEqual(0, a.Fingerprint);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void A_different_layout_fingerprints_differently()
    {
        var plain = Dpm.Analyze(Ramp());
        var ringed = Ramp();
        for (int i = 150; i < 156; i++) ringed[i] = new DpmSample(ringed[i].Lba, ringed[i].Speed * 0.4);
        var withRing = Dpm.Analyze(ringed);
        Assert.NotEqual(plain.Fingerprint, withRing.Fingerprint);
    }

    [Fact]
    public void Csv_parses_speed_columns_by_header()
    {
        string csv = "lba,speed\n0,1.5\n100,1.7\n200,2.0\n";
        var s = Dpm.ParseCsv(csv);
        Assert.Equal(3, s.Count);
        Assert.Equal(100, s[1].Lba);
        Assert.Equal(1.7, s[1].Speed, 6);
    }

    [Fact]
    public void Csv_time_column_is_inverted_to_speed()
    {
        // Larger read time = slower: the reciprocal makes sample 1 slower than sample 0.
        string csv = "lba,timeus\n0,100\n100,400\n";
        var s = Dpm.ParseCsv(csv);
        Assert.Equal(1.0 / 100, s[0].Speed, 9);
        Assert.True(s[1].Speed < s[0].Speed);
    }

    [Fact]
    public void Headerless_lines_parse_as_lba_speed()
    {
        string csv = "# scan\n0,1.0\n10,1.1\n20,1.2\n";
        var s = Dpm.ParseCsv(csv);
        Assert.Equal(3, s.Count);
        Assert.Equal(20, s[2].Lba);
    }

    [Fact]
    public void An_empty_scan_is_safe()
    {
        var r = Dpm.Analyze(new List<DpmSample>());
        Assert.Equal(0, r.Samples);
        Assert.Equal(DpmVerdict.Clean, r.Verdict);
        Assert.Contains("No DPM samples", r.Summary());
    }
}
