// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The unreadable-sector map is the one preservation fact a checksum cannot carry — a zero-filled hole hashes
/// like real data. These tests pin the map's core behaviour: it coalesces dropouts into runs, tells genuine
/// damage apart from harmless track-boundary holes, re-expresses absolute LBAs as offsets inside the track file
/// that holds them (pregap hits counted separately, lead-in/out hits kept only in the absolute list), survives a
/// JSON round-trip, and — folded into a preservation master — flips a holed dump from "complete" to INCOMPLETE
/// even when every member file hashes cleanly.
/// </summary>
public class BadSectorMapTests
{
    private static BadSectorMap Sample() => new()
    {
        Image = "disc.cdi",
        TotalSectors = 320,
        UnreadableLba = new long[] { 5, 6, 7, 105, 150, 999 },  // a 3-run then singletons
        BoundaryLba = new long[] { 105 },
        Note = "sample",
    };

    [Fact]
    public void Coalesces_runs_and_separates_damage_from_boundary_holes()
    {
        var m = Sample();
        Assert.Equal(6, m.Count);
        Assert.Equal(1, m.BoundaryCount);
        Assert.Equal(5, m.DamageCount);           // 6 total − 1 boundary
        Assert.True(m.DamagePresent);

        var runs = m.Runs();
        Assert.Equal(4, runs.Count);              // [5-7], [105], [150], [999]
        Assert.Equal(new BadSectorRun(5, 7), runs[0]);
        Assert.Equal(3, runs[0].Count);
        Assert.Equal("5-7 (×3)", runs[0].ToString());
        Assert.Equal("105", runs[1].ToString());
    }

    [Fact]
    public void A_boundary_only_map_is_not_flagged_as_damage()
    {
        var m = new BadSectorMap
        {
            Image = "disc.cdi", TotalSectors = 100,
            UnreadableLba = new long[] { 98, 99 },
            BoundaryLba = new long[] { 98, 99 },
        };
        Assert.False(m.DamagePresent);
        Assert.Equal(0, m.DamageCount);
        Assert.Contains("boundaries", m.Summary());
    }

    [Fact]
    public void Remaps_absolute_lbas_to_per_track_offsets()
    {
        var spans = new List<BadSectorMap.TrackSpan>
        {
            new(1, "d_track01.bin", StartLba: 0,   PregapSectors: 0,  LengthSectors: 100),
            new(2, "d_track02.bin", StartLba: 100, PregapSectors: 10, LengthSectors: 100),
            new(3, "d_track03.bin", StartLba: 210, PregapSectors: 10, LengthSectors: 100),
        };
        var re = Sample().RemapToTracks(spans, "d.cue");

        Assert.Equal("d.cue", re.Image);
        Assert.NotNull(re.ByTrack);

        var t1 = re.ByTrack!.Single(t => t.Track == 1);
        Assert.Equal(new long[] { 5, 6, 7 }, t1.WithinFileLba);   // body offsets = LBA (track starts at 0)
        Assert.Equal(0, t1.InPregap);

        var t2 = re.ByTrack!.Single(t => t.Track == 2);
        Assert.Equal(new long[] { 40 }, t2.WithinFileLba);        // LBA 150 → 150 − (100+10)
        Assert.Equal(1, t2.InPregap);                             // LBA 105 sits in track 2's pregap

        Assert.DoesNotContain(re.ByTrack!, t => t.Track == 3);    // no holes in track 3
        // LBA 999 is beyond every track (lead-out) — it stays in the absolute list, not the per-file view.
        Assert.Equal(6, re.Count);
    }

    [Fact]
    public void Survives_a_json_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), "dforge_bsm_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Sample().Save(path);
            var back = BadSectorMap.Load(path);
            Assert.Equal("dbs/1", back.FormatVersion);
            Assert.Equal(6, back.Count);
            Assert.Equal(5, back.DamageCount);
            Assert.Equal(new long[] { 5, 6, 7, 105, 150, 999 }, back.UnreadableLba);
            Assert.Equal("sample", back.Note);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_master_reads_the_sidecar_and_reports_the_dump_incomplete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_bsm_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var cue = Path.Combine(dir, "d.cue");
            File.WriteAllText(cue, "FILE \"d_track01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");
            File.WriteAllBytes(Path.Combine(dir, "d_track01.bin"), new byte[2352 * 10]);

            new BadSectorMap
            {
                Image = "d.cue", TotalSectors = 10,
                UnreadableLba = new long[] { 3 },   // one genuine hole
            }.Save(BadSectorMap.SidecarPath(cue));

            var master = PreservationMasterBuilder.Build(cue);

            Assert.NotNull(master.BadSectors);
            Assert.Equal(1, master.BadSectors!.Total);
            Assert.Equal(1, master.BadSectors.Damage);
            Assert.False(master.Complete);                          // holed dump is never "complete"…
            Assert.Contains("INCOMPLETE", master.CompletenessSummary);
        }
        finally { Directory.Delete(dir, true); }
    }
}
