// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Dat;
using DiscForge.Core.Files;
using DiscForge.Core.Preservation;
using DiscForge.Core.Raw;
using DiscForge.Core.Verify;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// dump-audit gives the one-line "is my dump good?" verdict. These tests build a raw Mode-1 dump and confirm the
/// verdict for the cases that matter: a clean dump that matches a DAT is GOOD; a single flipped byte is caught by
/// the EDC audit and makes it BAD; a recorded unreadable sector makes it BAD even though the bytes still hash to
/// the DAT; a zero-filled tail is flagged as a suspect end-of-disc read.
/// </summary>
public class DumpAuditTests
{
    private const int SS = 2352;
    private const int NS = 60;

    private static byte[] Sector(int lba, int seed)
    {
        var s = new byte[SS];
        s[0] = 0; for (int i = 1; i <= 10; i++) s[i] = 0xFF; s[11] = 0;
        s[12] = (byte)(lba >> 16); s[13] = (byte)(lba >> 8); s[14] = (byte)lba; s[15] = 1;
        for (int i = 16; i < 16 + 2048; i++) s[i] = (byte)((i * 31 + lba * 7 + seed) % 256);
        EdcEcc.FillMode1(s);
        return s;
    }

    private static byte[] Image(int seed)
    {
        var img = new byte[NS * SS];
        for (int l = 0; l < NS; l++) Sector(l, seed).CopyTo(img.AsSpan(l * SS, SS));
        return img;
    }

    private static string Dump(string dir, byte[] bin, long[]? holes)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "t.bin"), bin);
        var cue = Path.Combine(dir, "t.cue");
        File.WriteAllText(cue, "FILE \"t.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
        if (holes is not null)
            new BadSectorMap { Image = "t.cue", TotalSectors = NS, UnreadableLba = holes }.Save(BadSectorMap.SidecarPath(cue));
        return cue;
    }

    private static DatFile DatFor(string dir)
    {
        DatBuildRom Rom(string n)
        {
            var s = ImageChecksums.ComputeFile(Path.Combine(dir, n));
            return new DatBuildRom("Good Game", n, s.Length, s.Crc32, s.Md5, s.Sha1);
        }
        return DatFile.ParseText(DatBuilder.Build("Ref", new[] { Rom("t.cue"), Rom("t.bin") }));
    }

    private static CheckStatus StatusOf(DumpVerdict v, string check) => v.Checks.First(c => c.Name == check).Status;

    [Fact]
    public void A_clean_dump_matching_a_dat_is_good()
    {
        var root = Path.Combine(Path.GetTempPath(), "dforge_da_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cue = Dump(root, Image(1), null);
            var v = DumpAudit.Audit(cue, DatFor(root));
            Assert.Equal(DumpQuality.Good, v.Quality);
            Assert.Equal(CheckStatus.Pass, StatusOf(v, "data integrity (EDC/ECC)"));
            Assert.Equal(CheckStatus.Pass, StatusOf(v, "Redump match"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_single_flipped_byte_is_caught_by_edc_and_is_bad()
    {
        var root = Path.Combine(Path.GetTempPath(), "dforge_da_" + Guid.NewGuid().ToString("N"));
        try
        {
            var good = Image(1);
            Dump(root, good, null);
            var datRoot = Path.Combine(Path.GetTempPath(), "dforge_da_" + Guid.NewGuid().ToString("N"));
            Dump(datRoot, good, null);
            var dat = DatFor(datRoot);

            var corrupt = (byte[])good.Clone();
            corrupt[20 * SS + 100] ^= 0xFF;                 // one bad byte in sector 20's user data
            var cue = Dump(root, corrupt, null);

            var v = DumpAudit.Audit(cue, dat);
            Assert.Equal(DumpQuality.Bad, v.Quality);
            Assert.Equal(CheckStatus.Fail, StatusOf(v, "data integrity (EDC/ECC)"));

            Directory.Delete(datRoot, true);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_recorded_hole_makes_it_bad_even_when_bytes_match_the_dat()
    {
        var root = Path.Combine(Path.GetTempPath(), "dforge_da_" + Guid.NewGuid().ToString("N"));
        try
        {
            var good = Image(1);
            var cue = Dump(root, good, new long[] { 25 });   // clean bytes, but a hole recorded
            var v = DumpAudit.Audit(cue, DatFor(root));
            Assert.Equal(DumpQuality.Bad, v.Quality);
            Assert.Equal(CheckStatus.Fail, StatusOf(v, "read completeness"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_zero_filled_tail_is_flagged_as_a_suspect_end_read()
    {
        var root = Path.Combine(Path.GetTempPath(), "dforge_da_" + Guid.NewGuid().ToString("N"));
        try
        {
            var img = Image(1);
            for (int l = NS - 3; l < NS; l++) img.AsSpan(l * SS, SS).Clear();
            var cue = Dump(root, img, null);
            var v = DumpAudit.Audit(cue);                    // no DAT
            Assert.Equal(CheckStatus.Warn, StatusOf(v, "end sectors"));
            Assert.NotEqual(DumpQuality.Good, v.Quality);    // end-warn (and no-DAT) keep it out of GOOD
        }
        finally { Directory.Delete(root, true); }
    }
}
