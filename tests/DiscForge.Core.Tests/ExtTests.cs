// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.FileSystems;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The ext2/3/4 reader, proven at two levels. The two error-prone primitives — the extent-leaf decoder
/// and the linear directory parser — are checked against hand-built vectors. Then two synthetic volumes
/// exercise the two data paths end to end: an ext4 volume whose files are extent-mapped, and an ext2
/// volume whose large file spans the twelve direct pointers plus one single-indirect block.
/// </summary>
public class ExtTests
{
    private static void WU16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void WU32(byte[] b, int o, uint v) { for (int i = 0; i < 4; i++) b[o + i] = (byte)(v >> (8 * i)); }

    // ---- pure primitives ---------------------------------------------------

    [Fact]
    public void ParseExtentLeaf_decodes_entries_and_uninitialized_flag()
    {
        var node = new byte[12 + 2 * 12];
        WU16(node, 0, 0xF30A);   // magic
        WU16(node, 2, 2);        // entries
        WU16(node, 4, 4);        // max
        WU16(node, 6, 0);        // depth (leaf)
        // extent 0: logical 0, len 3, phys 100
        WU32(node, 12, 0); WU16(node, 16, 3); WU16(node, 18, 0); WU32(node, 20, 100);
        // extent 1: logical 3, len (32768 + 5) → uninitialized run of 5, phys 200
        WU32(node, 24, 3); WU16(node, 28, 32768 + 5); WU16(node, 30, 0); WU32(node, 32, 200);

        var ex = Ext.ParseExtentLeaf(node);
        Assert.Equal(2, ex.Count);
        Assert.Equal((uint)3, ex[0].Length); Assert.Equal((ulong)100, ex[0].PhysicalBlock); Assert.False(ex[0].Uninitialized);
        Assert.Equal((uint)5, ex[1].Length); Assert.Equal((ulong)200, ex[1].PhysicalBlock); Assert.True(ex[1].Uninitialized);
    }

    [Fact]
    public void ParseDirectoryBlock_reads_entries_and_skips_unused_slots()
    {
        var blk = new byte[128];
        int o = 0;
        // "." (inode 2, dir)
        WU32(blk, o, 2); WU16(blk, o + 4, 12); blk[o + 6] = 1; blk[o + 7] = 2; blk[o + 8] = (byte)'.'; o += 12;
        // an unused slot (inode 0) that still advances
        WU32(blk, o, 0); WU16(blk, o + 4, 12); blk[o + 6] = 4; o += 12;
        // "file" (inode 7, regular)
        WU32(blk, o, 7); WU16(blk, o + 4, 12); blk[o + 6] = 4; blk[o + 7] = 1;
        Encoding.ASCII.GetBytes("file").CopyTo(blk, o + 8);

        var entries = Ext.ParseDirectoryBlock(blk, 1024);
        Assert.Equal(2, entries.Count);
        Assert.Equal(".", entries[0].Name); Assert.Equal(2, entries[0].Inode);
        Assert.Equal("file", entries[1].Name); Assert.Equal(7, entries[1].Inode); Assert.Equal(1, entries[1].FileType);
    }

    // ---- shared image scaffolding ------------------------------------------

    private const int Bs = 1024;

    private static void WriteSuperblock(byte[] disk, int inodeSize, uint incompat, string label)
    {
        int sb = 1024;
        WU32(disk, sb + 0, 32);       // inodes_count (plenty)
        WU32(disk, sb + 4, 32);       // blocks_count_lo
        WU32(disk, sb + 24, 0);       // log_block_size → 1024
        WU32(disk, sb + 32, 64);      // blocks_per_group
        WU32(disk, sb + 40, 16);      // inodes_per_group
        WU16(disk, sb + 56, 0xEF53);  // magic
        WU32(disk, sb + 76, 1);       // rev_level = DYNAMIC (honours inode_size)
        WU32(disk, sb + 84, 11);      // first_ino
        WU16(disk, sb + 88, inodeSize);
        WU32(disk, sb + 96, incompat);
        Encoding.ASCII.GetBytes(label).CopyTo(disk, sb + 120);
    }

    private static void WriteGroupDesc(byte[] disk, uint inodeTableBlock)
    {
        int gd = 2 * Bs;              // GDT is block 2 when block size is 1024
        WU32(disk, gd + 8, inodeTableBlock);
    }

