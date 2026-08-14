// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Convert;
using DiscForge.Core.Cue;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the ODE export planner: a PSIO plan puts the track bin, the cue and a generated CU2 in a game
/// folder; the folder name is made filesystem-safe; and the CU2 content is the same track map the cu2 command
/// would emit.
/// </summary>
public class OdeExportTests
{
    private static CueSheet TwoTrack() => CueSheet.Parse(
        "FILE \"GAME.bin\" BINARY\n" +
        "  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
        "  TRACK 02 AUDIO\n    INDEX 00 00:58:00\n    INDEX 01 01:00:00\n");

    [Fact]
    public void Psio_plan_lays_out_bin_cue_and_generated_cu2()
    {
        var cue = TwoTrack();
        var plan = OdeExporter.Psio("GAME.cue", cue, totalSectors: 7500, "Cool Game (USA)");

        Assert.Equal("psio", plan.Target);
        Assert.Equal("Cool Game (USA)", plan.GameFolder);

        Assert.Contains(plan.Ops, o => o.Kind == "copy" && o.DestRelPath.EndsWith("GAME.bin", System.StringComparison.Ordinal));
        Assert.Contains(plan.Ops, o => o.Kind == "copy" && o.DestRelPath.EndsWith("GAME.cue", System.StringComparison.Ordinal));

        var cu2 = Assert.Single(plan.Ops, o => o.Kind == "write" && o.DestRelPath.EndsWith("GAME.cu2", System.StringComparison.Ordinal));
        Assert.Equal(Cu2.Write(cue, 7500), cu2.Content);
        Assert.StartsWith("ntracks 2", cu2.Content);
    }

    [Fact]
    public void The_game_folder_is_made_filesystem_safe()
    {
        Assert.Equal("GAME", OdeExporter.SanitizeFolder(""));
        Assert.Equal("GAME", OdeExporter.SanitizeFolder("   "));
        string cleaned = OdeExporter.SanitizeFolder("Rad/Game: Vol*2?");
        Assert.DoesNotContain('/', cleaned);
        Assert.DoesNotContain(':', cleaned);
        Assert.DoesNotContain('*', cleaned);
        Assert.DoesNotContain('?', cleaned);
    }

    [Fact]
    public void Duplicate_track_files_are_copied_once()
    {
        // A single-bin, two-track cue references GAME.bin twice; it should be copied a single time.
        var cue = CueSheet.Parse(
            "FILE \"GAME.bin\" BINARY\n" +
            "  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n    INDEX 01 01:00:00\n");
        var plan = OdeExporter.Psio("GAME.cue", cue, 7500, "G");
        Assert.Equal(1, plan.Ops.Count(o => o.Kind == "copy" && o.DestRelPath.EndsWith("GAME.bin", System.StringComparison.Ordinal)));
    }
}
