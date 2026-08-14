// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Media;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// DVD/BD read-back verification: a sector-exact compare that reports at ECC-block
/// granularity and, for dual-layer discs, attributes mismatches to L0/L1 and
/// checks the layer break — the detail ImgBurn's MD5 verify can't give.
/// </summary>
public class DvdReadbackCompareTests
{
    private const int Sector = 2048;

    private static byte[] Image(int sectors, int seed = 3)
    {
        var b = new byte[sectors * Sector];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void An_identical_readback_passes_and_md5_matches()
    {
        var src = Image(64);
        var r = DvdReadbackCompare.Compare(new MemoryStream(src), new MemoryStream((byte[])src.Clone()));

        Assert.Equal(DvdReadbackCompare.Grade.Pass, r.Result);
        Assert.Equal(0, r.MismatchedSectors);
        Assert.True(r.Md5Match);
        Assert.Equal(64, r.SectorsCompared);
    }

    [Fact]
    public void A_corrupt_sector_fails_and_is_localised_to_its_ecc_block()
    {
        var src = Image(64);
        var rb = (byte[])src.Clone();
        rb[20 * Sector + 5] ^= 0xFF;                 // sector 20 → ECC block 1

        var r = DvdReadbackCompare.Compare(new MemoryStream(src), new MemoryStream(rb));

        Assert.Equal(DvdReadbackCompare.Grade.Fail, r.Result);
        Assert.Equal(1, r.MismatchedSectors);
        Assert.Equal(1, r.BadEccBlocks);
        Assert.False(r.Md5Match);
        Assert.Contains(r.Examples, e => e.EccBlock == 1 && e.FirstSector == 16 && e.BadSectors == 1);
    }

    [Fact]
    public void Trailing_blank_padding_in_the_readback_is_benign()
    {
        var src = Image(48);
        var rb = new byte[(48 + 16) * Sector];       // 16 trailing zero sectors
        src.CopyTo(rb, 0);

        var r = DvdReadbackCompare.Compare(new MemoryStream(src), new MemoryStream(rb));

        Assert.Equal(DvdReadbackCompare.Grade.Pass, r.Result);
        Assert.Equal(16, r.ExtraSectors);
        Assert.Equal(16, r.PaddingSectors);
    }

    [Fact]
    public void Non_blank_extra_sectors_fail()
    {
        var src = Image(32);
        var rb = new byte[(32 + 4) * Sector];
        src.CopyTo(rb, 0);
        rb[(33 * Sector) + 10] = 0x7F;               // a non-blank extra sector

        var r = DvdReadbackCompare.Compare(new MemoryStream(src), new MemoryStream(rb));

        Assert.Equal(DvdReadbackCompare.Grade.Fail, r.Result);
        Assert.Equal(4, r.ExtraSectors);
        Assert.True(r.PaddingSectors < 4);
    }

    [Fact]
    public void A_short_readback_reports_missing_sectors()
    {
        var src = Image(64);
        var rb = src.AsSpan(0, 40 * Sector).ToArray();

        var r = DvdReadbackCompare.Compare(new MemoryStream(src), new MemoryStream(rb));

        Assert.Equal(DvdReadbackCompare.Grade.Fail, r.Result);
        Assert.Equal(24, r.MissingSectors);
        Assert.Equal(40, r.SectorsCompared);
    }

    [Fact]
    public void Layer_break_attributes_mismatches_to_the_right_layer()
    {
        var src = Image(64);
        var rb = (byte[])src.Clone();
        rb[10 * Sector] ^= 0xFF;                     // L0 (before break 32)
        rb[50 * Sector] ^= 0xFF;                     // L1 (after break)

        var r = DvdReadbackCompare.Compare(new MemoryStream(src), new MemoryStream(rb), layerBreakLba: 32);

        Assert.Equal(DvdReadbackCompare.Grade.Fail, r.Result);
        Assert.Equal(1, r.L0Mismatches);
        Assert.Equal(1, r.L1Mismatches);
        Assert.True(r.LayerBreakConsistent);
        Assert.Contains(r.Examples, e => e.Layer == "L0");
        Assert.Contains(r.Examples, e => e.Layer == "L1");
    }

    [Fact]
    public void An_illegal_layer_break_boundary_is_flagged()
    {
        var src = Image(64);
        var r = DvdReadbackCompare.Compare(
            new MemoryStream(src), new MemoryStream((byte[])src.Clone()), layerBreakLba: 33);   // not %16

        Assert.Equal(DvdReadbackCompare.Grade.Fail, r.Result);
        Assert.False(r.LayerBreakConsistent);
        Assert.Contains(r.Notes, n => n.Contains("ECC-block boundary"));
    }
}
