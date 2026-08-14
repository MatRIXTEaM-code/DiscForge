// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The emulation-readiness analyzer grades whether a dump has what an emulator needs to run — a
/// higher-level question than physical completeness. These drive its pure core with synthetic cue
/// layouts and completeness facts, covering each verdict: a clean raw disc is READY, a missing file
/// is NOT READY, and cooked data / a missing subchannel produce the right caveats and notes.
/// </summary>
public class EmulationReadinessTests
{
    private static CueTrack Track(int n, CueTrackType type, string file, Msf? pregap = null, bool index0 = false)
    {
        var indices = new List<CueIndex>();
        if (index0) indices.Add(new CueIndex(0, new Msf(0, 0, 0)));
        indices.Add(new CueIndex(1, new Msf(0, index0 ? 2 : 0, 0)));
        return new CueTrack { Number = n, Type = type, File = file, Pregap = pregap, Indices = indices };
    }

    private static CueSheet Cue(params CueTrack[] tracks) => new() { Tracks = tracks };

    private static DumpCompletenessResult Completeness(
        IReadOnlyList<string> bins, bool allPresent = true, bool wholeSector = true,
        bool sub = false, bool subMatches = false) => new()
    {
        TrackCount = 1,
        SessionCount = 1,
        TotalSectors = 1000,
        BinFiles = bins,
        AllBinsPresent = allPresent,
        WholeSector = wholeSector,
        SubchannelPresent = sub,
        SubchannelMatches = subMatches,
        Gaps = Array.Empty<string>(),
        NotRepresentable = Array.Empty<string>(),
    };

    [Fact]
    public void A_raw_data_disc_with_all_files_is_ready()
    {
        var cue = Cue(Track(1, CueTrackType.Mode2_2352, "game.bin"));
        var r = EmulationReadiness.Analyze(cue, Completeness(new[] { "game.bin" }));

        Assert.Equal(EmuReadiness.Ready, r.Grade);
        Assert.Contains(r.Findings, f => f.Aspect == "data-mode" && f.Severity == EmuSeverity.Ok);
        Assert.Contains("READY", r.Summary);
    }

    [Fact]
    public void A_missing_file_is_not_ready()
    {
        var cue = Cue(Track(1, CueTrackType.Mode2_2352, "game.bin"));
        var r = EmulationReadiness.Analyze(cue, Completeness(new[] { "game.bin" }, allPresent: false));

        Assert.Equal(EmuReadiness.NotReady, r.Grade);
        Assert.Contains(r.Blockers, f => f.Aspect == "files");
    }

    [Fact]
    public void A_cooked_2048_data_track_is_ready_with_caveats()
    {
        var cue = Cue(Track(1, CueTrackType.Mode1_2048, "game.bin"));
        var r = EmulationReadiness.Analyze(cue, Completeness(new[] { "game.bin" }));

        Assert.Equal(EmuReadiness.ReadyWithCaveats, r.Grade);
        Assert.Contains(r.Warnings, f => f.Aspect == "data-mode");
    }

    [Fact]
    public void A_mixed_mode_disc_notes_audio_and_missing_subchannel()
    {
        var cue = Cue(
            Track(1, CueTrackType.Mode2_2352, "game.bin"),
            Track(2, CueTrackType.Audio, "game.bin", index0: true),
            Track(3, CueTrackType.Audio, "game.bin", index0: true));
        var r = EmulationReadiness.Analyze(cue, Completeness(new[] { "game.bin" }));

        Assert.Equal(EmuReadiness.Ready, r.Grade);   // notes don't downgrade
        Assert.Contains(r.Findings, f => f.Aspect == "audio" && f.Detail.Contains("2 CD-DA"));
        Assert.Contains(r.Notes, f => f.Aspect == "subchannel");
    }

    [Fact]
    public void A_present_matching_subchannel_is_reported_ok()
    {
        var cue = Cue(Track(1, CueTrackType.Mode2_2352, "game.bin"));
        var r = EmulationReadiness.Analyze(cue, Completeness(new[] { "game.bin" }, sub: true, subMatches: true));

        Assert.Equal(EmuReadiness.Ready, r.Grade);
        Assert.Contains(r.Findings, f => f.Aspect == "subchannel" && f.Severity == EmuSeverity.Ok);
    }

    [Fact]
    public void A_mismatched_subchannel_is_a_caveat()
    {
        var cue = Cue(Track(1, CueTrackType.Mode2_2352, "game.bin"));
        var r = EmulationReadiness.Analyze(cue, Completeness(new[] { "game.bin" }, sub: true, subMatches: false));

        Assert.Equal(EmuReadiness.ReadyWithCaveats, r.Grade);
        Assert.Contains(r.Warnings, f => f.Aspect == "subchannel");
    }
}