    private static void WriteExtentInode(byte[] disk, int inodeOffset, ushort mode, long size,
                                         uint startBlock, ushort lenBlocks)
    {
        WU16(disk, inodeOffset + 0, mode);
        WU32(disk, inodeOffset + 4, (uint)size);
        WU32(disk, inodeOffset + 32, 0x0008_0000);  // EXTENTS_FL
        int ib = inodeOffset + 40;                   // i_block: one leaf extent
        WU16(disk, ib + 0, 0xF30A); WU16(disk, ib + 2, 1); WU16(disk, ib + 4, 4); WU16(disk, ib + 6, 0);
        WU32(disk, ib + 12, 0); WU16(disk, ib + 16, lenBlocks); WU16(disk, ib + 18, 0); WU32(disk, ib + 20, startBlock);
    }

    // Two leaf extents in one inode, with a logical gap between them (a hole) when logical1 > 1.
    private static void WriteTwoExtentInode(byte[] disk, int inodeOffset, ushort mode, long size,
        uint logical0, ushort len0, uint start0, uint logical1, ushort len1, uint start1)
    {
        WU16(disk, inodeOffset + 0, mode);
        WU32(disk, inodeOffset + 4, (uint)size);
        WU32(disk, inodeOffset + 32, 0x0008_0000);   // EXTENTS_FL
        int ib = inodeOffset + 40;
        WU16(disk, ib + 0, 0xF30A); WU16(disk, ib + 2, 2); WU16(disk, ib + 4, 4); WU16(disk, ib + 6, 0);
        WU32(disk, ib + 12, logical0); WU16(disk, ib + 16, len0); WU16(disk, ib + 18, 0); WU32(disk, ib + 20, start0);
        WU32(disk, ib + 24, logical1); WU16(disk, ib + 28, len1); WU16(disk, ib + 30, 0); WU32(disk, ib + 32, start1);
    }

    private static int WriteDirEntry(byte[] disk, int o, uint inode, int recLen, byte fileType, string name)
    {
        WU32(disk, o, inode);
        WU16(disk, o + 4, recLen);
        disk[o + 6] = (byte)name.Length;
        disk[o + 7] = fileType;
        Encoding.ASCII.GetBytes(name).CopyTo(disk, o + 8);
        return o + recLen;
    }

    // ---- ext4 (extent-mapped) end to end -----------------------------------

    [Fact]
    public void Ext4_opens_lists_resolves_and_extracts_extent_files()
    {
        var hello = Encoding.ASCII.GetBytes("Hello, ext4!");
        var big = new byte[1500];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 5 + 3);

        // Sparse file: block A at logical 0, a hole at logical 1, block C at logical 2 (3 blocks total).
        var blockA = new byte[Bs]; for (int i = 0; i < Bs; i++) blockA[i] = (byte)(i + 1);
        var blockC = new byte[Bs]; for (int i = 0; i < Bs; i++) blockC[i] = (byte)(255 - (i & 0xFF));

        var disk = new byte[20 * Bs];
        WriteSuperblock(disk, inodeSize: 256, incompat: 0x42 /* EXTENTS|FILETYPE */, label: "TESTEXT4");
        WriteGroupDesc(disk, inodeTableBlock: 5);
        int itbl = 5 * Bs;

        WriteExtentInode(disk, itbl + 1 * 256, 0x41ED, size: 1024, startBlock: 9, lenBlocks: 1);   // inode 2 root
        WriteExtentInode(disk, itbl + 10 * 256, 0x81A4, size: hello.Length, startBlock: 10, lenBlocks: 1); // inode 11
        WriteExtentInode(disk, itbl + 11 * 256, 0x81A4, size: big.Length, startBlock: 11, lenBlocks: 2);   // inode 12
        WriteTwoExtentInode(disk, itbl + 12 * 256, 0x81A4, size: 3 * Bs,   // inode 13, sparse
            logical0: 0, len0: 1, start0: 13, logical1: 2, len1: 1, start1: 14);

        int d = 9 * Bs;
        d = WriteDirEntry(disk, d, 2, 12, 2, ".");
        d = WriteDirEntry(disk, d, 2, 12, 2, "..");
        d = WriteDirEntry(disk, d, 11, 20, 1, "hello.txt");
        d = WriteDirEntry(disk, d, 12, 20, 1, "big.bin");
        WriteDirEntry(disk, d, 13, 960, 1, "sparse.bin");

        hello.CopyTo(disk, 10 * Bs);
        big.CopyTo(disk, 11 * Bs);
        blockA.CopyTo(disk, 13 * Bs);
        blockC.CopyTo(disk, 14 * Bs);

        using var s = new MemoryStream(disk);
        var vol = Ext.Open(s);
        Assert.Equal(1024, vol.Info.BlockSize);
        Assert.Equal("TESTEXT4", vol.Info.Label);

