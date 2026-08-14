// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Mmc;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The DAO cue-sheet builder (for direct-SPTI RAW writing). These validate the STRUCTURE the
/// drive is handed — lead-in POINT entries, per-track index positions, lead-out, and the
/// control/MSF math. Exact byte acceptance of ambiguous fields is confirmed separately against
/// real hardware via `burn-raw --engine spti --test-cue` (non-destructive).
/// </summary>
public class DaoCueSheetTests
{
    private static DiscLayout TwoAudioTracks()
    {
        var bin = new MemoryStream(new byte[1500 * 2352]);   // 2 × 750-sector audio tracks
        const string cue = """
            FILE "a.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 01 00:10:00
            """;
        return DiscLayout.FromCue(CueSheet.Parse(cue), _ => bin);
    }

    [Fact]
    public void Lead_in_points_carry_first_last_track_and_lead_out()
    {
        var e = DaoCueSheet.BuildEntries(TwoAudioTracks());

        // A0: first track = 1; A1: last track = 2; A2: lead-out at 00:22:00 (150 pregap + 1500).
        Assert.Equal(0xA0, e[0].IndexOrPoint);
        Assert.Equal(0x01, e[0].Min);                    // BCD first track
        Assert.Equal(0x01, e[0].CtlAdr);                 // audio control (0) + ADR 1
        Assert.Equal(0xA1, e[1].IndexOrPoint);
        Assert.Equal(0x02, e[1].Min);                    // BCD last track
        Assert.Equal(0xA2, e[2].IndexOrPoint);
        Assert.Equal(0x00, e[2].Min);                    // lead-out at 00:22:00 → 0 min, 22 sec
        Assert.Equal(0x22, e[2].Sec);
    }

    [Fact]
    public void Track_starts_land_at_the_right_absolute_time()
    {
        var e = DaoCueSheet.BuildEntries(TwoAudioTracks());

        var t1 = e.First(x => x.TrackNumber == 1 && x.IndexOrPoint == 1);
        Assert.Equal(0x00, t1.Min);                      // track 1 index 1 at 00:02:00 (0 min, 2 sec)
        Assert.Equal(0x02, t1.Sec);
        var t2 = e.First(x => x.TrackNumber == 2 && x.IndexOrPoint == 1);
        Assert.Equal(0x00, t2.Min);                      // track 2 index 1 at 00:12:00 (0 min, 12 sec BCD)
        Assert.Equal(0x12, t2.Sec);
    }

    [Fact]
    public void Track_one_has_a_pregap_index_zero_entry()
    {
        var e = DaoCueSheet.BuildEntries(TwoAudioTracks());
        var pregap = e.First(x => x.TrackNumber == 1 && x.IndexOrPoint == 0);
        Assert.Equal(0x00, pregap.Min);                  // pregap starts at 00:00:00
        Assert.Equal(0x00, pregap.Sec);
    }

    [Fact]
    public void There_is_a_lead_out_track_and_the_bytes_are_a_multiple_of_eight()
    {
        var layout = TwoAudioTracks();
        var e = DaoCueSheet.BuildEntries(layout);
        Assert.Contains(e, x => x.TrackNumber == 0xAA);

        var bytes = DaoCueSheet.Build(layout);
        Assert.Equal(e.Count * 8, bytes.Length);
        Assert.Equal(0, bytes.Length % 8);
    }

    [Fact]
    public void A_data_track_carries_the_data_control_bit()
    {
        var bin = new MemoryStream(new byte[600 * 2048]);
        const string cue = """
            FILE "d.bin" BINARY
              TRACK 01 MODE1/2048
                INDEX 01 00:00:00
            """;
        var layout = DiscLayout.FromCue(CueSheet.Parse(cue), _ => bin);
        var e = DaoCueSheet.BuildEntries(layout);

        var t1 = e.First(x => x.TrackNumber == 1 && x.IndexOrPoint == 1);
        Assert.Equal(0x41, t1.CtlAdr);                   // data control (4) + ADR 1
    }
}
