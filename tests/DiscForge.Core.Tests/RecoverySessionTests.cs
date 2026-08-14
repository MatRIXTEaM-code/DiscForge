// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The recovery assessment: every grading rule, the advice generation, the zero-region finder, and
/// the HTML report. All pure, so each verdict tier is pinned by a test.
/// </summary>
public class RecoverySessionTests
{
    private static RecoverySession.Findings Base() => new()
    {
        Image = "test.iso",
        SizeBytes = 1_000_000,
        Format = "ISO 9660 image",
        FilesystemViews = new[] { "ISO9660 \"GAME\" (12 files, 900,000 bytes)" },
        FilesystemVerdict = "Agree",
    };

    [Fact]
    public void Clean_image_with_readable_filesystem_is_intact()
    {
        Assert.Equal(RecoverySession.Grade.Intact, RecoverySession.Assess(Base()));
    }

    [Fact]
    public void Damage_with_a_surviving_filesystem_is_recoverable()
    {
        var f = Base() with { DamagedSectors = 12, BoundarySectors = 2 };
        var g = RecoverySession.Assess(f);
        Assert.Equal(RecoverySession.Grade.Recoverable, g);
        Assert.Contains(RecoverySession.Advise(f, g), a => a.Contains("fs-recover"));
        Assert.Contains(RecoverySession.Advise(f, g), a => a.Contains("12"));
    }

    [Fact]
    public void Damage_with_no_filesystem_is_damaged()
    {
        var f = Base() with { FilesystemViews = System.Array.Empty<string>(), FilesystemVerdict = null, DamagedSectors = 5 };
        var g = RecoverySession.Assess(f);
        Assert.Equal(RecoverySession.Grade.Damaged, g);
        Assert.Contains(RecoverySession.Advise(f, g), a => a.Contains("salvage-plan"));
    }

    [Fact]
    public void Unrecognized_and_unreadable_is_unreadable()
    {
        var f = Base() with { Format = null, FilesystemViews = System.Array.Empty<string>(), FilesystemVerdict = null };
        Assert.Equal(RecoverySession.Grade.Unreadable, RecoverySession.Assess(f));
    }

    [Fact]
    public void An_incomplete_filesystem_view_downgrades_a_clean_map()
    {
        var f = Base() with { FilesystemVerdict = "Incomplete" };
        Assert.Equal(RecoverySession.Grade.Recoverable, RecoverySession.Assess(f));
    }

    [Fact]
    public void Mostly_blank_image_is_flagged_even_without_a_bad_sector_map()
    {
        var f = Base() with
        {
            ZeroRegions = new[] { new RecoverySession.ZeroRegion(0, 600_000) },
        };
        Assert.Equal(RecoverySession.Grade.Recoverable, RecoverySession.Assess(f));
    }

    [Fact]
    public void FindZeroRegions_reports_runs_at_the_right_offsets()
    {
        // 300 KB: data / 100 KB zeros / data / trailing 80 KB zeros.
        var buf = new byte[300 * 1024];
        for (int i = 0; i < buf.Length; i++) buf[i] = 0xAB;
        System.Array.Clear(buf, 50 * 1024, 100 * 1024);
        System.Array.Clear(buf, buf.Length - 80 * 1024, 80 * 1024);

        var regions = RecoverySession.FindZeroRegions(new MemoryStream(buf));
        Assert.Equal(2, regions.Count);
        Assert.Equal(50 * 1024, regions[0].Offset);
        Assert.Equal(100 * 1024, regions[0].Length);
        Assert.Equal(300 * 1024 - 80 * 1024, regions[1].Offset);
        Assert.Equal(80 * 1024, regions[1].Length);
    }

    [Fact]
    public void Short_zero_runs_are_not_reported()
    {
        var buf = new byte[128 * 1024];
        for (int i = 0; i < buf.Length; i++) buf[i] = 1;
        System.Array.Clear(buf, 1000, 4096);          // well under the 64 KiB threshold
        Assert.Empty(RecoverySession.FindZeroRegions(new MemoryStream(buf)));
    }

    [Fact]
    public void Html_report_carries_the_grade_advice_and_escapes_content()
    {
        var f = Base() with { Image = "weird<name>.iso", DamagedSectors = 3 };
        var g = RecoverySession.Assess(f);
        var advice = RecoverySession.Advise(f, g);
        var html = RecoverySession.BuildHtml(f, g, advice);
        Assert.Contains("RECOVERABLE", html);
        Assert.Contains("weird&lt;name&gt;.iso", html);
        Assert.DoesNotContain("weird<name>", html);
        Assert.Contains("fs-recover", html);
    }
}
