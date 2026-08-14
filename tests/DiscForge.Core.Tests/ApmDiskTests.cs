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

public class ApmDiskTests
{
    private const int Bs = 512;

    private static void Be16(byte[] b, long o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan((int)o), v);
    private static void Be32(byte[] b, long o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan((int)o), v);
    private static void Str(byte[] b, long o, string s, int len)
    {
        var d = Encoding.ASCII.GetBytes(s);
        for (int i = 0; i < len; i++) b[o + i] = i < d.Length ? d[i] : (byte)0;
    }

    // Driver record + a 3-entry map: the map itself, an HFS partition, and free space.
    private static byte[] Build()
    {
        var img = new byte[64 * Bs];

        // Block 0: Driver Descriptor Record.
        Be16(img, 0, 0x4552);            // "ER"
        Be16(img, 2, Bs);                // block size
        Be32(img, 4, 64);                // block count

        void Entry(int i, string name, string type, uint start, uint count, uint status)
        {
            long o = (long)(i + 1) * Bs;
            Be16(img, o, 0x504D);        // "PM"
            Be32(img, o + 4, 3);         // pmMapBlkCnt (total map entries)
            Be32(img, o + 8, start);     // pmPyPartStart
            Be32(img, o + 12, count);    // pmPartBlkCnt
            Str(img, o + 16, name, 32);  // pmPartName
            Str(img, o + 48, type, 32);  // pmParType
            Be32(img, o + 88, status);   // pmPartStatus
        }
        Entry(0, "Apple", "Apple_partition_map", 1, 3, 0x01);
        Entry(1, "MacOS", "Apple_HFS", 4, 40, 0x01 | 0x08);   // valid + bootable
        Entry(2, "Extra", "Apple_Free", 44, 20, 0x00);

        return img;
    }

    [Fact]
    public void Recognises_an_apm_image()
    {
        Assert.True(ApmDisk.IsApm(Build()));
        Assert.False(ApmDisk.IsApm(new byte[Bs * 4]));   // all zero
    }

    [Fact]
    public void Reads_all_partition_entries()
    {
        var apm = ApmDisk.Read(Build());
        Assert.Equal(512, apm.BlockSize);
        Assert.Equal(3, apm.Partitions.Count);
        Assert.Equal("Apple_partition_map", apm.Partitions[0].Type);
        Assert.Equal("Apple_HFS", apm.Partitions[1].Type);
        Assert.Equal("Apple_Free", apm.Partitions[2].Type);
    }

    [Fact]
    public void Derives_extent_and_flags()
    {
        var apm = ApmDisk.Read(Build());
        var hfs = apm.Partitions[1];
        Assert.Equal("MacOS", hfs.Name);
        Assert.Equal(4, hfs.StartBlock);
        Assert.Equal(40, hfs.BlockCount);
        Assert.Equal(40L * Bs, hfs.SizeBytes);
        Assert.True(hfs.Bootable);
        Assert.True(hfs.Valid);
        Assert.False(apm.Partitions[2].Valid);   // Apple_Free has status 0
    }

    [Fact]
    public void Probes_a_2048_byte_block_size()
    {
        // A CD-style APM at 2048-byte blocks (no driver record — opens directly with 'PM' at block 1).
        int bs = 2048;
        var img = new byte[16 * bs];
        Be16(img, 0, 0x504D);            // some CD images put 'PM' at block 0 too; here just make block 1 valid
        long o = bs;
        Be16(img, o, 0x504D); Be32(img, o + 4, 1); Be32(img, o + 8, 1); Be32(img, o + 12, 10);
        Str(img, o + 16, "CD", 32); Str(img, o + 48, "Apple_HFS", 32); Be32(img, o + 88, 0x01);

        Assert.True(ApmDisk.IsApm(img));
        var apm = ApmDisk.Read(img);
        Assert.Equal(2048, apm.BlockSize);
        Assert.Single(apm.Partitions);
        Assert.Equal("Apple_HFS", apm.Partitions[0].Type);
    }

    [Fact]
    public void A_non_apm_image_throws()
    {
        Assert.Throws<InvalidDataException>(() => ApmDisk.Read(new byte[Bs * 4]));
    }
}
