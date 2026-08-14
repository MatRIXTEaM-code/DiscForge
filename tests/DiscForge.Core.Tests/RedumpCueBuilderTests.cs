// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using DiscForge.Core.Convert;
using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// redump-cue re-cuts a split bin/cue at the subchannel's INDEX 00 boundaries. The invariant that matters for a
/// preservation tool is that the re-cut is byte-exact: concatenating the new bins must reproduce the original
/// program area bit for bit, whatever the boundaries do. These tests build a small three-track disc (a data
/// track then two audio tracks with pregaps), whose bins are cut the "gaps folded into the previous file" way,
/// with a matching synthetic subchannel — and assert the re-cut preserves every byte, emits INDEX 00/01 at the
/// right places, and handles a dropped-Q-frame (149-sector) pregap both by reporting it and by snapping it.
/// </summary>
public class RedumpCueBuilderTests
{
    private const int SS = 2352;

    // A disc: track1 data body 0..299; track2 audio pregap [g2start..449] body 450..699; track3 audio pregap
    // 700..849 body 850..999. Bins are cut at INDEX 01 (original style): 450 / 400 / 150 sectors.
    private static string BuildDisc(string dir, int track2PregapStart)
    {
        Directory.CreateDirectory(dir);
        const int total = 1000;
        var program = new byte[(long)total * SS];
        for (long i = 0; i < program.Length; i++) program[i] = (byte)(i % 251);

        void WriteBin(string name, int startLba, int sectors) =>
            File.WriteAllBytes(Path.Combine(dir, name), program.AsSpan(startLba * SS, sectors * SS).ToArray());
        WriteBin("d_track01.bin", 0, 450);
        WriteBin("d_track02.bin", 450, 400);
        WriteBin("d_track03.bin", 850, 150);

        var cuePath = Path.Combine(dir, "d.cue");
        File.WriteAllText(cuePath,
            "FILE \"d_track01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "FILE \"d_track02.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n" +
            "FILE \"d_track03.bin\" BINARY\n  TRACK 03 AUDIO\n    INDEX 01 00:00:00\n");

        var sub = new byte[total * 96];
        void Q(int lba, QControl c, int t, int idx)
        {
            long a = lba + 150;
            var m = new Msf((int)(a / 4500), (int)(a / 75 % 60), (int)(a % 75));
            SubQ.Position(c, t, idx, new Msf(0, 0, 0), m).CopyTo(sub.AsSpan(lba * 96 + 12, 12));
        }
        for (int l = 0; l < 300; l++) Q(l, QControl.Data, 1, 1);
        for (int l = track2PregapStart; l < 450; l++) Q(l, QControl.None, 2, 0);
        for (int l = 450; l < 700; l++) Q(l, QControl.None, 2, 1);
        for (int l = 700; l < 850; l++) Q(l, QControl.None, 3, 0);
        for (int l = 850; l < 1000; l++) Q(l, QControl.None, 3, 1);
        File.WriteAllBytes(Path.Combine(dir, "d.sub"), sub);
        return cuePath;
    }

    private static byte[] Concat(string dir, IEnumerable<string> files)
    {
        using var ms = new MemoryStream();
        foreach (var f in files) ms.Write(File.ReadAllBytes(Path.Combine(dir, f)));
        return ms.ToArray();
    }

    private static string Sha(byte[] b) => System.Convert.ToHexString(SHA256.HashData(b));

    [Fact]
    public void Recut_preserves_every_byte_and_places_index00_01()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_redump_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cue = BuildDisc(dir, track2PregapStart: 300);   // a clean 150-sector track-2 pregap
            var sub = File.ReadAllBytes(Path.Combine(dir, "d.sub"));

            var r = RedumpCueBuilder.Build(cue, sub, dir, "out");

            // Byte-exact: concatenated new bins == concatenated original bins.
            string original = Sha(Concat(dir, new[] { "d_track01.bin", "d_track02.bin", "d_track03.bin" }));
            string recut = Sha(Concat(dir, r.BinFilenames));
            Assert.Equal(original, recut);

            // Boundaries moved to INDEX 00: 300 / 400 / 300 sectors.
            Assert.Equal(new long[] { 300, 400, 300 }, r.Tracks.Select(t => t.NewLengthSectors).ToArray());

            var sheet = CueSheet.Parse(r.CueText);
            Assert.Single(sheet.Tracks[0].Indices);                          // track 1: INDEX 01 only
            Assert.Equal(1, sheet.Tracks[0].Indices[0].Number);
            Assert.Equal(2, sheet.Tracks[1].Indices.Count);                  // track 2: INDEX 00 + INDEX 01
            Assert.Equal(0, sheet.Tracks[1].Indices[0].Number);
            Assert.Equal(new Msf(0, 2, 0), sheet.Tracks[1].Indices[1].Time); // 150 sectors = 00:02:00
            Assert.Equal(new Msf(0, 2, 0), sheet.Tracks[2].Indices[1].Time);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_149_sector_pregap_is_reported_and_can_be_snapped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_redump_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cue = BuildDisc(dir, track2PregapStart: 301);   // one Q frame short → 149-sector pregap
            var sub = File.ReadAllBytes(Path.Combine(dir, "d.sub"));

            // Measured: track 2 reported as 149 with a note; still byte-exact.
            var measured = RedumpCueBuilder.Build(cue, sub, dir, "m");
            var t2 = measured.Tracks.First(t => t.Track == 2);
            Assert.Equal(149, t2.PregapSectors);
            Assert.False(t2.Snapped);
            Assert.NotNull(t2.Note);
            Assert.Equal(Sha(Concat(dir, new[] { "d_track01.bin", "d_track02.bin", "d_track03.bin" })),
                         Sha(Concat(dir, measured.BinFilenames)));

            // Snapped: track 2 becomes exactly 150, INDEX 01 at 00:02:00, and STILL byte-exact — the cut simply
            // moves one sector from track 1's tail into track 2's head; nothing is invented or lost.
            var snapped = RedumpCueBuilder.Build(cue, sub, dir, "s", snapPregap: true);
            var s2 = snapped.Tracks.First(t => t.Track == 2);
            Assert.Equal(150, s2.PregapSectors);
            Assert.True(s2.Snapped);
            Assert.Equal(new long[] { 300, 400, 300 }, snapped.Tracks.Select(t => t.NewLengthSectors).ToArray());
            Assert.Equal(Sha(Concat(dir, new[] { "d_track01.bin", "d_track02.bin", "d_track03.bin" })),
                         Sha(Concat(dir, snapped.BinFilenames)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_cooked_data_track_is_refused_rather_than_miscut()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_redump_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "c_track01.bin"), new byte[2048 * 10]);
            var cue = Path.Combine(dir, "c.cue");
            File.WriteAllText(cue, "FILE \"c_track01.bin\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n");
            var sub = new byte[96 * 10];

            var ex = Assert.Throws<InvalidOperationException>(() =>
                RedumpCueBuilder.Build(cue, sub, dir, "out"));
            Assert.Contains("2352", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }
}
