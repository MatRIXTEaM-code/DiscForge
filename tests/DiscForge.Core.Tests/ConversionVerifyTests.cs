// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Chd;
using DiscForge.Core.Convert;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// verify-convert proves a format conversion kept the disc byte-for-byte. These tests pin the comparison logic
/// (identical → lossless; a size delta or a first-differing byte → not lossless, located precisely) and then run
/// the real path end to end: a bin/cue is written to a CHD by DiscForge's own writer and read back, and the
/// round-trip must verify lossless — while a single flipped byte in the source is caught at its exact sector.
/// </summary>
public class ConversionVerifyTests
{
    private const int SS = 2352;

    private static byte[] RawImage(int sectors, int seed = 0)
    {
        var b = new byte[sectors * SS];
        for (int s = 0; s < sectors; s++)
        {
            var sec = b.AsSpan(s * SS, SS);
            sec[0] = 0; for (int i = 1; i <= 10; i++) sec[i] = 0xFF; sec[15] = 1;   // sync + mode 1
            for (int i = 16; i < 16 + 2048; i++) sec[i] = (byte)((i * 31 + s * 7 + seed) % 256);
        }
        return b;
    }

    [Fact]
    public void Identical_data_is_lossless()
    {
        var a = RawImage(50);
        var r = ConversionVerify.Compare(a, (byte[])a.Clone());
        Assert.True(r.Lossless);
        Assert.Null(r.FirstDiffOffset);
    }

    [Fact]
    public void A_size_delta_is_not_lossless()
    {
        var r = ConversionVerify.Compare(RawImage(50), RawImage(51));
        Assert.False(r.Lossless);
        Assert.Equal(50 * SS, r.LengthA);
        Assert.Equal(51 * SS, r.LengthB);
        Assert.Contains("sector", r.Summary());
    }

    [Fact]
    public void A_content_difference_is_located_to_the_sector()
    {
        var a = RawImage(50);
        var b = (byte[])a.Clone();
        b[30 * SS + 100] ^= 0xFF;
        var r = ConversionVerify.Compare(a, b);
        Assert.False(r.Lossless);
        Assert.Equal(30L * SS + 100, r.FirstDiffOffset);
    }

    [Fact]
    public void A_real_chd_round_trip_verifies_lossless()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_vc_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var bin = RawImage(80);
            File.WriteAllBytes(Path.Combine(dir, "game.bin"), bin);
            const string cue = "FILE \"game.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n";
            File.WriteAllText(Path.Combine(dir, "game.cue"), cue);

            var chd = ChdWriter.CreateCdFromBinCue(cue, dir);
            var extracted = ChdExtractor.ExtractCd(chd).Bin;

            var r = ConversionVerify.Compare(bin, extracted);
            Assert.True(r.Lossless);                         // the CHD conversion preserved every byte

            bin[40 * SS + 200] ^= 0xFF;                      // now the source differs from the CHD
            var r2 = ConversionVerify.Compare(bin, extracted);
            Assert.False(r2.Lossless);
            Assert.Equal(40L * SS + 200, r2.FirstDiffOffset);
        }
        finally { Directory.Delete(dir, true); }
    }
}
