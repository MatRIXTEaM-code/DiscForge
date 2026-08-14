// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the PS2 memory-card reader. A minimal but structurally complete card
/// is built by hand — superblock, an indirect-FAT cluster, a FAT cluster, a root
/// directory holding one save folder, and that folder holding one file — and read
/// back. This pins the double-indirect FAT walk, the directory-across-clusters
/// traversal, the alloc_offset cluster mapping, and file extraction.
/// </summary>
public class Ps2MemoryCardTests
{
    private const int Ppc = 2;
    private const int ClusterBytes = 1024;
    private const int AllocOffset = 8;

    // Byte offset of a physical cluster in a raw (no-ECC) image.
    private static int ClusterAt(int physCluster) => physCluster * ClusterBytes;

    private static void Word(byte[] d, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), v);
    private static void Half(byte[] d, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at), v);

    private static void FatEntry(byte[] d, int fatPhysCluster, int logical, uint value) =>
        Word(d, ClusterAt(fatPhysCluster) + logical * 4, value);

    private static void DirEntry(byte[] d, int physCluster, int slot, ushort mode, uint length, uint cluster, string name)
    {
        int at = ClusterAt(physCluster) + slot * 512;
        Half(d, at + 0x00, mode);
        Word(d, at + 0x04, length);
        Word(d, at + 0x10, cluster);
        Encoding.ASCII.GetBytes(name).CopyTo(d, at + 0x40);
    }

    private const ushort Dir = 0x8027;    // exists | directory | rwx
    private const ushort File = 0x8017;   // exists | file | rwx

    private static byte[] BuildCard(out byte[] fileContent)
    {
        int clusters = 64;
        var d = new byte[clusters * Ppc * 512];   // raw (no ECC)

        // Superblock (physical cluster 0, page 0).
        Encoding.ASCII.GetBytes("Sony PS2 Memory Card Format ").CopyTo(d, 0);
        Half(d, 0x28, 512);          // page_len
        Half(d, 0x2A, Ppc);          // pages_per_cluster
        Half(d, 0x2C, 16);           // pages_per_block
        Word(d, 0x30, (uint)clusters);
        Word(d, 0x34, AllocOffset);  // alloc_offset
        Word(d, 0x38, (uint)(clusters - 1));
        Word(d, 0x3C, 0);            // rootdir_cluster (logical)
        Word(d, 0x50, 1);            // ifc_list[0] -> physical cluster 1

        // Indirect FAT (physical cluster 1): slot 0 -> FAT cluster at physical 2.
        Word(d, ClusterAt(1) + 0, 2);

        // FAT cluster (physical 2), entries indexed by logical cluster.
        FatEntry(d, 2, 0, 0x80000001);   // root cluster A -> logical 1
        FatEntry(d, 2, 1, 0xFFFFFFFF);   // root cluster B -> last
        FatEntry(d, 2, 2, 0x80000003);   // save cluster A -> logical 3
        FatEntry(d, 2, 3, 0xFFFFFFFF);   // save cluster B -> last
        FatEntry(d, 2, 4, 0xFFFFFFFF);   // file cluster    -> last

        // Root directory: logical 0/1 = physical 8/9.
        DirEntry(d, 8, 0, Dir, 3, 0, ".");
        DirEntry(d, 8, 1, Dir, 3, 0, "..");
        DirEntry(d, 9, 0, Dir, 3, 2, "BASLUS-12345");   // a save folder at logical cluster 2

        // Save directory: logical 2/3 = physical 10/11.
        DirEntry(d, 10, 0, Dir, 3, 2, ".");
        DirEntry(d, 10, 1, Dir, 3, 0, "..");
        fileContent = new byte[600];
        for (int i = 0; i < fileContent.Length; i++) fileContent[i] = (byte)(i * 7 + 1);
        DirEntry(d, 11, 0, File, (uint)fileContent.Length, 4, "icon.sys");  // a file at logical cluster 4

        // File data: logical 4 = physical 12.
        fileContent.CopyTo(d, ClusterAt(12));
        return d;
    }

    [Fact]
    public void A_card_is_recognised_and_its_save_and_file_enumerated()
    {
        var card = BuildCard(out _);
        var vol = Ps2MemoryCard.Read(card);

        Assert.False(vol.HasEcc);
        var save = Assert.Single(vol.Saves);
        Assert.Equal("/BASLUS-12345", save.Path);
        Assert.True(save.IsDirectory);

        var file = Assert.Single(vol.Files);
        Assert.Equal("/BASLUS-12345/icon.sys", file.Path);
        Assert.Equal(600, file.Size);
    }

    [Fact]
    public void A_file_extracts_via_its_fat_chain()
    {
        var card = BuildCard(out var content);
        var vol = Ps2MemoryCard.Read(card);
        var file = vol.Files.Single();

        Assert.Equal(content, Ps2MemoryCard.Extract(card, vol, file));
    }

    [Fact]
    public void A_non_ps2_card_is_refused()
    {
        Assert.False(Ps2MemoryCard.IsPs2MemoryCard(new byte[512]));
        Assert.Throws<Ps2McFormatException>(() => Ps2MemoryCard.Read(new byte[512]));
    }
}
