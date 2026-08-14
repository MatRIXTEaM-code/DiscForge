// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Hfs;

/// <summary>A run of free allocation blocks that still hold non-zero data — leftover / deleted content.</summary>
public sealed record HfsOrphanRegion(int FirstBlock, int BlockCount, long ByteOffset, long ByteLength, long NonZeroBytes)
{
    public override string ToString()
        => $"blocks {FirstBlock}–{FirstBlock + BlockCount - 1} @ 0x{ByteOffset:X}, " +
           $"{ByteLength:N0} bytes ({NonZeroBytes:N0} non-zero)";
}

/// <summary>The free-space carve verdict for an HFS volume.</summary>
public sealed record HfsFreeSpaceReport
{
    public required int TotalAllocationBlocks { get; init; }
    public required int FreeBlocks { get; init; }
    public required int FreeBlocksWithData { get; init; }
    public required long AllocationBlockSize { get; init; }
    /// <summary>Total non-zero bytes sitting in free space — a floor on recoverable leftover content.</summary>
    public required long LeftoverBytes { get; init; }
    public required IReadOnlyList<HfsOrphanRegion> Regions { get; init; }

    public bool HasLeftovers => FreeBlocksWithData > 0;

    public string Summary()
        => FreeBlocksWithData == 0
            ? $"HFS free space: {FreeBlocks:N0} free block(s), all zeroed — no leftover content."
            : $"HFS free space: {FreeBlocksWithData:N0} of {FreeBlocks:N0} free block(s) still hold data — " +
              $"{LeftoverBytes:N0} non-zero byte(s) of leftover/deleted content in {Regions.Count} region(s).";
}

/// <summary>
/// hfs-orphans — the free-space carve for a classic Mac (HFS) volume, the forensic complement to the HFS
/// directory reader. The Master Directory Block records the volume bitmap: one bit per allocation block,
/// set when the block is in use. A block the bitmap marks FREE but which still holds non-zero data is
/// leftover content — a deleted file the catalog no longer lists, or slack the volume never wiped. This
/// reads the bitmap, scans every free block, and reports the runs that are not zeroed, with their byte
/// offsets and how much non-zero data each holds — the on-disc route to content the file tree cannot show.
/// Read-only; it measures and reports, it recovers nothing itself.
/// </summary>
public static class HfsFreeSpace
{
    private const int MdbOffset = 0x400;

    /// <summary>Scan an HFS image's free allocation blocks for leftover data.</summary>
    public static HfsFreeSpaceReport Analyze(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!HfsReader.IsHfs(image))
            throw new HfsFormatException("No HFS Master Directory Block (\"BD\") at 0x400 — not an HFS volume.");

        int bitmapStartSector = U16(image, MdbOffset + 0x0C);   // drVBMSt (512-byte sectors)
        int numAllocBlocks = U16(image, MdbOffset + 0x12);      // drNmAlBlks
        long allocBlockSize = U32(image, MdbOffset + 0x14);     // drAlBlkSiz
        int allocStartSector = U16(image, MdbOffset + 0x1C);    // drAlBlSt (512-byte sectors)

        if (allocBlockSize is <= 0 || allocBlockSize % 512 != 0)
            throw new HfsFormatException($"Implausible allocation block size {allocBlockSize}.");
        if (numAllocBlocks is <= 0 or > 20_000_000)
            throw new HfsFormatException($"Implausible allocation block count {numAllocBlocks}.");

        long bitmapOffset = (long)bitmapStartSector * 512;
        long allocOffset = (long)allocStartSector * 512;

        int free = 0, freeWithData = 0;
        long leftover = 0;
        var regions = new List<HfsOrphanRegion>();

        // Merge consecutive leftover blocks into a single region.
        int runStart = -1, runCount = 0;
        long runNonZero = 0;

        void FlushRun()
        {
            if (runStart < 0) return;
            long off = allocOffset + (long)runStart * allocBlockSize;
            regions.Add(new HfsOrphanRegion(runStart, runCount, off, runCount * allocBlockSize, runNonZero));
            runStart = -1; runCount = 0; runNonZero = 0;
        }

        for (int b = 0; b < numAllocBlocks; b++)
        {
            long bitByte = bitmapOffset + b / 8;
            if (bitByte >= image.Length) break;                 // bitmap runs past the image
            bool allocated = (image[bitByte] & (0x80 >> (b & 7))) != 0;
            if (allocated) { FlushRun(); continue; }

            free++;
            long blkOff = allocOffset + (long)b * allocBlockSize;
            if (blkOff + allocBlockSize > image.Length) { FlushRun(); continue; }

            long nonZero = 0;
            for (long i = 0; i < allocBlockSize; i++)
                if (image[blkOff + i] != 0) nonZero++;

            if (nonZero > 0)
            {
                freeWithData++;
                leftover += nonZero;
                if (runStart < 0) runStart = b;
                runCount++;
                runNonZero += nonZero;
            }
            else FlushRun();
        }
        FlushRun();

        return new HfsFreeSpaceReport
        {
            TotalAllocationBlocks = numAllocBlocks,
            FreeBlocks = free,
            FreeBlocksWithData = freeWithData,
            AllocationBlockSize = allocBlockSize,
            LeftoverBytes = leftover,
            Regions = regions,
        };
    }

    public static string Render(HfsFreeSpaceReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var region in r.Regions.Take(50)) sb.AppendLine($"  {region}");
        if (r.Regions.Count > 50) sb.AppendLine($"  … and {r.Regions.Count - 50} more region(s)");
        return sb.ToString().TrimEnd();
    }

    private static int U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
}
