// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Partition;

/// <summary>One Apple Partition Map entry.</summary>
public sealed record ApmPartition(
    int Index, string Name, string Type, long StartBlock, long BlockCount, long SizeBytes, bool Bootable, bool Valid);

/// <summary>An Apple Partition Map and its partitions.</summary>
public sealed record ApplePartitionMap
{
    public required int BlockSize { get; init; }
    public required long DeviceBlocks { get; init; }
    public required IReadOnlyList<ApmPartition> Partitions { get; init; }

    public string Summary()
        => $"Apple Partition Map: {BlockSize}-byte blocks, {Partitions.Count} partition(s).";
}

/// <summary>
/// apm — the reader for the Apple Partition Map, the scheme Macs use on hard disks and hybrid CDs, and the
/// map that points at the HFS/HFS+ partition of a Mac+PC hybrid disc. Block 0 is a Driver Descriptor
/// Record (the "ER" signature and the device block size); the partition map itself starts at block 1, one
/// self-describing "PM" entry per block — each naming the partition and its type ("Apple_HFS",
/// "Apple_Free", "Apple_partition_map", "Apple_Driver"…), giving its start block and length, and a status
/// word. The first entry says how many entries there are. All fields are big-endian. This finds the map
/// (probing the block size), reads every entry, and derives each partition's byte extent. Sits alongside
/// the MBR/GPT/APA/RDB readers. Read-only; it parses and reports.
/// </summary>
public static class ApmDisk
{
    private const ushort DdrSignature = 0x4552;   // "ER"
    private const ushort PmSignature = 0x504D;    // "PM"
    private const int MaxPartitions = 256;

    /// <summary>Candidate block sizes to probe for the "PM" map at block 1.</summary>
    private static readonly int[] BlockSizes = { 512, 2048, 4096, 1024 };

    /// <summary>True if the image begins with an Apple Partition Map.</summary>
    public static bool IsApm(byte[] image) => FindBlockSize(image) > 0;

    /// <summary>Read the Apple Partition Map, or throw if the image carries none.</summary>
    public static ApplePartitionMap Read(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int bs = FindBlockSize(image);
        if (bs <= 0) throw new InvalidDataException("No Apple Partition Map ('ER'/'PM' signatures) in this image.");

        long deviceBlocks = image.Length >= 8 && U16(image, 0) == DdrSignature ? U32(image, 4) : image.Length / bs;

        // The first map entry (block 1) records how many entries the map has.
        int mapCount = (int)U32(image, bs + 4);
        if (mapCount is <= 0 or > MaxPartitions) mapCount = MaxPartitions;

        var parts = new List<ApmPartition>();
        for (int i = 0; i < mapCount; i++)
        {
            long o = (long)(i + 1) * bs;
            if (o + 128 > image.Length) break;
            if (U16(image, o) != PmSignature) break;

            long start = U32(image, o + 8);
            long count = U32(image, o + 12);
            string name = Ascii(image, o + 16, 32);
            string type = Ascii(image, o + 48, 32);
            uint status = U32(image, o + 88);

            parts.Add(new ApmPartition(
                Index: i,
                Name: name, Type: type,
                StartBlock: start, BlockCount: count, SizeBytes: count * bs,
                Bootable: (status & 0x08) != 0,
                Valid: (status & 0x01) != 0));
        }

        return new ApplePartitionMap { BlockSize = bs, DeviceBlocks = deviceBlocks, Partitions = parts };
    }

    public static string Render(ApplePartitionMap apm)
    {
        ArgumentNullException.ThrowIfNull(apm);
        var sb = new StringBuilder();
        sb.AppendLine(apm.Summary());
        foreach (var p in apm.Partitions)
            sb.AppendLine($"  {p.Index,2}. {p.Name,-24} {p.Type,-22} " +
                          $"blk {p.StartBlock}+{p.BlockCount}, {p.SizeBytes / (1024.0 * 1024):0.0} MiB" +
                          $"{(p.Bootable ? " [boot]" : "")}{(p.Valid ? "" : " [invalid]")}");
        return sb.ToString().TrimEnd();
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>Probe for the block size at which block 0 is a Driver Descriptor ('ER') and block 1 is a
    /// partition map entry ('PM'); returns 0 if the image is not an APM. Also accepts a bare map that opens
    /// directly with 'PM' at block 1 (some CD images omit the driver record).</summary>
    private static int FindBlockSize(byte[] image)
    {
        if (image is null || image.Length < 8) return 0;
        foreach (int bs in BlockSizes)
        {
            if ((long)bs + 8 > image.Length) continue;
            bool ddr = U16(image, 0) == DdrSignature;
            bool pm = U16(image, bs) == PmSignature;
            if (pm && (ddr || U16(image, 0) == PmSignature)) return bs;
        }
        return 0;
    }

    private static ushort U16(byte[] b, long o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan((int)o));
    private static uint U32(byte[] b, long o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)o));

    private static string Ascii(byte[] b, long o, int len)
    {
        int start = (int)o, count = 0;
        for (int i = 0; i < len && start + i < b.Length; i++)
        {
            if (b[start + i] == 0) break;
            count = i + 1;
        }
        return Encoding.ASCII.GetString(b, start, count).Trim();
    }
}
