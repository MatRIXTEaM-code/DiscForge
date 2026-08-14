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
/// Tests for the CU2 track-map sidecar: generation from a cue applies the +150-sector lead-in offset and
/// the revision-2 line layout, parsing round-trips to the original absolute LBAs, and verify cross-checks
/// a CU2 against the cue (catching a shifted track).
/// </summary>
public class Cu2Tests
{
    private static CueSheet TwoTrack() => CueSheet.Parse(
        "FILE \"g.bin\" BINARY\n" +
        "  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
        "  TRACK 02 AUDIO\n    INDEX 00 04:58:00\n    INDEX 01 05:00:00\n");

    [Fact]
    public void Generation_applies_the_lead_in_offset_and_revision_2_layout()
    {
        string cu2 = Cu2.Write(TwoTrack(), totalSectors: 30000);
        Assert.StartsWith("ntracks 2", cu2);
        Assert.Contains("data1     00:02:00", cu2);        // LBA 0 + 150 = 00:02:00
        Assert.Contains("pregap02  05:00:00", cu2);        // index00 22350 + 150 = 22500
        Assert.Contains("track02   05:02:00", cu2);        // index01 22500 + 150 = 22650
        Assert.Contains("size      06:42:00", cu2);        // 30000 + 150 = 30150
        Assert.Contains("\r\n\r\ntrk end", cu2);                  // trk end has exactly one leading blank line…
        Assert.DoesNotContain("\r\n\r\n\r\ntrk end", cu2 + "X");  // …and never two
        Assert.EndsWith("trk end   06:42:00", cu2);        // no trailing newline
    }

    [Fact]
    public void Parsing_round_trips_to_absolute_lbas()
    {
        var parsed = Cu2.Parse(Cu2.Write(TwoTrack(), 30000));
        Assert.Equal(2, parsed.NTracks);
        Assert.Equal(0, parsed.Data1Lba);
        Assert.Equal(30000, parsed.SizeLba);
        Assert.Equal(22500, parsed.Tracks.First(t => t.Number == 2).StartLba);
    }

    [Fact]
    public void Verify_matches_a_faithful_CU2_and_catches_a_shifted_track()
    {
        var cue = TwoTrack();
        string cu2 = Cu2.Write(cue, 30000);
        Assert.True(Cu2.Verify(cue, 30000, Cu2.Parse(cu2)).Match);

        var shifted = Cu2.Parse(cu2.Replace("track02   05:02:00", "track02   05:03:00"));
        Assert.False(Cu2.Verify(cue, 30000, shifted).Match);
    }
}
