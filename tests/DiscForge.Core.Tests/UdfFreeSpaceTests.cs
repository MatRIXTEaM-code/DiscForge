// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Udf;
using Xunit;

namespace DiscForge.Core.Tests;

public class UdfFreeSpaceTests
{
    private const int Bs = 2048;

    private static void W16(byte[] b, long o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)o), v);
    private static void W32(byte[] b, long o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)o), v);

    // Build a minimal UDF-shaped image with the layout validated against a real mkudffs volume:
    //   Anchor @ sector 256 → MVDS @ block 96 → Partition Descriptor.
    //   Partition starts at block 100, 16 blocks; Space Bitmap at partition-relative block 0.
    //   Bitmap: bit==1 = FREE, LSB-first. Blocks 0-3 allocated, 4-15 free.
    private static byte[] Build(IEnumerable<int> dataBlocks)
    {
        const int partStart = 100, partBlocks = 16, mvds = 96;
        var img = new byte[260 * Bs];

        // Anchor @ 256.
        long anchor = 256L * Bs;
        W16(img, anchor, 2);                 // tag = Anchor
        W32(img, anchor + 16, Bs);           // MVDS extent length (1 block)
        W32(img, anchor + 20, mvds);         // MVDS location

        // Partition Descriptor @ block 96.
        long pd = (long)mvds * Bs;
        W16(img, pd, 5);                     // tag = Partition Descriptor
        W32(img, pd + 188, partStart);       // Partition Starting Location
        W32(img, pd + 192, partBlocks);      // Partition Length
        W32(img, pd + 64, Bs);               // Unallocated Space Bitmap short_ad: ExtentLength
        W32(img, pd + 68, 0);                // ExtentPosition (relative to partition start)

        // Space Bitmap Descriptor @ partition-relative block 0 = absolute block 100.
        long sbd = (long)partStart * Bs;
        W16(img, sbd, 264);                  // tag = Space Bitmap
        W32(img, sbd + 16, partBlocks);      // NumberOfBits
        W32(img, sbd + 20, 2);               // NumberOfBytes
        // bits: 0-3 allocated (0), 4-15 free (1), LSB-first.
        img[sbd + 24 + 0] = 0b1111_0000;     // blocks 0-7
        img[sbd + 24 + 1] = 0b1111_1111;     // blocks 8-15

        foreach (int rel in dataBlocks)
        {
            long off = (long)(partStart + rel) * Bs;
            img[off] = 0xDE; img[off + 1] = 0xAD;
        }
        return img;
    }

    [Fact]
    public void Finds_leftover_data_in_a_free_block()
    {
        var r = UdfFreeSpace.Analyze(Build(new[] { 5 }));   // free block 5 holds data
        Assert.True(r.HasBitmap);
        Assert.True(r.HasLeftovers);
        Assert.Equal(12, r.FreeBlocks);                     // blocks 4-15 free
        Assert.Equal(1, r.FreeBlocksWithData);
        Assert.Equal(2, r.LeftoverBytes);
        var region = Assert.Single(r.Regions);
        Assert.Equal(105, region.FirstBlock);               // absolute (partStart 100 + 5)
    }

    [Fact]
    public void Merges_consecutive_leftovers()
    {
        var r = UdfFreeSpace.Analyze(Build(new[] { 6, 7, 8 }));
        var region = Assert.Single(r.Regions);
        Assert.Equal(3, region.BlockCount);
        Assert.Equal(106, region.FirstBlock);
    }

    [Fact]
    public void Separated_leftovers_are_distinct_regions()
    {
        var r = UdfFreeSpace.Analyze(Build(new[] { 5, 11 }));
        Assert.Equal(2, r.Regions.Count);
    }

    [Fact]
    public void Allocated_block_with_data_is_not_a_leftover()
    {
        // Block 2 is allocated (bit 0) and we put data there — a live file, not leftover.
        var r = UdfFreeSpace.Analyze(Build(new[] { 2 }));
        Assert.False(r.HasLeftovers);
    }

    [Fact]
    public void Zeroed_free_space_reports_none()
    {
        var r = UdfFreeSpace.Analyze(Build(Array.Empty<int>()));
        Assert.False(r.HasLeftovers);
        Assert.Contains("all zeroed", r.Summary());
    }

    [Fact]
    public void A_non_udf_image_throws()
    {
        Assert.Throws<UdfFormatException>(() => UdfFreeSpace.Analyze(new byte[300 * Bs]));
    }
}
