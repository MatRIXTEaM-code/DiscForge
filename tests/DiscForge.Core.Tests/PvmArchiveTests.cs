// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Dreamcast;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the read-only PVM (PVR-Multi) archive reader: a PVMH header with a filename/format/dimension
/// entry table, then 16-byte-aligned PVRT textures. Built byte for byte so the header offsets, the entry
/// table walk and the per-texture parse are all pinned.
/// </summary>
public class PvmArchiveTests
{
    private static byte[] Tex(byte pixel, byte dataFormat, int w, int h)
    {
        var data = new byte[w * h / 2];
        var p = new List<byte>();
        p.AddRange("PVRT"u8.ToArray());
        var sz = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(sz, (uint)(8 + data.Length)); p.AddRange(sz);
        p.Add(pixel); p.Add(dataFormat); p.Add(0); p.Add(0);
        var ww = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(ww, (ushort)w); p.AddRange(ww);
        var hh = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(hh, (ushort)h); p.AddRange(hh);
        p.AddRange(data);
        return p.ToArray();
    }

    // Build a PVM with the filenames+formats+dimensions flags set.
    private static byte[] Build((string name, byte[] tex)[] texs, int? forceCount = null)
    {
        const int flags = 0x0E;   // filenames | formats | dimensions
        int tableBytes = texs.Length * (2 + 28 + 2 + 2);
        int firstTex = 0x0C + tableBytes;

        var h = new List<byte>();
        h.AddRange("PVMH"u8.ToArray());
        var off = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(off, (uint)(firstTex - 8)); h.AddRange(off);
        var fl = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(fl, flags); h.AddRange(fl);
        var ct = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(ct, (ushort)(forceCount ?? texs.Length)); h.AddRange(ct);
        for (int i = 0; i < texs.Length; i++)
        {
            var idx = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(idx, (ushort)i); h.AddRange(idx);
            h.AddRange(Encoding.ASCII.GetBytes(texs[i].name.PadRight(28)[..28]));
            h.Add(0x02); h.Add(0x01);       // format bytes
            h.AddRange(new byte[2]);         // dimensions (ignored by the reader)
        }
        foreach (var t in texs)
        {
            while (h.Count % 16 != 0) h.Add(0);
            h.AddRange(t.tex);
        }
        return h.ToArray();
    }

    [Fact]
    public void Lists_each_embedded_texture_with_its_name_and_parsed_header()
    {
        var pvm = Pvm.Parse(Build(new[]
        {
            ("FONT.PVR", Tex(0x02, 0x01, 256, 256)),
            ("TITLE.PVR", Tex(0x01, 0x03, 128, 128)),
        }));

        Assert.Equal(2, pvm.DeclaredCount);
        Assert.Equal(2, pvm.Textures.Count);
        Assert.True(pvm.CountMatches);
        Assert.True(pvm.HasFilenames);
        Assert.Equal("FONT.PVR", pvm.Textures[0].Name);
        Assert.Equal("TITLE.PVR", pvm.Textures[1].Name);
        Assert.Equal(256, pvm.Textures[0].Texture.Width);
        Assert.Equal("square twiddled", pvm.Textures[0].Texture.DataFormatName);
        Assert.Equal("VQ", pvm.Textures[1].Texture.DataFormatName);
        Assert.True(pvm.Valid);
    }

    [Fact]
    public void A_declared_count_that_exceeds_the_textures_found_is_flagged()
    {
        var pvm = Pvm.Parse(Build(new[] { ("A.PVR", Tex(0x02, 0x01, 64, 64)) }, forceCount: 3));
        Assert.False(pvm.CountMatches);
        Assert.Contains(pvm.Warnings, w => w.Contains("declares 3"));
    }

    [Fact]
    public void Bytes_without_a_PVMH_signature_are_rejected()
        => Assert.Throws<PvrFormatException>(() => Pvm.Parse(new byte[16]));
}
