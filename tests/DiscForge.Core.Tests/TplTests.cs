// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.GameCube;
using Xunit;

namespace DiscForge.Core.Tests;

public class TplTests
{
    private static void BE32(List<byte> b, uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
    private static void BE16(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }

    // Build a single-texture TPL. palFmt<0 => no palette.
    private static byte[] BuildTpl(int w, int h, int fmt, byte[] imgData, int palFmt = -1, ushort[]? palette = null)
    {
        var f = new List<byte>();
        BE32(f, 0x0020AF30); BE32(f, 1); BE32(f, 0x0C);
        int imgHdrOff = 0x14, imgHdrEnd = imgHdrOff + 0x24;
        int palHdrOff = palFmt >= 0 ? imgHdrEnd : 0;
        int palHdrEnd = palFmt >= 0 ? imgHdrEnd + 0x0C : imgHdrEnd;
        int palDataOff = palFmt >= 0 ? palHdrEnd : 0;
        int palDataEnd = palFmt >= 0 ? palDataOff + palette!.Length * 2 : palHdrEnd;
        int dataOff = palDataEnd;

        BE32(f, (uint)imgHdrOff); BE32(f, (uint)palHdrOff);
        var ih = new List<byte>();
        BE16(ih, (ushort)h); BE16(ih, (ushort)w); BE32(ih, (uint)fmt); BE32(ih, (uint)dataOff);
        while (ih.Count < 0x24) ih.Add(0);
        f.AddRange(ih);
        if (palFmt >= 0)
        {
            var ph = new List<byte>();
            BE16(ph, (ushort)palette!.Length); BE16(ph, 0); BE32(ph, (uint)palFmt); BE32(ph, (uint)palDataOff);
            f.AddRange(ph);
            foreach (var p in palette) BE16(f, p);
        }
        f.AddRange(imgData);
        return f.ToArray();
    }

    private static (byte r, byte g, byte b, byte a) Px(IReadOnlyList<TplTexture> t, int i, int x, int y)
    { var tex = t[i]; int o = (y * tex.Width + x) * 4; return (tex.Rgba[o], tex.Rgba[o + 1], tex.Rgba[o + 2], tex.Rgba[o + 3]); }

    [Fact]
    public void Rgba8_splits_ar_gb_and_tiles_correctly()
    {
        var data = new byte[64];
        int k = 1 * 4 + 2; // texel (2,1)
        data[k * 2] = 0x44; data[k * 2 + 1] = 0x11; data[32 + k * 2] = 0x22; data[32 + k * 2 + 1] = 0x33;
        var t = Tpl.Read(BuildTpl(4, 4, 0x6, data));
        Assert.Equal("RGBA8", t[0].FormatName);
        Assert.Equal(((byte)0x11, (byte)0x22, (byte)0x33, (byte)0x44), Px(t, 0, 2, 1));
    }

    [Fact]
    public void Rgb5a3_orders_4x4_tiles()
    {
        var data = new byte[8 * 8 * 2];
        void Set(int tile, int texel, ushort v) { int o = (tile * 16 + texel) * 2; data[o] = (byte)(v >> 8); data[o + 1] = (byte)v; }
        for (int i = 0; i < 64; i++) Set(i / 16, i % 16, 0xFFFF);
        Set(1, 0, (ushort)(0x8000 | (31 << 10)));
        var t = Tpl.Read(BuildTpl(8, 8, 0x5, data));
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), Px(t, 0, 0, 0));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Px(t, 0, 4, 0));  // tile 1 starts at x=4
    }

    [Fact]
    public void Ci8_indexes_the_palette_including_transparency()
    {
        var pal = new ushort[] { 0x0000, (ushort)(0x8000 | (31 << 10)) };
        var data = new byte[32];
        for (int i = 0; i < 32; i++) data[i] = 1;
        data[1 * 8 + 3] = 0;
        var t = Tpl.Read(BuildTpl(8, 4, 0x9, data, palFmt: 2, palette: pal));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Px(t, 0, 0, 0));
        Assert.Equal((byte)0, Px(t, 0, 3, 1).a);
    }

    [Fact]
    public void Cmpr_decodes_endpoints_and_index_bit_order()
    {
        var data = new byte[32];
        ushort c0 = 0xF800, c1 = 0x001F;
        data[0] = (byte)(c0 >> 8); data[1] = (byte)c0; data[2] = (byte)(c1 >> 8); data[3] = (byte)c1;
        data[4] = 0x1B;
        var t = Tpl.Read(BuildTpl(4, 4, 0xE, data));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Px(t, 0, 0, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), Px(t, 0, 1, 0));
    }

    [Fact]
    public void I4_reads_high_nibble_first()
    {
        var data = new byte[32];
        data[0] = 0xF0;
        var t = Tpl.Read(BuildTpl(8, 8, 0x0, data));
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), Px(t, 0, 0, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), Px(t, 0, 1, 0));
    }

    [Fact]
    public void Magic_is_required()
    {
        Assert.True(Tpl.IsTpl(BuildTpl(4, 4, 0x6, new byte[64])));
        Assert.False(Tpl.IsTpl(new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<GameCubeFormatException>(() => Tpl.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
    }
}
