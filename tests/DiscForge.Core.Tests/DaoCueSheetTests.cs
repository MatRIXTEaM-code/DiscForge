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
/// drive is handed — the single generic lead-in entry, per-track index positions, lead-out, and
/// the control/MSF math. Exact byte acceptance of ambiguous fields is confirmed separately
/// against real hardware via `burn-raw --engine spti --test-cue` (non-destructive).
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
    public void Lead_in_is_a_single_generic_entry_at_zero()
    {
        // Matches cdrdao's GenericMMC::createCueSheet: ONE lead-in entry (TNO=0, Index=0,
        // MSF=00:00:00), not the three Red-Book-style POINT (A0/A1/A2) entries an earlier
        // version of this file sent — those were rejected outright by a real drive.
        var e = DaoCueSheet.BuildEntries(TwoAudioTracks());

        Assert.Equal(0x00, e[0].TrackNumber);
        Assert.Equal(0x00, e[0].IndexOrPoint);
        Assert.Equal(0x01, e[0].DataForm);                // LeadInOutForm(Audio) = 0x01
        Assert.Equal(0x01, e[0].CtlAdr);                  // audio control (0) + ADR 1
        Assert.Equal(0x00, e[0].Min);
        Assert.Equal(0x00, e[0].Sec);
        Assert.Equal(0x00, e[0].Frame);
    }

    [Fact]
    public void Lead_out_lands_at_the_right_absolute_time()
    {
        // Lead-out at 00:22:00 (150-sector pregap + 1500 program sectors).
        var e = DaoCueSheet.BuildEntries(TwoAudioTracks());
        var leadOut = e.First(x => x.TrackNumber == 0xAA);

        Assert.Equal(0x01, leadOut.IndexOrPoint);
        Assert.Equal(0x00, leadOut.Min);
        Assert.Equal(0x22, leadOut.Sec);
        Assert.Equal(0x01, leadOut.DataForm);             // LeadInOutForm(Audio) = 0x01
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

    [Fact]
    public void A_mode1_track_uses_cdrdaos_data_form_code_not_the_control_nibble()
    {
        // DATA FORM is its own byte, distinct from (and easy to confuse with) the CTL/ADR
        // control nibble. Two earlier guesses here were both rejected by a real drive with
        // ASC 0x26/0x00: 0x10-for-everything, then 0x08 (from an unreliable PDF-summary
        // extraction). This locks in cdrdao's proven value for MODE1: 0x10.
        var bin = new MemoryStream(new byte[600 * 2048]);
        const string cue = """
            FILE "d.bin" BINARY
              TRACK 01 MODE1/2048
                INDEX 01 00:00:00
            """;
        var layout = DiscLayout.FromCue(CueSheet.Parse(cue), _ => bin);
        var e = DaoCueSheet.BuildEntries(layout);

        var t1 = e.First(x => x.TrackNumber == 1 && x.IndexOrPoint == 1);
        Assert.Equal(0x10, t1.DataForm);
    }

    [Fact]
    public void A_mode2_track_uses_cdrdaos_xa_data_form_code()
    {
        // cdrdao's MODE2_RAW bucket ("assume it contains XA sectors") = 0x20 — DiscForge's
        // RawTrackMode.Mode2 (raw 2352-byte sectors) maps onto exactly that bucket, since
        // DiscForge doesn't separately distinguish plain Mode2 from CD-XA Mode2 forms.
        var bin = new MemoryStream(new byte[600 * 2352]);
        const string cue = """
            FILE "d.bin" BINARY
              TRACK 01 MODE2/2352
                INDEX 01 00:00:00
            """;
        var layout = DiscLayout.FromCue(CueSheet.Parse(cue), _ => bin);
        var e = DaoCueSheet.BuildEntries(layout);

        var t1 = e.First(x => x.TrackNumber == 1 && x.IndexOrPoint == 1);
        Assert.Equal(0x20, t1.DataForm);
    }
}
