// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the dump-completeness certificate: it reconciles the cue track layout, the data file size and
/// the subchannel sidecar's sector count, and reports concrete gaps (mismatched subchannel, non-whole-sector
/// data, missing files) while noting what a bin/cue can never physically hold.
/// </summary>
public class DumpCompletenessTests : IDisposable
{
    private readonly string _dir;

    public DumpCompletenessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "compt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteCue()
    {
        string cue = Path.Combine(_dir, "game.cue");
        // Index times are deliberately small (and inside every test's file size below) so the
        // per-sector coverage proof — which now checks each INDEX 01 against the file's actual
        // sector count — passes on a fixture that was never meant to exercise that check.
        File.WriteAllText(cue,
            "FILE \"game.bin\" BINARY\n" +
            "  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n    INDEX 00 00:00:10\n    INDEX 01 00:00:12\n");
        return cue;
    }

    [Fact]
    public void A_whole_image_with_matching_subchannel_is_complete()
    {
        var cue = WriteCue();
        File.WriteAllBytes(Path.Combine(_dir, "game.bin"), new byte[1000 * 2352]);
        File.WriteAllBytes(Path.Combine(_dir, "game.sub"), new byte[1000 * 96]);

        var r = DumpCompleteness.Check(cue);
        Assert.Equal(2, r.TrackCount);
        Assert.Equal(1000, r.TotalSectors);
        Assert.True(r.AllBinsPresent);
        Assert.True(r.WholeSector);
        Assert.True(r.SubchannelPresent);
        Assert.True(r.SubchannelMatches);
        Assert.True(r.Complete);
        Assert.True(r.CoverageProven);
        Assert.Contains(r.NotRepresentable, n => n.Contains("lead-in"));
    }

    [Fact]
    public void A_track_index_pointing_past_the_end_of_its_file_breaks_the_coverage_proof()
    {
        string cue = Path.Combine(_dir, "game.cue");
        File.WriteAllText(cue,
            "FILE \"game.bin\" BINARY\n" +
            "  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n    INDEX 00 00:30:00\n    INDEX 01 00:32:00\n");   // 2400 sectors — file below has 1000
        File.WriteAllBytes(Path.Combine(_dir, "game.bin"), new byte[1000 * 2352]);

        var r = DumpCompleteness.Check(cue);
        Assert.False(r.CoverageProven);
        Assert.False(r.Complete);
        Assert.Contains(r.Gaps, g => g.Contains("track 2") && g.Contains("outside"));
    }

    [Fact]
    public void Two_tracks_claiming_the_same_or_reversed_start_sector_breaks_the_coverage_proof()
    {
        string cue = Path.Combine(_dir, "game.cue");
        // Track 2 starts no later than track 1 — an out-of-order/duplicated INDEX 01 that a
        // total-sector-count check alone would never catch (the file is still the right size).
        File.WriteAllText(cue,
            "FILE \"game.bin\" BINARY\n" +
            "  TRACK 01 MODE1/2352\n    INDEX 01 00:00:20\n" +
            "  TRACK 02 AUDIO\n    INDEX 01 00:00:10\n");
        File.WriteAllBytes(Path.Combine(_dir, "game.bin"), new byte[1000 * 2352]);

        var r = DumpCompleteness.Check(cue);
        Assert.False(r.CoverageProven);
        Assert.False(r.Complete);
        Assert.Contains(r.Gaps, g => g.Contains("out of order or duplicated"));
    }

    [Fact]
    public void A_subchannel_covering_fewer_sectors_is_flagged()
    {
        var cue = WriteCue();
        File.WriteAllBytes(Path.Combine(_dir, "game.bin"), new byte[1000 * 2352]);
        File.WriteAllBytes(Path.Combine(_dir, "game.sub"), new byte[999 * 96]);

        var r = DumpCompleteness.Check(cue);
        Assert.False(r.SubchannelMatches);
        Assert.False(r.Complete);
        Assert.Contains(r.Gaps, g => g.Contains("subchannel"));
    }

    [Fact]
    public void A_non_whole_sector_data_file_is_flagged()
    {
        var cue = WriteCue();
        File.WriteAllBytes(Path.Combine(_dir, "game.bin"), new byte[1000 * 2352 - 5]);
        var r = DumpCompleteness.Check(cue);
        Assert.False(r.WholeSector);
        Assert.Contains(r.Gaps, g => g.Contains("whole"));
    }

    [Fact]
    public void A_missing_data_file_is_flagged()
    {
        var cue = WriteCue();   // no game.bin written
        var r = DumpCompleteness.Check(cue);
        Assert.False(r.AllBinsPresent);
        Assert.Contains(r.Gaps, g => g.Contains("missing"));
    }

    [Fact]
    public void A_dump_without_a_subchannel_is_structurally_complete_but_notes_the_absence()
    {
        var cue = WriteCue();
        File.WriteAllBytes(Path.Combine(_dir, "game.bin"), new byte[1000 * 2352]);
        var r = DumpCompleteness.Check(cue);
        Assert.True(r.Complete);
        Assert.False(r.SubchannelPresent);
        Assert.Contains(r.NotRepresentable, n => n.Contains("subchannel"));
    }
}
