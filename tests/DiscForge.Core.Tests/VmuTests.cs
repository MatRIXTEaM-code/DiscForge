// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Vmu;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the Dreamcast VMU reader. A formatted card is built by hand to the
/// documented layout (root, FAT, directory, a two-block save) and read back: the
/// checks pin the directory-entry decode, the FAT-chain extraction, the VMS
/// descriptions, and that the copy-protect flag is honoured rather than ignored.
/// </summary>
public class VmuTests
{
    private static void U16(byte[] b, int at, int v) =>
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at, 2), (ushort)v);

    // Build a formatted VMU with one 2-block data save "MYSAVE" at blocks 0-1.
    private static byte[] BuildVmu(byte copyProtect = 0x00)
    {
        var d = new byte[VmuImage.ImageSize];
        int root = 255 * VmuImage.BlockSize;
        for (int i = 0; i < 16; i++) d[root + i] = 0x55;   // formatted marker
        U16(d, root + 0x46, 254);   // FAT location
        U16(d, root + 0x4A, 253);   // directory location
        U16(d, root + 0x4C, 13);    // directory size (blocks)
        U16(d, root + 0x50, 200);   // user blocks

        // FAT: default everything unallocated, then chain blocks 0 -> 1 -> end.
        int fat = 254 * VmuImage.BlockSize;
        for (int i = 0; i < 256; i++) U16(d, fat + i * 2, 0xFFFC);
        U16(d, fat + 0 * 2, 1);        // block 0 -> block 1
        U16(d, fat + 1 * 2, 0xFFFA);   // block 1 -> last

        // Directory entry 0 in block 253.
        int dir = 253 * VmuImage.BlockSize;
        d[dir + 0] = 0x33;             // data save
        d[dir + 1] = copyProtect;      // copy-protect flag
        U16(d, dir + 2, 0);            // first block
        Encoding.ASCII.GetBytes("MYSAVE").CopyTo(d, dir + 4);
        U16(d, dir + 0x18, 2);         // size in blocks
        U16(d, dir + 0x1A, 0);         // header at block 0

        // The file data: VMS header (descriptions) at block 0, payload in block 1.
        int f0 = 0 * VmuImage.BlockSize;
        Encoding.ASCII.GetBytes("SONIC SAVE").CopyTo(d, f0 + 0x00);        // 16-char short
        Encoding.ASCII.GetBytes("Sonic Adventure save game").CopyTo(d, f0 + 0x10); // 32-char long
        for (int i = 0; i < VmuImage.BlockSize; i++) d[VmuImage.BlockSize + i] = (byte)(i & 0xFF);

        return d;
    }

    [Fact]
    public void A_formatted_card_is_recognised_and_its_geometry_read()
    {
        var vmu = VmuImage.Read(BuildVmu());
        Assert.True(vmu.Formatted);
        Assert.Equal(256, vmu.TotalBlocks);
        Assert.Equal(200, vmu.UserBlocks);
        Assert.True(VmuImage.IsVmu(BuildVmu()));
    }

    [Fact]
    public void The_directory_entry_reads_name_type_and_size()
    {
        var file = Assert.Single(VmuImage.Read(BuildVmu()).Files);
        Assert.Equal("MYSAVE", file.Name);
        Assert.Equal(0x33, file.FileType);
        Assert.False(file.IsGame);
        Assert.Equal(2, file.SizeBlocks);
        Assert.Equal(1024, file.SizeBytes);
        Assert.Equal("SONIC SAVE", file.ShortDescription);
        Assert.Equal("Sonic Adventure save game", file.LongDescription);
    }

    [Fact]
    public void Extraction_follows_the_fat_chain()
    {
        var image = BuildVmu();
        var file = VmuImage.Read(image).Files.Single();
        var bytes = VmuImage.Extract(image, file);

        Assert.Equal(1024, bytes.Length);                 // two blocks
        Assert.Equal("SONIC SAVE", Encoding.ASCII.GetString(bytes, 0, 10));
        Assert.Equal((byte)0x05, bytes[512 + 5]);         // block 1's payload pattern
    }

    [Fact]
    public void The_copy_protect_flag_is_honoured_unless_forced()
    {
        var image = BuildVmu(copyProtect: 0xFF);
        var file = VmuImage.Read(image).Files.Single();

        Assert.True(file.CopyProtected);
        Assert.Throws<InvalidOperationException>(() => VmuImage.Extract(image, file));
        Assert.Equal(1024, VmuImage.Extract(image, file, force: true).Length);
    }

    [Fact]
    public void A_wrong_size_image_is_refused()
    {
        Assert.Throws<VmuFormatException>(() => VmuImage.Read(new byte[1000]));
        Assert.False(VmuImage.IsVmu(new byte[1000]));
    }

    [Fact]
    public void Free_blocks_are_counted()
    {
        // 200 user blocks, two used by the save → 198 free.
        Assert.Equal(198, VmuImage.Read(BuildVmu()).FreeBlocks);
    }
}
