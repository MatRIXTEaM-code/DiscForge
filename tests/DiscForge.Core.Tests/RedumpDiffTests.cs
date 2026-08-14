// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Dat;
using DiscForge.Core.Files;
using DiscForge.Core.Preservation;
using DiscForge.Core.Redump;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// redump-diff turns a Redump yes/no into a diagnosis. These tests build a two-track dump and an authentic DAT
/// from its own checksums, then confirm: an untouched dump verifies; a dump whose tracks are cut at the wrong
/// boundary (same total bytes, different per-track sizes) is diagnosed as a split problem, not lost data; a
/// track padded past its catalogued size is called out as padding; and a bad-sector sidecar marks the exact
/// track that can never match until the disc is re-read.
/// </summary>
public class RedumpDiffTests
{
    private const int SS = 2352;

    private static byte[] Program(int sectors)
    {
        var b = new byte[(long)sectors * SS];
        for (long i = 0; i < b.Length; i++) b[i] = (byte)((i * 7 + 3) % 251);
        return b;
    }

    // Writes correct d.cue + two bins and a DAT built from their real checksums. Returns (dir, cuePath, datPath).
    private static (string dir, string cue, string dat) BuildCorrect(string dir)
    {
        Directory.CreateDirectory(dir);
        var prog = Program(600);
        File.WriteAllBytes(Path.Combine(dir, "d_track01.bin"), prog.AsSpan(0, 300 * SS).ToArray());
        File.WriteAllBytes(Path.Combine(dir, "d_track02.bin"), prog.AsSpan(300 * SS, 300 * SS).ToArray());
        const string cue = "FILE \"d_track01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
                           "FILE \"d_track02.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n";
        File.WriteAllText(Path.Combine(dir, "d.cue"), cue);

        DatBuildRom Rom(string n)
        {
            var s = ImageChecksums.ComputeFile(Path.Combine(dir, n));
            return new DatBuildRom("Test Game (Europe)", n, s.Length, s.Crc32, s.Md5, s.Sha1);
        }
        var dat = DatBuilder.Build("Test", new[] { Rom("d.cue"), Rom("d_track01.bin"), Rom("d_track02.bin") });
        var datPath = Path.Combine(dir, "ref.dat");
        File.WriteAllText(datPath, dat);
        return (dir, Path.Combine(dir, "d.cue"), datPath);
    }

    [Fact]
    public void An_untouched_dump_verifies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_rd_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (_, cue, datPath) = BuildCorrect(dir);
            var r = RedumpDiffer.Diff(cue, DatFile.ParseText(File.ReadAllText(datPath)));
            Assert.True(r.Identified);
            Assert.True(r.Match);
            Assert.Equal(3, r.Verified);
            Assert.All(r.Roms, x => Assert.Equal(RomVerdict.Verified, x.Verdict));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_wrong_split_is_diagnosed_as_a_split_not_lost_data()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_rd_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (_, _, datPath) = BuildCorrect(dir);
            var prog = Program(600);
            // Same total (600 sectors) but cut 310/290 instead of 300/300.
            File.WriteAllBytes(Path.Combine(dir, "s_track01.bin"), prog.AsSpan(0, 310 * SS).ToArray());
            File.WriteAllBytes(Path.Combine(dir, "s_track02.bin"), prog.AsSpan(310 * SS, 290 * SS).ToArray());
            var scue = Path.Combine(dir, "s.cue");
            File.WriteAllText(scue,
                "FILE \"s_track01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
                "FILE \"s_track02.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n");

            var r = RedumpDiffer.Diff(scue, DatFile.ParseText(File.ReadAllText(datPath)), "Test Game (Europe)");
            Assert.False(r.Match);
            Assert.Contains(r.Roms, x => x.Verdict == RomVerdict.SizeMismatch);
            Assert.Contains(r.Roms.SelectMany(x => x.Explanations), e => e.Contains("split is wrong"));
            Assert.Contains(r.Recommendations, rec => rec.Contains("redump-cue"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_padded_track_is_called_out_as_padding()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_rd_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (_, _, datPath) = BuildCorrect(dir);
            var prog = Program(600);
            File.WriteAllBytes(Path.Combine(dir, "p_track01.bin"), prog.AsSpan(0, 300 * SS).ToArray());
            File.WriteAllBytes(Path.Combine(dir, "p_track02.bin"),
                prog.AsSpan(300 * SS, 300 * SS).ToArray().Concat(new byte[5 * SS]).ToArray());  // +5 sectors
            var pcue = Path.Combine(dir, "p.cue");
            File.WriteAllText(pcue,
                "FILE \"p_track01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
                "FILE \"p_track02.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n");

            var r = RedumpDiffer.Diff(pcue, DatFile.ParseText(File.ReadAllText(datPath)), "Test Game (Europe)");
            Assert.False(r.Match);
            var t2 = r.Roms.First(x => x.ActualName == "p_track02.bin");
            Assert.Equal(RomVerdict.SizeMismatch, t2.Verdict);
            Assert.Contains(t2.Explanations, e => e.Contains("5 sector(s) LONGER"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_bad_sector_sidecar_marks_the_mismatched_track_that_can_never_match()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_rd_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (_, cue, datPath) = BuildCorrect(dir);
            // A genuine hole makes the track's content differ from the DAT — flip a byte inside track 1 so it is
            // a real ContentMismatch, exactly as a zero-filled unreadable sector would be.
            var t1 = Path.Combine(dir, "d_track01.bin");
            var bytes = File.ReadAllBytes(t1);
            bytes[50 * SS] ^= 0xFF;
            File.WriteAllBytes(t1, bytes);

            var bad = new BadSectorMap
            {
                Image = "d.cue", TotalSectors = 600, UnreadableLba = new long[] { 50 },
            }.RemapToTracks(new List<BadSectorMap.TrackSpan>
            {
                new(1, "d_track01.bin", 0, 0, 300),
                new(2, "d_track02.bin", 300, 0, 300),
            }, "d.cue");

            var r = RedumpDiffer.Diff(cue, DatFile.ParseText(File.ReadAllText(datPath)), null, bad);
            Assert.False(r.Match);
            var track1 = r.Roms.First(x => x.ActualName == "d_track01.bin");
            Assert.Equal(RomVerdict.ContentMismatch, track1.Verdict);
            Assert.Contains(track1.Explanations, e => e.Contains("never match", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(r.Recommendations, rec => rec.Contains("re-read"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
