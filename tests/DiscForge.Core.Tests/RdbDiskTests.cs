// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Partition;
using Xunit;

namespace DiscForge.Core.Tests;

public class RdbDiskTests
{
    private const int Bs = 512;

    private static void Be32(byte[] b, long o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan((int)o), v);

    // Set the block's checksum (offset+8) so the additive sum of `longs` longwords is zero.
    private static void FixChecksum(byte[] b, long o, int longs)
    {
        Be32(b, o + 8, 0);
        uint sum = 0;
        for (int i = 0; i < longs; i++) sum += BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)o + i * 4));
        Be32(b, o + 8, (uint)(-(long)sum));
    }

    private static byte[] Build()
    {
        var img = new byte[Bs * 8];

        // RDB in block 0.
        Be32(img, 0x00, 0x5244534B);   // "RDSK"
        Be32(img, 0x04, 64);           // size_of_block (longwords)
        Be32(img, 0x10, Bs);           // block size
        Be32(img, 0x1C, 1);            // partition list → block 1
        Be32(img, 0x40, 100);          // cylinders
        Be32(img, 0x44, 32);           // sectors per track
        Be32(img, 0x48, 4);            // heads
        Encoding.ASCII.GetBytes("DFORGE").CopyTo(img, 0xA8);   // vendor
        Encoding.ASCII.GetBytes("HD0").CopyTo(img, 0xB0);      // product
        FixChecksum(img, 0, 64);

        // PART in block 1: "DH0", FFS, cyl 2..49, bootable.
        long p = 1 * Bs;
        Be32(img, p + 0x00, 0x50415254);       // "PART"
        Be32(img, p + 0x04, 64);
        Be32(img, p + 0x10, 0xFFFFFFFF);       // next = end
        Be32(img, p + 0x14, 0x1);              // flags: bootable
        img[p + 0x24] = 3;                      // BCPL name length
        Encoding.ASCII.GetBytes("DH0").CopyTo(img, (int)p + 0x25);
        Be32(img, p + 0x8C, 4);                // de_Surfaces (heads)
        Be32(img, p + 0x94, 32);               // de_BlocksPerTrack
        Be32(img, p + 0xA4, 2);                // de_LowCyl
        Be32(img, p + 0xA8, 49);               // de_HighCyl
        Be32(img, p + 0xBC, 0);                // boot pri
        Be32(img, p + 0xC0, 0x444F5301);       // DOSType "DOS\1" (FFS)
        FixChecksum(img, p, 64);

        return img;
    }

    [Fact]
    public void Finds_and_reads_the_rdb()
    {
        var rdb = RdbDisk.Read(Build());
        Assert.Equal(512, rdb.BlockSize);
        Assert.Equal(100u, rdb.Cylinders);
        Assert.Equal(4u, rdb.Heads);
        Assert.Equal(32u, rdb.SectorsPerTrack);
        Assert.Equal("DFORGE", rdb.Vendor);
        Assert.Equal("HD0", rdb.Product);
        Assert.True(rdb.ChecksumValid);
    }

    [Fact]
    public void Reads_the_partition_and_its_geometry()
    {
        var rdb = RdbDisk.Read(Build());
        var p = Assert.Single(rdb.Partitions);
        Assert.Equal("DH0", p.Name);
        Assert.True(p.Bootable);
        Assert.Equal("DOS\\1", p.DosType);
        Assert.Equal("FFS", p.FileSystem);
        Assert.Equal(2u, p.LowCylinder);
        Assert.Equal(49u, p.HighCylinder);

        // cylBlocks = heads(4) × blocksPerTrack(32) = 128; start = 2×128 = 256; count = 48×128 = 6144.
        Assert.Equal(256, p.StartBlock);
        Assert.Equal(6144, p.BlockCount);
        Assert.Equal(6144L * 512, p.SizeBytes);
    }

    [Fact]
    public void Decodes_common_dos_types()
    {
        Assert.Equal("OFS", RdbDisk.DosTypeName(0x444F5300));
        Assert.Equal("FFS-INTL", RdbDisk.DosTypeName(0x444F5303));
        Assert.Equal("Smart File System", RdbDisk.DosTypeName(0x53465300));
        Assert.Equal("Professional File System", RdbDisk.DosTypeName(0x50465300));
        Assert.Equal("DOS\\2", RdbDisk.RenderDosType(0x444F5302));
    }

    [Fact]
    public void A_bad_checksum_is_reported_but_still_parses()
    {
        var img = Build();
        img[0x30] ^= 0xFF;   // corrupt an RDB field without fixing the checksum
        var rdb = RdbDisk.Read(img);
        Assert.False(rdb.ChecksumValid);
        Assert.Single(rdb.Partitions);   // partitions still read
    }

    [Fact]
    public void A_non_rdb_image_is_detected()
    {
        Assert.False(RdbDisk.IsRdb(new byte[Bs * 4]));
        Assert.Throws<InvalidDataException>(() => RdbDisk.Read(new byte[Bs * 4]));
    }

    [Fact]
    public void A_partition_loop_is_guarded()
    {
        // A PART whose "next" points back to itself must not loop forever.
        var img = Build();
        Be32(img, 1 * Bs + 0x10, 1);   // next → self
        FixChecksum(img, 1 * Bs, 64);
        var rdb = RdbDisk.Read(img);
        Assert.Single(rdb.Partitions);
    }
}
