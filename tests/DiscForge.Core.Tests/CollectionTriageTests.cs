// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Collection;
using DiscForge.Core.Dat;
using DiscForge.Core.Files;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// collection-triage folds DiscForge's per-disc checks into one ranked, collection-level worklist. This test
/// builds a folder with one dump of every kind — a Redump-verified copy, a content duplicate of it, a holed
/// (incomplete) dump, a wrong-split dump whose cue still matches the catalogued entry, and an unidentified dump
/// — from authentic hashes, and asserts each is classified and prioritised correctly.
/// </summary>
public class CollectionTriageTests
{
    private const int SS = 2352;

    private static byte[] Program(int sectors)
    {
        var b = new byte[(long)sectors * SS];
        for (long i = 0; i < b.Length; i++) b[i] = (byte)((i * 7 + 3) % 251);
        return b;
    }

    [Fact]
    public void Classifies_every_kind_of_dump_and_ranks_worst_first()
    {
        var prog = Program(700);
        byte[] Slice(int s, int c) => prog.AsSpan(s * SS, c * SS).ToArray();

        var root = Path.Combine(Path.GetTempPath(), "dforge_ct_" + Guid.NewGuid().ToString("N"));
        try
        {
            var roms = new List<DatBuildRom>();
            DatBuildRom Rom(string game, string dir, string n)
            {
                var s = ImageChecksums.ComputeFile(Path.Combine(dir, n));
                return new DatBuildRom(game, n, s.Length, s.Crc32, s.Md5, s.Sha1);
            }
            void Cue(string dir, string name, params string[] lines) =>
                File.WriteAllText(Path.Combine(dir, name), string.Concat(lines));

            // Verified
            var vd = Path.Combine(root, "verified"); Directory.CreateDirectory(vd);
            File.WriteAllBytes(Path.Combine(vd, "v_track01.bin"), Slice(0, 200));
            Cue(vd, "v.cue", "FILE \"v_track01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");
            roms.Add(Rom("Verified Game", vd, "v.cue")); roms.Add(Rom("Verified Game", vd, "v_track01.bin"));

            // Duplicate of Verified (same bin bytes)
            var dd = Path.Combine(root, "dup"); Directory.CreateDirectory(dd);
            File.WriteAllBytes(Path.Combine(dd, "d_track01.bin"), Slice(0, 200));
            Cue(dd, "d.cue", "FILE \"d_track01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");

            // Incomplete (holed)
            var hd = Path.Combine(root, "holed"); Directory.CreateDirectory(hd);
            File.WriteAllBytes(Path.Combine(hd, "h_track01.bin"), Slice(300, 150));
            var hcue = Path.Combine(hd, "h.cue");
            Cue(hd, "h.cue", "FILE \"h_track01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");
            new BadSectorMap { Image = "h.cue", TotalSectors = 150, UnreadableLba = new long[] { 42 } }
                .Save(BadSectorMap.SidecarPath(hcue));

            // Needs re-cut: DAT from the correct 300/300 split; on-disk bins re-cut 310/290 (same total, same cue).
            var sd = Path.Combine(root, "split"); Directory.CreateDirectory(sd);
            File.WriteAllBytes(Path.Combine(sd, "s_track01.bin"), Slice(0, 300));
            File.WriteAllBytes(Path.Combine(sd, "s_track02.bin"), Slice(300, 300));
            Cue(sd, "s.cue",
                "FILE \"s_track01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n",
                "FILE \"s_track02.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n");
            roms.Add(Rom("Split Game", sd, "s.cue"));
            roms.Add(Rom("Split Game", sd, "s_track01.bin"));
            roms.Add(Rom("Split Game", sd, "s_track02.bin"));
            File.WriteAllBytes(Path.Combine(sd, "s_track01.bin"), Slice(0, 310));   // wrong split, same total
            File.WriteAllBytes(Path.Combine(sd, "s_track02.bin"), Slice(310, 290));

            // Unidentified
            var ud = Path.Combine(root, "unknown"); Directory.CreateDirectory(ud);
            File.WriteAllBytes(Path.Combine(ud, "u_track01.bin"), Slice(500, 80));
            Cue(ud, "u.cue", "FILE \"u_track01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");

            var datPath = Path.Combine(root, "redump.dat");
            File.WriteAllText(datPath, DatBuilder.Build("Ref", roms));

            var report = CollectionTriage.Scan(root, DatFile.ParseText(File.ReadAllText(datPath)));

            TriageStatus StatusOf(string name) => report.Entries.First(e => e.Name == name).Status;
            Assert.Equal(TriageStatus.Verified, StatusOf("v.cue"));
            Assert.Equal(TriageStatus.Duplicate, StatusOf("d.cue"));
            Assert.Equal(TriageStatus.Incomplete, StatusOf("h.cue"));
            Assert.Equal(TriageStatus.NeedsRecut, StatusOf("s.cue"));
            Assert.Equal(TriageStatus.NeedsAttention, StatusOf("u.cue"));

            // Ranked worst-first: the incomplete dump leads, the verified dump trails.
            Assert.Equal("h.cue", report.Entries[0].Name);
            Assert.Equal(TriageStatus.Verified, report.Entries[^1].Status);

            // The dashboard renders and mentions the games.
            var html = CollectionTriage.RenderHtml(report);
            Assert.Contains("collection triage", html);
            Assert.Contains("Verified Game", html);
        }
        finally { Directory.Delete(root, true); }
    }
}
