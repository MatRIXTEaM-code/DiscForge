// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using DiscForge.Core.Cue;
using DiscForge.Core.Dat;
using DiscForge.Core.Files;
using DiscForge.Core.Preservation;
using DiscForge.Core.Raw;
using DiscForge.Core.Redump;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// redump-prep turns a raw capture into a submission-ready set in one step. These tests build a gaps-folded
/// capture (tracks cut at INDEX 01) with a matching subchannel, and confirm the prep re-cuts to Redump's
/// boundaries byte-for-byte, carries a bad-sector map forward (re-expressed against the new split), blocks the
/// submission when there is genuine damage, and reports SUBMISSION-READY with a Redump match when the prepared
/// set lines up with a grouped DAT.
/// </summary>
public class RedumpPrepTests
{
    private const int SS = 2352;

    private static byte[] Program(int sectors)
    {
        var b = new byte[(long)sectors * SS];
        for (long i = 0; i < b.Length; i++) b[i] = (byte)((i * 7 + 3) % 251);
        return b;
    }

    // A gaps-folded capture: t1=450, t2=400, t3=150 (INDEX-01 split); subchannel has 150-sector pregaps for t2,t3.
    private static string BuildCapture(string dir)
    {
        Directory.CreateDirectory(dir);
        var prog = Program(1000);
        void W(string n, int s, int c) => File.WriteAllBytes(Path.Combine(dir, n), prog.AsSpan(s * SS, c * SS).ToArray());
        W("g_track01.bin", 0, 450); W("g_track02.bin", 450, 400); W("g_track03.bin", 850, 150);
        var cue = Path.Combine(dir, "g.cue");
        File.WriteAllText(cue,
            "FILE \"g_track01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "FILE \"g_track02.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n" +
            "FILE \"g_track03.bin\" BINARY\n  TRACK 03 AUDIO\n    INDEX 01 00:00:00\n");

        var sub = new byte[1000 * 96];
        void Q(int l, QControl c, int t, int i)
        {
            long a = l + 150;
            SubQ.Position(c, t, i, new Msf(0, 0, 0), new Msf((int)(a / 4500), (int)(a / 75 % 60), (int)(a % 75)))
                .CopyTo(sub.AsSpan(l * 96 + 12, 12));
        }
        for (int l = 0; l < 300; l++) Q(l, QControl.Data, 1, 1);
        for (int l = 300; l < 450; l++) Q(l, QControl.None, 2, 0);
        for (int l = 450; l < 700; l++) Q(l, QControl.None, 2, 1);
        for (int l = 700; l < 850; l++) Q(l, QControl.None, 3, 0);
        for (int l = 850; l < 1000; l++) Q(l, QControl.None, 3, 1);
        File.WriteAllBytes(Path.Combine(dir, "g.sub"), sub);
        return cue;
    }

    private static string Sha(IEnumerable<string> files) =>
        System.Convert.ToHexString(SHA256.HashData(files.SelectMany(File.ReadAllBytes).ToArray()));

    [Fact]
    public void Recut_is_byte_preserving_and_submission_text_is_written()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_pp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cue = BuildCapture(dir);
            var outDir = Path.Combine(dir, "out");
            var r = RedumpPrep.Prepare(cue, outDir, new RedumpPrepOptions { SubPath = Path.Combine(dir, "g.sub") });

            Assert.True(r.ReSplit);
            Assert.True(File.Exists(r.SubmissionInfoPath));
            Assert.Contains(r.Checks, c => c.Name == "track split" && c.Status == PrepStatus.Pass);

            var capture = new[] { "g_track01.bin", "g_track02.bin", "g_track03.bin" }.Select(f => Path.Combine(dir, f));
            var recut = new[] { "g_track01.bin", "g_track02.bin", "g_track03.bin" }.Select(f => Path.Combine(outDir, f));
            Assert.Equal(Sha(capture), Sha(recut));   // same program area, only the cuts moved
            Assert.Equal(new long[] { 300, 400, 300 }, recut.Select(f => new FileInfo(f).Length / SS).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Genuine_damage_blocks_the_submission()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_pp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cue = BuildCapture(dir);
            new BadSectorMap { Image = "g.cue", TotalSectors = 1000, UnreadableLba = new long[] { 500 } }
                .Save(BadSectorMap.SidecarPath(cue));

            var r = RedumpPrep.Prepare(cue, Path.Combine(dir, "out"),
                new RedumpPrepOptions { SubPath = Path.Combine(dir, "g.sub") });

            Assert.False(r.SubmissionReady);
            Assert.Contains(r.Checks, c => c.Name == "unreadable sectors" && c.Status == PrepStatus.Fail);
            // The carried map is re-expressed against the new split (LBA 500 lands in track 2 of the re-cut).
            var carried = BadSectorMap.Load(BadSectorMap.SidecarPath(Path.Combine(dir, "out", "g.cue")));
            Assert.Contains(carried.ByTrack!, t => t.Track == 2);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_clean_prepared_set_is_submission_ready_and_matches_a_grouped_dat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_pp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cue = BuildCapture(dir);
            var outDir = Path.Combine(dir, "out");
            RedumpPrep.Prepare(cue, outDir, new RedumpPrepOptions { SubPath = Path.Combine(dir, "g.sub") });

            // A grouped DAT built from the prepared files (all under one game name).
            DatBuildRom Rom(string n)
            {
                var s = ImageChecksums.ComputeFile(Path.Combine(outDir, n));
                return new DatBuildRom("MyGame (Europe)", n, s.Length, s.Crc32, s.Md5, s.Sha1);
            }
            var datPath = Path.Combine(dir, "grouped.dat");
            File.WriteAllText(datPath, DatBuilder.Build("Ref",
                new[] { Rom("g.cue"), Rom("g_track01.bin"), Rom("g_track02.bin"), Rom("g_track03.bin") }));

            var r = RedumpPrep.Prepare(cue, Path.Combine(dir, "out3"),
                new RedumpPrepOptions { SubPath = Path.Combine(dir, "g.sub"), DatPath = datPath });

            Assert.True(r.SubmissionReady);
            Assert.Contains(r.Checks, c => c.Name == "Redump match" && c.Status == PrepStatus.Pass);
        }
        finally { Directory.Delete(dir, true); }
    }
}
