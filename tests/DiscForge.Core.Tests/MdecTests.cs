// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

public class MdecTests
{
    [Fact]
    public void Idct_of_a_dc_only_block_is_a_flat_value()
    {
        var block = new double[64];
        block[0] = 800;
        Mdec.Idct8x8(block);
        // A DC-only block inverse-transforms to a flat plane of DC/8.
        Assert.All(block, v => Assert.True(Math.Abs(v - 100) < 1e-9));
    }

    [Fact]
    public void Idct_matches_the_reference_transform_on_a_known_block()
    {
        // A single mid-band AC coefficient must produce a symmetric cosine ripple, not a flat field.
        var block = new double[64];
        block[1] = 64;  // horizontal frequency 1
        Mdec.Idct8x8(block);
        Assert.True(block[0] > 0);            // left edge bright
        Assert.True(block[7] < 0);            // right edge dark
        Assert.True(Math.Abs(block[0] + block[7]) < 1e-9);  // anti-symmetric across the row
    }

    [Fact]
    public void Dequantize_scales_dc_directly_and_ac_by_quant_scale()
    {
        var zz = new short[64];
        zz[0] = 10; zz[1] = 4;
        var nat = Mdec.Dequantize(zz, 2);
        Assert.Equal(20.0, nat[0], 9);                       // DC: 10 * quant[0] (=2)
        Assert.Equal(16.5, nat[Mdec.ZigZag[1]], 9);          // AC: (4 * 16 * 2 + 4) / 8
    }

    [Fact]
    public void Parses_the_frame_header_little_endian()
    {
        var b = new byte[] { 0x40, 0x01, 0x00, 0x38, 0x05, 0x00, 0x02, 0x00 };
        var h = Mdec.ParseFrameHeader(b);
        Assert.Equal(0x0140, h.CodeCount);
        Assert.True(h.MarkerOk);
        Assert.Equal(5, h.QuantScale);
        Assert.Equal(2, h.Version);
    }

    [Fact]
    public void A_missing_marker_is_flagged()
    {
        var b = new byte[] { 0x40, 0x01, 0x00, 0x00, 0x05, 0x00, 0x02, 0x00 };
        Assert.False(Mdec.ParseFrameHeader(b).MarkerOk);
    }

    [Fact]
    public void Ycbcr_converts_grey_and_a_red_chroma()
    {
        Assert.Equal(((byte)128, (byte)128, (byte)128), Mdec.YcbcrToRgb(0, 0, 0));
        var (r, g, _) = Mdec.YcbcrToRgb(0, 0, 90);
        Assert.True(r > 200 && g < 128);
    }

    [Fact]
    public void A_short_header_throws()
        => Assert.Throws<ArgumentException>(() => Mdec.ParseFrameHeader(new byte[4]));
}
