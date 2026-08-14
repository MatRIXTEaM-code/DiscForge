// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Gdi;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the GD-ROM zone-geometry checks: a pressed GD-ROM's high-density area
/// begins at exactly LBA 45000, after a low-density (SD) area. The validator should
/// pass a standard layout and flag a shifted HD start or a missing SD area.
/// </summary>
public class GdiValidatorTests
{
    private static GdiTrack T(int number, long lba, GdiTrackType type) => new()
    {
        Number = number, StartLba = lba, Type = type,
        SectorSize = 2352, FileName = $"track{number:00}.bin", Offset = 0,
    };

    // A standard retail layout: SD data + SD audio, then the HD data track at 45000.
    private static GdiDisc Standard(long hdStart = 45000) => new()
    {
        Tracks = new[] { T(1, 0, GdiTrackType.Data), T(2, 600, GdiTrackType.Audio), T(3, hdStart, GdiTrackType.Data) },
    };

    private static GdiValidation Run(GdiDisc disc) =>
        // The track files aren't on disk; file checks add their own issues, but the zone-geometry
        // checks under test are independent of file presence.
        GdiValidator.Validate(disc, Path.GetTempPath());

    [Fact]
    public void A_standard_layout_reports_the_zone_summary_and_no_shifted_start()
    {
        var v = Run(Standard());
        Assert.Contains(v.Issues, i => i.Message.Contains("Layout: 2 low-density (SD) track(s), then 1 high-density"));
        Assert.DoesNotContain(v.Issues, i => i.Message.Contains("shifted HD start") || i.Message.Contains("begins at LBA 45000 on a standard GD-ROM, but"));
    }

    [Fact]
    public void A_high_density_track_that_does_not_start_at_45000_is_flagged()
    {
        var v = Run(Standard(hdStart: 45100));
        var issue = Assert.Single(v.Issues, i => i.Message.Contains("shifted HD start"));
        Assert.Equal(GdiIssueLevel.Warning, issue.Level);
        Assert.Contains("+100 sectors", issue.Message);
    }

    [Fact]
    public void An_image_with_no_low_density_area_is_flagged()
    {
        var hdOnly = new GdiDisc { Tracks = new[] { T(1, 45000, GdiTrackType.Data) } };
        var v = Run(hdOnly);
        Assert.Contains(v.Issues, i => i.Level == GdiIssueLevel.Warning && i.Message.Contains("No low-density (SD) area tracks"));
    }

    [Fact]
    public void A_low_density_only_dump_warns_that_the_game_area_is_absent()
    {
        var sdOnly = new GdiDisc { Tracks = new[] { T(1, 0, GdiTrackType.Data), T(2, 600, GdiTrackType.Audio) } };
        var v = Run(sdOnly);
        Assert.Contains(v.Issues, i => i.Message.Contains("No data track begins in the high-density area"));
    }
}
