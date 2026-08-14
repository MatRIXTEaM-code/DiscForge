// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Hfs;
using Xunit;

namespace DiscForge.Core.Tests;

public class HfsFreeSpaceTests
{
    private const int Mdb = 0x400;

    // Build a minimal HFS-shaped image: a Master Directory Block, a volume bitmap, and allocation blocks.
    // blockBits[i] = true → block i allocated. dataBlocks = which free blocks carry non-zero bytes.
    private static byte[] Build(bool[] blockBits, IEnumerable<int> dataBlocks,
        int allocBlockSize = 512, int allocStartSector = 8, int bitmapStartSector = 3)
    {
        int n = blockBits.Length;
        long size = (long)allocStartSector * 512 + (long)n * allocBlockSize;
        var img = new byte[size];

        BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(Mdb), 0x4244);          // "BD"
        BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(Mdb + 0x0C), (ushort)bitmapStartSector);
        BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(Mdb + 0x12), (ushort)n);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(Mdb + 0x14), (uint)allocBlockSize);
        BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(Mdb + 0x1C), (ushort)allocStartSector);

        int bitmapOff = bitmapStartSector * 512;
        for (int b = 0; b < n; b++)
            if (blockBits[b]) img[bitmapOff + b / 8] |= (byte)(0x80 >> (b & 7));

        foreach (int b in dataBlocks)
        {
            long off = (long)allocStartSector * 512 + (long)b * allocBlockSize;
            img[off] = 0xDE; img[off + 1] = 0xAD;   // some non-zero content
        }
        return img;
    }

    [Fact]
    public void Finds_leftover_data_in_a_free_block()
    {
        // Blocks 0–3 allocated, 4–15 free; block 5 (free) still holds data.
        var bits = new bool[16];
        bits[0] = bits[1] = bits[2] = bits[3] = true;
        var r = HfsFreeSpace.Analyze(Build(bits, new[] { 5 }));

        Assert.True(r.HasLeftovers);
        Assert.Equal(12, r.FreeBlocks);
        Assert.Equal(1, r.FreeBlocksWithData);
        Assert.Equal(2, r.LeftoverBytes);            // two non-zero bytes
        var region = Assert.Single(r.Regions);
        Assert.Equal(5, region.FirstBlock);
        Assert.Equal(1, region.BlockCount);
    }

    [Fact]
    public void Merges_consecutive_leftover_blocks_into_one_region()
    {
        var bits = new bool[16];   // all free
        var r = HfsFreeSpace.Analyze(Build(bits, new[] { 6, 7, 8 }));
        var region = Assert.Single(r.Regions);
        Assert.Equal(6, region.FirstBlock);
        Assert.Equal(3, region.BlockCount);
    }

    [Fact]
    public void Separated_leftovers_are_distinct_regions()
    {
        var bits = new bool[16];
        var r = HfsFreeSpace.Analyze(Build(bits, new[] { 2, 10 }));
        Assert.Equal(2, r.Regions.Count);
    }

    [Fact]
    public void Zeroed_free_space_reports_no_leftovers()
    {
        var bits = new bool[16];
        bits[0] = true;
        var r = HfsFreeSpace.Analyze(Build(bits, Array.Empty<int>()));
        Assert.False(r.HasLeftovers);
        Assert.Empty(r.Regions);
        Assert.Contains("all zeroed", r.Summary());
    }

    [Fact]
    public void Allocated_blocks_with_data_are_not_leftovers()
    {
        // Block 5 is allocated AND has data — that's a live file, not leftover.
        var bits = new bool[16];
        bits[5] = true;
        var r = HfsFreeSpace.Analyze(Build(bits, new[] { 5 }));
        Assert.False(r.HasLeftovers);
    }

    [Fact]
    public void A_non_hfs_image_throws()
    {
        Assert.Throws<HfsFormatException>(() => HfsFreeSpace.Analyze(new byte[0x800]));
    }
}
