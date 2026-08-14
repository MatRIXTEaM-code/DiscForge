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
/// cue-repair fixes the everyday ways a cue breaks and re-emits a clean sheet. The test builds a broken cue
/// beside its real track files — a FILE line with the wrong case, a second FILE naming "track2.bin" when
/// "track02.bin" is on disk, a track numbered 03 instead of 02, and a missing INDEX 01 — and confirms every one
/// is repaired (the mis-cased name, the orphan-file reconciliation, the renumber, the added index) with nothing
/// left unresolved, and that the repaired cue parses and references the real files.
/// </summary>
public class CueRepairTests
{
    [Fact]
    public void Fixes_file_refs_numbering_and_missing_index()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_cr_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "track01.bin"), new byte[2352 * 2]);
            File.WriteAllBytes(Path.Combine(dir, "track02.bin"), new byte[2352]);
            var cue = Path.Combine(dir, "broken.cue");
            File.WriteAllText(cue,
                "FILE \"TRACK01.BIN\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
                "FILE \"track2.bin\" BINARY\n  TRACK 03 AUDIO\n");

            var r = CueRepair.Repair(cue);

            Assert.True(r.Changed);
            Assert.Empty(r.Unresolved);
            Assert.Contains(r.Changes, c => c.Contains("track01.bin"));                 // case fix
            Assert.Contains(r.Changes, c => c.Contains("track2.bin") && c.Contains("track02.bin")); // orphan reconciliation
            Assert.Contains(r.Changes, c => c.Contains("INDEX 01"));                    // added index
            Assert.Contains(r.Changes, c => c.Contains("→ 02"));                        // renumber

            var fixedSheet = CueSheet.Parse(r.CueText);
            Assert.Equal(new[] { 1, 2 }, fixedSheet.Tracks.Select(t => t.Number).ToArray());
            Assert.Equal(new[] { "track01.bin", "track02.bin" }, fixedSheet.Tracks.Select(t => t.File).ToArray());
            Assert.All(fixedSheet.Tracks, t => Assert.Contains(t.Indices, i => i.Number == 1));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_clean_cue_needs_no_repair()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_cr_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "game.bin"), new byte[2352 * 10]);
            var cue = Path.Combine(dir, "game.cue");
            File.WriteAllText(cue, "FILE \"game.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");

            var r = CueRepair.Repair(cue);
            Assert.False(r.Changed);
            Assert.Empty(r.Unresolved);
        }
        finally { Directory.Delete(dir, true); }
    }
}
