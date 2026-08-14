// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the pregap conformance audit: a data+audio disc with the standard 2-second boundary pregap
/// passes; a missing pregap, a negative gap, and a non-standard boundary length are each caught; and a
/// track that does not start at 00:00:00 is flagged on track 1.
/// </summary>
public class PregapConformanceTests
{
    private static CueSheet Parse(string body) => CueSheet.Parse("FILE \"g.bin\" BINARY\n" + body);

    [Fact]
    public void A_data_then_audio_disc_with_a_two_second_boundary_pregap_conforms()
    {
        var cue = Parse(
            "  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n    INDEX 00 09:58:00\n    INDEX 01 10:00:00\n");

        var r = PregapConformance.Check(cue);
        Assert.True(r.Conformant);
        Assert.Equal(150, r.Tracks[1].GapSectors);
        Assert.True(r.Tracks[1].CrossesDataAudioBoundary);
    }

    [Fact]
    public void A_missing_boundary_pregap_is_caught()
    {
        var cue = Parse(
            "  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n    INDEX 01 10:00:00\n");

        var r = PregapConformance.Check(cue);
        Assert.False(r.Conformant);
        Assert.Contains(r.Issues, i => i.Contains("no 2-second pregap"));
    }

    [Fact]
    public void A_negative_pregap_is_caught()
    {
        var cue = Parse(
            "  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n    INDEX 00 10:02:00\n    INDEX 01 10:00:00\n");

        var r = PregapConformance.Check(cue);
        Assert.False(r.Conformant);
        Assert.Equal(-150, r.Tracks[1].GapSectors);
        Assert.Contains(r.Issues, i => i.Contains("negative pregap"));
    }

    [Fact]
    public void A_non_standard_boundary_length_is_flagged()
    {
        var cue = Parse(
            "  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n    INDEX 00 09:59:00\n    INDEX 01 10:00:00\n"); // 75-sector gap

        var r = PregapConformance.Check(cue);
        Assert.False(r.Conformant);
        Assert.Contains(r.Issues, i => i.Contains("not the standard 150"));
    }

    [Fact]
    public void Track_one_must_start_at_zero()
    {
        var cue = Parse("  TRACK 01 MODE1/2352\n    INDEX 01 00:02:00\n");

        var r = PregapConformance.Check(cue);
        Assert.False(r.Conformant);
        Assert.Contains(r.Issues, i => i.Contains("not 00:00:00"));
    }

    [Fact]
    public void Two_consecutive_audio_tracks_do_not_require_a_boundary_pregap()
    {
        // Audio→audio is not a data/audio boundary, so a zero gap is fine.
        var cue = Parse(
            "  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n    INDEX 01 03:00:00\n");

        var r = PregapConformance.Check(cue);
        Assert.True(r.Conformant);
        Assert.False(r.Tracks[1].CrossesDataAudioBoundary);
    }
}
