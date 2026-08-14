// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Udf;

/// <summary>A run of free UDF blocks that still hold non-zero data — leftover / deleted content.</summary>
public sealed record UdfOrphanRegion(long FirstBlock, long BlockCount, long ByteOffset, long ByteLength, long NonZeroBytes)
{
    public override string ToString()
        => $"blocks {FirstBlock}–{FirstBlock + BlockCount - 1} @ 0x{ByteOffset:X}, " +
           $"{ByteLength:N0} bytes ({NonZeroBytes:N0} non-zero)";
}

/// <summary>The free-space carve verdict for a UDF partition.</summary>
public sealed record UdfFreeSpaceReport
{
    public required long PartitionStart { get; init; }
    public required long PartitionBlocks { get; init; }
    public required int BlockSize { get; init; }
    public required long FreeBlocks { get; init; }
    public required long FreeBlocksWithData { get; init; }
    public required long LeftoverBytes { get; init; }
    public required IReadOnlyList<UdfOrphanRegion> Regions { get; init; }
    /// <summary>True when the partition records a Space Bitmap (some UDF partitions use a space table
    /// instead, or none — read-only media); false means the carve could not run.</summary>
    public required bool HasBitmap { get; init; }

    public bool HasLeftovers => FreeBlocksWithData > 0;

    public string Summary()
    {
        if (!HasBitmap)
            return "UDF free space: no Space Bitmap on this partition (space table or read-only) — carve not available.";
        return FreeBlocksWithData == 0
            ? $"UDF free space: {FreeBlocks:N0} free block(s), all zeroed — no leftover content."
            : $"UDF free space: {FreeBlocksWithData:N0} of {FreeBlocks:N0} free block(s) still hold data — " +
              $"{LeftoverBytes:N0} non-zero byte(s) of leftover/deleted content in {Regions.Count} region(s).";
    }
}

/// <summary>
/// udf-orphans — the free-space carve for a UDF volume (the UDF-bridge sibling of the HFS carve), reaching
/// the on-disc content the file tree cannot show. The Partition Descriptor's header names an Unallocated
/// Space Bitmap: one bit per logical block, and — unlike HFS — a bit set to ONE means the block is FREE,
/// with the bits packed least-significant-first. A block the bitmap marks free but which still holds
/// non-zero data is leftover content: a deleted file the catalog no longer lists, or slack the volume never
/// wiped. This finds the Partition Descriptor via the Anchor and the Main Volume Descriptor Sequence, reads
/// the Space Bitmap, scans every free block, and reports the runs that are not zeroed with their byte
/// offsets. Bitmap semantics validated against a real mkudffs-made volume. Read-only; it measures and
/// reports, it recovers nothing itself.
/// </summary>
public static class UdfFreeSpace
{
    public const int SectorSize = 2048;
    private const int AnchorSector = 256;
    private const ushort TagAnchor = 2;
    private const ushort TagPartition = 5;
    private const ushort TagSpaceBitmap = 264;
    private const int MaxMvdsBlocks = 64;

    /// <summary>Scan a UDF image's free blocks for leftover data.</summary>
    public static UdfFreeSpaceReport Analyze(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);

        long anchorOff = (long)AnchorSector * SectorSize;
        if (anchorOff + 32 > image.Length || U16(image, anchorOff) != TagAnchor)
            throw new UdfFormatException("No UDF Anchor Volume Descriptor Pointer at sector 256 — not a UDF image.");

        // Anchor → Main Volume Descriptor Sequence extent.
        uint mvdsLen = U32(image, anchorOff + 16);
        uint mvdsLoc = U32(image, anchorOff + 20);
        int mvdsBlocks = Math.Min(MaxMvdsBlocks, (int)(mvdsLen / SectorSize));

        long pd = -1;
        for (int i = 0; i < mvdsBlocks; i++)
        {
            long o = (long)(mvdsLoc + i) * SectorSize;
            if (o + 196 > image.Length) break;
            if (U16(image, o) == TagPartition) { pd = o; break; }
        }
        if (pd < 0) throw new UdfFormatException("No Partition Descriptor found in the Main Volume Descriptor Sequence.");

        long partStart = U32(image, pd + 188);
        long partBlocks = U32(image, pd + 192);

        // Partition Header Descriptor lives in Partition Contents Use (offset 56); the Unallocated Space
        // Bitmap short_ad sits at +8 → ExtentLength @64, ExtentPosition @68 (relative to partition start).
        uint bitmapLen = U32(image, pd + 64);
        uint bitmapPos = U32(image, pd + 68);

        if (bitmapLen == 0)
            return new UdfFreeSpaceReport
            {
                PartitionStart = partStart, PartitionBlocks = partBlocks, BlockSize = SectorSize,
                FreeBlocks = 0, FreeBlocksWithData = 0, LeftoverBytes = 0,
                Regions = Array.Empty<UdfOrphanRegion>(), HasBitmap = false,
            };

        long sbd = (partStart + bitmapPos) * SectorSize;
        if (sbd + 24 > image.Length || U16(image, sbd) != TagSpaceBitmap)
            throw new UdfFormatException("The Space Bitmap Descriptor is not where the partition header says it is.");

        long numBits = U32(image, sbd + 16);
        long numBytes = U32(image, sbd + 20);
        if (numBits > partBlocks) numBits = partBlocks;                 // never scan past the partition
        long bitmapOff = sbd + 24;
        if (bitmapOff + numBytes > image.Length) numBytes = Math.Max(0, image.Length - bitmapOff);

        long free = 0, freeWithData = 0, leftover = 0;
        var regions = new List<UdfOrphanRegion>();
        long runStart = -1, runCount = 0, runNonZero = 0;

        void Flush()
        {
            if (runStart < 0) return;
            long off = (partStart + runStart) * SectorSize;
            regions.Add(new UdfOrphanRegion(partStart + runStart, runCount, off, runCount * SectorSize, runNonZero));
            runStart = -1; runCount = 0; runNonZero = 0;
        }

        for (long b = 0; b < numBits; b++)
        {
            long byteIdx = bitmapOff + b / 8;
            if (byteIdx >= image.Length) break;
            // UDF: bit == 1 means FREE, packed least-significant-bit first.
            bool freeBlock = (image[byteIdx] & (1 << (int)(b & 7))) != 0;
            if (!freeBlock) { Flush(); continue; }

            free++;
            long blkOff = (partStart + b) * SectorSize;
            if (blkOff + SectorSize > image.Length) { Flush(); continue; }

            long nz = 0;
            for (int i = 0; i < SectorSize; i++)
                if (image[blkOff + i] != 0) nz++;

            if (nz > 0)
            {
                freeWithData++; leftover += nz;
                if (runStart < 0) runStart = b;
                runCount++; runNonZero += nz;
            }
            else Flush();
        }
        Flush();

        return new UdfFreeSpaceReport
        {
            PartitionStart = partStart, PartitionBlocks = partBlocks, BlockSize = SectorSize,
            FreeBlocks = free, FreeBlocksWithData = freeWithData, LeftoverBytes = leftover,
            Regions = regions, HasBitmap = true,
        };
    }

    public static string Render(UdfFreeSpaceReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var region in r.Regions.Take(50)) sb.AppendLine($"  {region}");
        if (r.Regions.Count > 50) sb.AppendLine($"  … and {r.Regions.Count - 50} more region(s)");
        return sb.ToString().TrimEnd();
    }

    private static ushort U16(byte[] b, long o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)o));
    private static uint U32(byte[] b, long o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)o));
}
