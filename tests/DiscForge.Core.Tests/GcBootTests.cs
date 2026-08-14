// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.GameCube;
using Xunit;

namespace DiscForge.Core.Tests;

public class GcBootTests
{
    private static void Be32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);

    private static byte[] BuildImageWithBoot()
    {
        var img = new byte[0x8000];
        // Apploader @ 0x2440.
        Encoding.ASCII.GetBytes("2001/11/18").CopyTo(img, 0x2440);
        Be32(img, 0x2440 + 0x10, 0x81300000);   // entry point
        Be32(img, 0x2440 + 0x14, 0x1954);        // size
        Be32(img, 0x2440 + 0x18, 0x20);          // trailer size

        // DOL @ 0x3000: 2 text sections, 1 data section, entry point, bss.
        int dol = 0x3000;
        Be32(img, dol + 0x00, 0x100);  Be32(img, dol + 0x90, 0x400);   // text 0: off, size
        Be32(img, dol + 0x04, 0x500);  Be32(img, dol + 0x94, 0x200);   // text 1
        Be32(img, dol + 0x1C, 0x700);  Be32(img, dol + 0xAC, 0x100);   // data 0 (index 7: offset 0x1C, size 0x90+7*4=0xAC)
        Be32(img, dol + 0xDC, 0x800);                                   // bss size
        Be32(img, dol + 0xE0, 0x81301000);                             // entry point
        return img;
    }

    [Fact]
    public void Reads_the_apploader_header()
    {
        var a = GcBoot.ReadApploader(new MemoryStream(BuildImageWithBoot()));
        Assert.Equal("2001/11/18", a.Date);
        Assert.Equal(0x81300000u, a.EntryPoint);
        Assert.Equal(0x1954u, a.Size);
        Assert.Equal(0x20u, a.TrailerSize);
    }

    [Fact]
    public void Reads_the_dol_entry_point_and_sections()
    {
        var d = GcBoot.ReadDol(new MemoryStream(BuildImageWithBoot()), 0x3000);
        Assert.Equal(0x81301000u, d.EntryPoint);
        Assert.Equal(2, d.TextSections);
        Assert.Equal(1, d.DataSections);
        Assert.Equal(0x800u, d.BssSize);
        // Largest section end: data 0 at 0x700 + 0x100 = 0x800.
        Assert.Equal(0x800, d.TotalSize);
    }

    [Fact]
    public void A_dol_offset_past_the_image_is_rejected()
    {
        Assert.Throws<GameCubeFormatException>(() => GcBoot.ReadDol(new MemoryStream(new byte[0x1000]), 0x900000));
    }

    [Fact]
    public void A_tiny_image_has_no_apploader()
    {
        Assert.Throws<GameCubeFormatException>(() => GcBoot.ReadApploader(new MemoryStream(new byte[0x100])));
    }
}