        var entries = vol.List(Ext.RootInode);
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Name == "hello.txt" && e.Size == hello.Length && !e.IsDirectory);
        Assert.Contains(entries, e => e.Name == "big.bin" && e.Size == big.Length);
        Assert.Contains(entries, e => e.Name == "sparse.bin");

        using (var outMs = new MemoryStream())
        {
            Assert.Equal(hello.Length, vol.Extract(vol.Resolve("hello.txt")!, outMs));
            Assert.Equal(hello, outMs.ToArray());
        }
        using (var outMs = new MemoryStream())
        {
            Assert.Equal(big.Length, vol.Extract(vol.Resolve("big.bin")!, outMs));
            Assert.Equal(big, outMs.ToArray());
        }
        // The sparse file must come back as [blockA][hole of zeros][blockC], NOT the two blocks compacted.
        using (var outMs = new MemoryStream())
        {
            Assert.Equal(3 * Bs, vol.Extract(vol.Resolve("sparse.bin")!, outMs));
            var got = outMs.ToArray();
            Assert.Equal(blockA, got.AsSpan(0, Bs).ToArray());
            Assert.All(got.AsSpan(Bs, Bs).ToArray(), b => Assert.Equal(0, b));
            Assert.Equal(blockC, got.AsSpan(2 * Bs, Bs).ToArray());
        }
    }

    // ---- ext2 (classic block map: direct + single indirect) ----------------

    [Fact]
    public void Ext2_extracts_a_file_spanning_direct_and_indirect_blocks()
    {
        // 13 blocks of data → 12 direct pointers + 1 through the single-indirect block.
        var big = new byte[13 * Bs];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 3 + 1);
        var hello = Encoding.ASCII.GetBytes("Hello, ext2 classic!");

        var disk = new byte[24 * Bs];
        WriteSuperblock(disk, inodeSize: 128, incompat: 0x02 /* FILETYPE, no extents */, label: "TESTEXT2");
        WriteGroupDesc(disk, inodeTableBlock: 5);
        int itbl = 5 * Bs;

        // Root inode 2 — classic dir, direct[0] = block 7.
        int root = itbl + 1 * 128;
        WU16(disk, root + 0, 0x41ED); WU32(disk, root + 4, 1024); WU32(disk, root + 32, 0);
        WU32(disk, root + 40, 7);

        // hello2 inode 11 — direct[0] = block 8.
        int hi = itbl + 10 * 128;
        WU16(disk, hi + 0, 0x81A4); WU32(disk, hi + 4, (uint)hello.Length); WU32(disk, hi + 40, 8);

        // big2 inode 12 — direct[0..11] = blocks 9..20, single-indirect = block 22 → points to block 21.
        int bi = itbl + 11 * 128;
        WU16(disk, bi + 0, 0x81A4); WU32(disk, bi + 4, (uint)big.Length);
        for (int i = 0; i < 12; i++) WU32(disk, bi + 40 + i * 4, (uint)(9 + i));
        WU32(disk, bi + 40 + 12 * 4, 22);          // i_block[12] = single indirect
        WU32(disk, 22 * Bs, 21);                    // indirect block's first pointer → block 21

        // Root directory (block 7).
        int d = 7 * Bs;
        d = WriteDirEntry(disk, d, 2, 12, 2, ".");
        d = WriteDirEntry(disk, d, 2, 12, 2, "..");
        d = WriteDirEntry(disk, d, 11, 20, 1, "hello2.txt");
        WriteDirEntry(disk, d, 12, 980, 1, "big2.bin");

        hello.CopyTo(disk, 8 * Bs);
        // Lay big2's logical blocks out: logical 0..11 → physical 9..20, logical 12 → physical 21.
        for (int L = 0; L < 13; L++)
        {
            int phys = L < 12 ? 9 + L : 21;
            Array.Copy(big, L * Bs, disk, phys * Bs, Bs);
        }

        using var s = new MemoryStream(disk);
        var vol = Ext.Open(s);
        var entries = vol.List(Ext.RootInode);
        Assert.Equal(2, entries.Count);

        using (var outMs = new MemoryStream())
        {
            Assert.Equal(hello.Length, vol.Extract(vol.Resolve("hello2.txt")!, outMs));
            Assert.Equal(hello, outMs.ToArray());
        }
        using (var outMs = new MemoryStream())
        {
            Assert.Equal(big.Length, vol.Extract(vol.Resolve("big2.bin")!, outMs));
            Assert.Equal(big, outMs.ToArray());
        }
    }

    [Fact]
    public void Rejects_a_non_ext_image()
    {
        using var s = new MemoryStream(new byte[4096]);
        Assert.Throws<InvalidDataException>(() => Ext.Open(s));
    }
}
