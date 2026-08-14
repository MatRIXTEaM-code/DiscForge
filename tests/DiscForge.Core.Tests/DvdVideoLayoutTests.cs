// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.DvdVideo;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The DVD-Video VIDEO_TS layout planner: classifies files, orders them exactly as a
/// conformant disc requires (VMG first, then each VTS with its IFO leading and BUP
/// trailing), and validates the set. This is the front half of the DVD-Video assembler.
/// </summary>
public class DvdVideoLayoutTests
{
    private static (string, long)[] SimpleDisc() => new (string, long)[]
    {
        ("VIDEO_TS.BUP", 12288),
        ("VIDEO_TS.IFO", 12288),
        ("VIDEO_TS.VOB", 1048576),
        ("VTS_01_0.BUP", 30720),
        ("VTS_01_0.IFO", 30720),
        ("VTS_01_0.VOB", 2097152),
        ("VTS_01_2.VOB", 4194304),
        ("VTS_01_1.VOB", 8388608),
    };

    [Theory]
    [InlineData("VIDEO_TS.IFO", DvdVideoRole.VmgIfo)]
    [InlineData("VIDEO_TS.VOB", DvdVideoRole.VmgMenu)]
    [InlineData("VIDEO_TS.BUP", DvdVideoRole.VmgBup)]
    [InlineData("VTS_01_0.IFO", DvdVideoRole.VtsIfo)]
    [InlineData("VTS_01_0.VOB", DvdVideoRole.VtsMenu)]
    [InlineData("VTS_01_0.BUP", DvdVideoRole.VtsBup)]
    [InlineData("VTS_03_5.VOB", DvdVideoRole.VtsTitle)]
    [InlineData("readme.txt", DvdVideoRole.Unknown)]
    [InlineData("VTS_01_A.VOB", DvdVideoRole.Unknown)]
    public void Classifies_files_by_role(string name, DvdVideoRole expected)
    {
        Assert.Equal(expected, DvdVideoLayout.Classify(name, 0).Role);
    }

    [Fact]
    public void Orders_a_simple_disc_correctly()
    {
        var plan = DvdVideoLayout.Plan(SimpleDisc());
        Assert.True(plan.IsValid);

        var order = plan.OrderedFiles.Select(f => f.Name).ToArray();
        Assert.Equal(new[]
        {
            "VIDEO_TS.IFO", "VIDEO_TS.VOB", "VIDEO_TS.BUP",   // VMG: IFO, menu, BUP
            "VTS_01_0.IFO", "VTS_01_0.VOB",                    // VTS control + menu
            "VTS_01_1.VOB", "VTS_01_2.VOB",                    // title VOBs, contiguous
            "VTS_01_0.BUP",                                    // BUP trails the set
        }, order);

        // The IFO leads its set and the BUP trails it (surface-defect separation).
        int ifo = Array.IndexOf(order, "VTS_01_0.IFO");
        int bup = Array.IndexOf(order, "VTS_01_0.BUP");
        Assert.True(ifo < bup);
    }

    [Fact]
    public void Orders_multiple_title_sets_ascending()
    {
        var files = new (string, long)[]
        {
            ("VIDEO_TS.IFO", 100), ("VIDEO_TS.BUP", 100),
            ("VTS_02_0.IFO", 100), ("VTS_02_1.VOB", 100), ("VTS_02_0.BUP", 100),
            ("VTS_01_0.IFO", 100), ("VTS_01_1.VOB", 100), ("VTS_01_0.BUP", 100),
        };
        var plan = DvdVideoLayout.Plan(files);
        Assert.Equal(new[] { 1, 2 }, plan.TitleSets);
        int t1 = plan.OrderedFiles.ToList().FindIndex(f => f.Name == "VTS_01_0.IFO");
        int t2 = plan.OrderedFiles.ToList().FindIndex(f => f.Name == "VTS_02_0.IFO");
        Assert.True(t1 < t2);   // title set 1 before title set 2
    }

    [Fact]
    public void A_missing_vmg_ifo_is_a_fatal_error()
    {
        var plan = DvdVideoLayout.Plan(new (string, long)[] { ("VTS_01_0.IFO", 100), ("VTS_01_1.VOB", 100) });
        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("VIDEO_TS.IFO"));
    }

    [Fact]
    public void A_missing_bup_is_only_a_warning()
    {
        var files = new (string, long)[]
        {
            ("VIDEO_TS.IFO", 100), ("VTS_01_0.IFO", 100), ("VTS_01_1.VOB", 100), ("VTS_01_0.BUP", 100),
        };
        var plan = DvdVideoLayout.Plan(files);
        Assert.True(plan.IsValid);   // still valid…
        Assert.Contains(plan.Warnings, w => w.Contains("VIDEO_TS.BUP"));   // …but warned
    }

    [Fact]
    public void Non_contiguous_title_vobs_are_a_fatal_error()
    {
        var files = new (string, long)[]
        {
            ("VIDEO_TS.IFO", 100), ("VIDEO_TS.BUP", 100),
            ("VTS_01_0.IFO", 100), ("VTS_01_1.VOB", 100), ("VTS_01_3.VOB", 100), ("VTS_01_0.BUP", 100),
        };
        var plan = DvdVideoLayout.Plan(files);
        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("contiguous"));
    }

    [Fact]
    public void An_oversize_vob_is_rejected()
    {
        var files = new (string, long)[]
        {
            ("VIDEO_TS.IFO", 100), ("VIDEO_TS.BUP", 100),
            ("VTS_01_0.IFO", 100), ("VTS_01_1.VOB", 1_073_741_825L), ("VTS_01_0.BUP", 100),
        };
        var plan = DvdVideoLayout.Plan(files);
        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("1 GB"));
    }
}
