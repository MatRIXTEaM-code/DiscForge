// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Dreamcast;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the read-only Dreamcast PVR texture header reader — built from headers laid out byte for
/// byte per the PVRT format, with and without a leading GBIX global-index chunk, so the fixed field
/// offsets and the structural checks (known formats, power-of-two/square dimensions, truncation) are pinned.
/// </summary>
public class PvrTextureTests
{
    // Build a PVR image: PVRT header + zeroed data, optionally behind a GBIX chunk.
    private static byte[] Build(byte pixel, byte dataFormat, int width, int height, int dataBytes, uint? gbix = null)
    {
        var p = new List<byte>();
        p.AddRange("PVRT"u8.ToArray());
        var u32 = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)(8 + dataBytes)); p.AddRange(u32);
        p.Add(pixel); p.Add(dataFormat); p.Add(0); p.Add(0);
        var w = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(w, (ushort)width); p.AddRange(w);
        var h = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(h, (ushort)height); p.AddRange(h);
        p.AddRange(new byte[dataBytes]);

        if (gbix is not { } gi) return p.ToArray();
        var g = new List<byte>();
        g.AddRange("GBIX"u8.ToArray());
        var len = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(len, 8); g.AddRange(len);
        var idx = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(idx, gi); g.AddRange(idx);
        g.AddRange(new byte[4]);   // padding
        g.AddRange(p);
        return g.ToArray();
    }

    [Fact]
    public void Reads_the_fixed_header_fields_of_a_square_twiddled_texture()
    {
        var t = Pvr.Parse(Build(0x02, 0x01, 256, 256, 256 * 256 / 2));
        Assert.Equal(256, t.Width);
        Assert.Equal(256, t.Height);
        Assert.Equal("ARGB4444", t.PixelFormatName);
        Assert.Equal("square twiddled", t.DataFormatName);
        Assert.False(t.HasGlobalIndex);
        Assert.True(t.Valid);
    }

    [Fact]
    public void Finds_the_PVRT_chunk_after_a_GBIX_global_index()
    {
        var t = Pvr.Parse(Build(0x01, 0x03, 128, 128, 128 * 128 / 2, gbix: 0xDEADBEEF));
        Assert.True(t.HasGlobalIndex);
        Assert.Equal(0xDEADBEEFu, t.GlobalIndex);
        Assert.Equal(16, t.PvrtOffset);
        Assert.Equal("VQ", t.DataFormatName);
        Assert.True(t.Valid);
    }

    [Fact]
    public void A_rectangle_texture_may_be_non_square()
    {
        var t = Pvr.Parse(Build(0x01, 0x09, 64, 32, 64 * 32 / 2));
        Assert.Equal("rectangle", t.DataFormatName);
        Assert.True(t.Valid);
    }

    [Fact]
    public void Non_power_of_two_dimensions_are_flagged()
    {
        var t = Pvr.Parse(Build(0x02, 0x01, 100, 100, 100 * 100 / 2));
        Assert.False(t.Valid);
        Assert.Contains(t.Warnings, w => w.Contains("powers of two"));
    }

    [Fact]
    public void A_twiddled_texture_that_is_not_square_is_flagged()
    {
        var t = Pvr.Parse(Build(0x02, 0x01, 128, 64, 128 * 64 / 2));
        Assert.Contains(t.Warnings, w => w.Contains("must be square"));
    }

    [Fact]
    public void A_file_too_small_for_its_declared_size_reads_as_truncated()
    {
        var t = Pvr.Parse(Build(0x02, 0x01, 512, 512, 16));
        Assert.Contains(t.Warnings, w => w.Contains("truncated"));
    }

    [Fact]
    public void An_unknown_pixel_format_is_flagged_but_still_parses()
    {
        var t = Pvr.Parse(Build(0x0F, 0x01, 64, 64, 64 * 64 / 2));
        Assert.False(t.PixelFormatKnown);
        Assert.Contains(t.Warnings, w => w.Contains("unknown pixel format"));
    }

    [Fact]
    public void Bytes_without_a_PVRT_signature_are_rejected()
        => Assert.Throws<PvrFormatException>(() => Pvr.Parse(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
}
