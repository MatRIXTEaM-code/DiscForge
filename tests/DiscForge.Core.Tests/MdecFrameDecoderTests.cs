// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The STR version-2 MDEC frame decoder. There is no reference-frame oracle in the
/// offline environment, so these tests validate every part that CAN be checked from
/// the spec alone: the 16-bit-LE / MSB-first bit reader and the AC VLC decode (against
/// hand-built codes taken directly from Table B-14), and full DC-only frames — which
/// are legitimate MDEC bitstreams — decoding to the exact flat colours the DC values,
/// IDCT and YCbCr→RGB conversion predict. That exercises the bit packing, DC decode,
/// block assembly, macroblock placement, clipping and colour end to end.
/// </summary>
public class MdecFrameDecoderTests
{
    // Writes bits MSB-first into 16-bit little-endian words — the exact inverse of the
    // decoder's BitReader16, so a stream built here reads back identically.
    private sealed class BitWriter16
    {
        private readonly List<byte> _bytes = new();
        private int _cur, _n;
        public void WriteBit(int b) { _cur = (_cur << 1) | (b & 1); if (++_n == 16) Flush(); }
        public void WriteBits(int v, int n) { for (int i = n - 1; i >= 0; i--) WriteBit((v >> i) & 1); }
        private void Flush() { _bytes.Add((byte)(_cur & 0xFF)); _bytes.Add((byte)((_cur >> 8) & 0xFF)); _cur = 0; _n = 0; }
        public byte[] ToArray() { if (_n > 0) { _cur <<= 16 - _n; Flush(); } return _bytes.ToArray(); }
    }

    private static byte[] FrameHeader(int qscale)
    {
        // numBlocks, 0x3800 marker, qscale, version 2 — all u16 little-endian.
        return new byte[]
        {
            0x06, 0x00,
            0x00, 0x38,
            (byte)qscale, (byte)(qscale >> 8),
            0x02, 0x00,
        };
    }

    // Build a frame whose every block is "DC then end-of-block" (a valid MDEC frame).
    private static byte[] DcOnlyFrame(int crDc, int cbDc, int yDc, int mbCount)
    {
        var bw = new BitWriter16();
        for (int mb = 0; mb < mbCount; mb++)
        {
            foreach (int dc in new[] { crDc, cbDc, yDc, yDc, yDc, yDc })
            {
                bw.WriteBits(dc & 0x3FF, 10);   // 10-bit DC
                bw.WriteBits(0b10, 2);          // end of block
            }
        }
        var body = bw.ToArray();
        var hdr = FrameHeader(1);
        var all = new byte[hdr.Length + body.Length];
        hdr.CopyTo(all, 0);
        body.CopyTo(all, hdr.Length);
        return all;
    }

    [Fact]
    public void Ac_vlc_decodes_known_table_codes_and_the_escape()
    {
        // "11" + sign 0  → run 0, level +1.
        var bw = new BitWriter16();
        bw.WriteBits(0b11, 2); bw.WriteBit(0);
        var r = MdecFrameDecoder.ReadAcForTest(bw.ToArray(), 0);
        Assert.Equal((false, 0, 1), r);

        // "0101" + sign 1 → run 2, level -1.
        bw = new BitWriter16();
        bw.WriteBits(0b0101, 4); bw.WriteBit(1);
        Assert.Equal((false, 2, -1), MdecFrameDecoder.ReadAcForTest(bw.ToArray(), 0));

        // End of block "10".
        bw = new BitWriter16();
        bw.WriteBits(0b10, 2);
        Assert.True(MdecFrameDecoder.ReadAcForTest(bw.ToArray(), 0).Eob);

        // Escape "000001" + 6-bit run (17) + 10-bit signed level (-3).
        bw = new BitWriter16();
        bw.WriteBits(0b000001, 6); bw.WriteBits(17, 6); bw.WriteBits(-3 & 0x3FF, 10);
        Assert.Equal((false, 17, -3), MdecFrameDecoder.ReadAcForTest(bw.ToArray(), 0));
    }

    [Fact]
    public void An_all_zero_dc_frame_decodes_to_uniform_mid_grey()
    {
        var img = MdecFrameDecoder.DecodeFrame(DcOnlyFrame(0, 0, 0, mbCount: 1), 16, 16);
        Assert.Equal(16, img.Width);
        Assert.Equal(16 * 16 * 4, img.Rgba.Length);
        for (int i = 0; i < img.Rgba.Length; i += 4)
        {
            Assert.Equal(128, img.Rgba[i]);
            Assert.Equal(128, img.Rgba[i + 1]);
            Assert.Equal(128, img.Rgba[i + 2]);
            Assert.Equal(255, img.Rgba[i + 3]);
        }
    }

    [Fact]
    public void A_luma_dc_frame_decodes_to_a_uniform_brighter_grey()
    {
        // Y DC 200 → dequant ×2, IDCT flat ÷8 → +50; +128 offset = 178, chroma neutral.
        var img = MdecFrameDecoder.DecodeFrame(DcOnlyFrame(0, 0, 200, mbCount: 1), 16, 16);
        for (int i = 0; i < img.Rgba.Length; i += 4)
        {
            Assert.Equal(178, img.Rgba[i]);
            Assert.Equal(178, img.Rgba[i + 1]);
            Assert.Equal(178, img.Rgba[i + 2]);
        }
    }

    [Fact]
    public void A_cr_dc_frame_tints_the_frame_red()
    {
        // Positive Cr must lift red above blue (R = Y + 1.402·Cr, B = Y + 1.772·Cb=neutral).
        var img = MdecFrameDecoder.DecodeFrame(DcOnlyFrame(200, 0, 0, mbCount: 1), 16, 16);
        Assert.True(img.Rgba[0] > img.Rgba[2], "red channel should exceed blue with positive Cr");
        Assert.True(img.Rgba[0] > img.Rgba[1], "red channel should exceed green with positive Cr");
    }

    [Fact]
    public void A_frame_whose_size_is_not_a_multiple_of_16_is_clipped()
    {
        // 20×18 → 2×2 macroblocks decoded, output clipped to the declared size.
        var img = MdecFrameDecoder.DecodeFrame(DcOnlyFrame(0, 0, 0, mbCount: 4), 20, 18);
        Assert.Equal(20, img.Width);
        Assert.Equal(18, img.Height);
        Assert.Equal(20 * 18 * 4, img.Rgba.Length);
    }

    [Fact]
    public void A_non_mdec_bitstream_is_rejected()
    {
        var bad = new byte[16];             // marker won't be 0x3800
        Assert.Throws<MdecFrameDecoder.MdecDecodeException>(
            () => MdecFrameDecoder.DecodeFrame(bad, 16, 16));
    }

    [Fact]
    public void Version_3_is_reported_not_mis_decoded()
    {
        var hdr = FrameHeader(1);
        hdr[6] = 0x03;                      // version 3
        var ex = Assert.Throws<MdecFrameDecoder.MdecDecodeException>(
            () => MdecFrameDecoder.DecodeFrame(hdr, 16, 16));
        Assert.Contains("version 3", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }
}
