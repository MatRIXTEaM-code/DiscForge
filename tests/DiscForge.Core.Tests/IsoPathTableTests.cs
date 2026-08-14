// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class IsoPathTableTests
{
    // A real ISO with nested directories: /DOCS/readme.txt, /IMAGES/ICONS/, /boot.bin.
    private static byte[] BuildIso()
    {
        var tree = new List<IsoBuilder.Node>
        {
            IsoBuilder.Node.Dir("DOCS", new[] { IsoBuilder.Node.File("README.TXT", Encoding.ASCII.GetBytes("hello")) }),
            IsoBuilder.Node.Dir("IMAGES", new[] { IsoBuilder.Node.Dir("ICONS", Array.Empty<IsoBuilder.Node>()) }),
            IsoBuilder.Node.File("BOOT.BIN", new byte[2048]),
        };
        return IsoBuilder.BuildTree("TESTVOL", tree, joliet: false).Image;
    }

    [Fact]
    public void A_real_iso_path_table_is_conformant()
    {
        var r = IsoPathTable.Read(BuildIso());
        Assert.True(r.Ok, IsoPathTable.Render(r));
        // Root + DOCS + IMAGES + ICONS.
        Assert.True(r.Entries.Count >= 4);
        Assert.Contains(r.Entries, e => e.Name == "DOCS");
        Assert.Contains(r.Entries, e => e.Name == "IMAGES");
        Assert.Contains(r.Entries, e => e.Name == "ICONS");
    }

    [Fact]
    public void Nested_directory_parents_are_correct()
    {
        var r = IsoPathTable.Read(BuildIso());
        var images = r.Entries.First(e => e.Name == "IMAGES");
        var icons = r.Entries.First(e => e.Name == "ICONS");
        Assert.Equal(images.Index, icons.ParentIndex);   // ICONS' parent is IMAGES
        Assert.Equal(1, r.Entries[0].ParentIndex);        // root's parent is itself (index 1)
    }

    [Fact]
    public void Corrupting_the_l_table_extent_breaks_l_m_agreement()
    {
        var img = BuildIso();
        // Locate the Type-L path table and corrupt the extent of its second record.
        uint lLoc = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(16 * 2048 + 140));
        long o = (long)lLoc * 2048;
        // First record is root: len(1)+extattr(1)+extent(4)+parent(2)+name(1)+pad(1) = 10 bytes.
        int firstLen = img[o];
        long second = o + 8 + firstLen + (firstLen & 1);
        img[second + 2] ^= 0xFF;   // wreck the little-endian extent of entry 2

        var r = IsoPathTable.Read(img);
        Assert.False(r.Ok);
        Assert.Contains(r.Findings, x => x.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_truncated_image_is_reported()
    {
        var r = IsoPathTable.Read(new byte[2048]);   // smaller than a descriptor set
        Assert.False(r.Ok);
        Assert.Contains(r.Findings, x => x.Message.Contains("too small"));
    }
}
