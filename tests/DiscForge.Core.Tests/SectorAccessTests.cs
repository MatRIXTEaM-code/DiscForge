// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Files;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The unified sector layer under the sector viewer and extraction: every
/// image kind, every address form, one contract. The subtle cases are the
/// ones tested — CDI session gaps (LBA space is discontiguous, the file is
/// not) and raw images (where the lead-in shifts everything).
/// </summary>
public class SectorAccessTests
{
    private static readonly string Fixtures =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures");

    private static MemoryStream GenerateDataImage(out byte[] user)
    {
        user = new byte[10 * 2048];
        new Random(21).NextBytes(user);
        using var bin = new MemoryStream(user);
        const string cue = "FILE \"d.bin\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n";
        using var layout = DiscLayout.FromCue(CueSheet.Parse(cue), _ => bin);
        var img = new MemoryStream();
        RawImageGenerator.Generate(layout, RawSubcodeForm.Packed96, img);
        img.Position = 0;
        return img;
    }

    [Fact]
    public void RawImage_AllAddressFormsAgree()
    {
        using var img = GenerateDataImage(out _);
        using var acc = SectorAccess.Open(img, ".img");

        Assert.Equal(SectorAccess.ImageKind.RawDao, acc.Kind);
        long byLba = acc.Resolve("0");
        long byMsf = acc.Resolve("00:02:00");
        long byIdx = acc.Resolve("+" + (RawImageGenerator.LeadInSectors + 150));
        Assert.Equal(byLba, byMsf);
        Assert.Equal(byMsf, byIdx);
        Assert.Equal(0, acc.Resolve("95:00:00"));   // lead-in addressing
    }

    [Fact]
    public void RawImage_ReadCarriesIdentityAndSubcode()
    {
        using var img = GenerateDataImage(out var user);
        using var acc = SectorAccess.Open(img, ".img");

        var sec = acc.Read(acc.Resolve("0"));
        Assert.Equal(0, sec.Lba);
        Assert.Equal(150, sec.Msf.ToSectors());
        Assert.False(sec.LeadIn);
        Assert.Equal(96, sec.Subcode?.Length);
        Assert.Equal(RawSubcodeForm.Packed96, sec.SubcodeForm);

        var copy = (byte[])sec.Stored.Clone();
        CdScrambler.ScrambleInPlace(copy);            // descramble
        Assert.True(copy.AsSpan(16, 2048).SequenceEqual(user.AsSpan(0, 2048)));
    }

    [Fact]
    public void Cdi_LbaGapsBetweenSessionsCollapseInFileSpace()
    {
        var path = Path.Combine(Fixtures, "synthetic", "multitrack_mixed_v3.cdi");
        using var acc = SectorAccess.Open(path);

        Assert.Equal(SectorAccess.ImageKind.Cdi, acc.Kind);

        // Track 3 starts at LBA 15000 in the descriptor, but the file has
        // only 2850 sectors before it (two 1350-sector tracks + its pregap).
        var sec = acc.Read(acc.Resolve("15000"));
        Assert.Equal(3, sec.Track);
        Assert.Equal(15000, sec.Lba);
        Assert.Equal(2336, sec.Stored.Length);

        Assert.Equal(150, acc.Resolve("00:02:00"));   // LBA 0, track 1
        Assert.Throws<ArgumentOutOfRangeException>(() => acc.Resolve("40000"));
    }

    [Fact]
    public void OutOfRangeReads_AreRefusedWithTheRange()
    {
        using var img = GenerateDataImage(out _);
        using var acc = SectorAccess.Open(img, ".img");
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => acc.Read(acc.TotalSectors));
        Assert.Contains("outside the image", ex.Message);
    }
}
