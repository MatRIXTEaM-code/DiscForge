// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Partition;

/// <summary>One Amiga RDB partition, with its geometry-derived extent and filesystem type.</summary>
public sealed record RdbPartition
{
    public required string Name { get; init; }
    public required bool Bootable { get; init; }
    public required int BootPriority { get; init; }
    /// <summary>The 4-byte DOSType, rendered like "DOS\1" / "PFS\0".</summary>
    public required string DosType { get; init; }
    /// <summary>A friendly filesystem name for the DOSType, or "unknown".</summary>
    public required string FileSystem { get; init; }
    public required uint LowCylinder { get; init; }
    public required uint HighCylinder { get; init; }
    public required long StartBlock { get; init; }
    public required long BlockCount { get; init; }
    public required long SizeBytes { get; init; }
}

/// <summary>An Amiga Rigid Disk Block and its partitions.</summary>
public sealed record RigidDiskBlock
{
    public required int BlockSize { get; init; }
    public required uint Cylinders { get; init; }
    public required uint Heads { get; init; }
    public required uint SectorsPerTrack { get; init; }
    public required string Vendor { get; init; }
    public required string Product { get; init; }
    public required string Revision { get; init; }
    public required bool ChecksumValid { get; init; }
    public required IReadOnlyList<RdbPartition> Partitions { get; init; }

    public string Summary()
        => $"Amiga RDB: {Cylinders}c/{Heads}h/{SectorsPerTrack}s, {BlockSize}-byte blocks, " +
           $"{Partitions.Count} partition(s)" +
           (Product.Length > 0 ? $" — {Vendor} {Product} {Revision}".TrimEnd() : "") +
           (ChecksumValid ? "" : " [checksum BAD]") + ".";
}

/// <summary>
/// rdb — the reader for the Amiga Rigid Disk Block, the partition scheme of Amiga hard disks and the
/// CD32/CDTV world, alongside the MBR/GPT/APA readers. Within the first sixteen 512-byte blocks sits an
/// 'RDSK' record — drive geometry, vendor/product strings, and a pointer to a linked list of 'PART'
/// blocks. Each PART names the partition (a BCPL string), flags it bootable or not, and carries a DOS
/// environment vector giving the low/high cylinder span and the DOSType (DOS\0 = OFS, DOS\1 = FFS, PFS,
/// SFS…). From the geometry this derives each partition's start block and size. Every block carries an
/// additive checksum, which is verified. Read-only; it parses and reports.
/// </summary>
public static class RdbDisk
{
    private const uint RdskMagic = 0x5244534B;   // "RDSK"
    private const uint PartMagic = 0x50415254;   // "PART"
    private const uint EndOfList = 0xFFFFFFFF;
    private const int ScanBlocks = 16;
    private const int MaxPartitions = 128;

    /// <summary>Locate the RDB within the first 16 blocks; returns its byte offset or -1.</summary>
    public static long FindRdb(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        for (int b = 0; b < ScanBlocks; b++)
        {
            long o = (long)b * 512;
            if (o + 4 > image.Length) break;
            if (BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan((int)o)) == RdskMagic) return o;
        }
        return -1;
    }

    public static bool IsRdb(byte[] image) => FindRdb(image) >= 0;

    /// <summary>Read the RDB and its partition list.</summary>
    public static RigidDiskBlock Read(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        long rdb = FindRdb(image);
        if (rdb < 0) throw new InvalidDataException("No Amiga RDB ('RDSK') in the first 16 blocks.");

        int blockSize = (int)U32(image, rdb + 0x10);
        if (blockSize is < 128 or > 8192 || blockSize % 512 != 0) blockSize = 512;

        bool checksumOk = ChecksumValid(image, rdb);
        uint partList = U32(image, rdb + 0x1C);
        uint cyls = U32(image, rdb + 0x40);
        uint sectors = U32(image, rdb + 0x44);
        uint heads = U32(image, rdb + 0x48);
        string vendor = Ascii(image, rdb + 0xA8, 8);
        string product = Ascii(image, rdb + 0xB0, 16);
        string revision = Ascii(image, rdb + 0xC0, 4);

        var partitions = new List<RdbPartition>();
        var visited = new HashSet<uint>();
        uint ptr = partList;
        while (ptr != EndOfList && ptr != 0 && visited.Add(ptr) && partitions.Count < MaxPartitions)
        {
            long po = (long)ptr * blockSize;
            if (po + 0xD0 > image.Length) break;
            if (U32(image, po) != PartMagic) break;

            uint next = U32(image, po + 0x10);
            uint flags = U32(image, po + 0x14);
            string name = BcplString(image, po + 0x24);

            // DOS environment vector at 0x80.
            uint surfaces = U32(image, po + 0x8C);
            uint blocksPerTrack = U32(image, po + 0x94);
            uint lowCyl = U32(image, po + 0xA4);
            uint highCyl = U32(image, po + 0xA8);
            int bootPri = (int)U32(image, po + 0xBC);
            uint dosType = U32(image, po + 0xC0);

            long cylBlocks = (long)surfaces * blocksPerTrack;
            long startBlock = lowCyl * cylBlocks;
            long blockCount = (highCyl >= lowCyl ? highCyl - lowCyl + 1 : 0) * cylBlocks;

            partitions.Add(new RdbPartition
            {
                Name = name,
                Bootable = (flags & 0x1) != 0,
                BootPriority = bootPri,
                DosType = RenderDosType(dosType),
                FileSystem = DosTypeName(dosType),
                LowCylinder = lowCyl, HighCylinder = highCyl,
                StartBlock = startBlock, BlockCount = blockCount, SizeBytes = blockCount * blockSize,
            });
            ptr = next;
        }

        return new RigidDiskBlock
        {
            BlockSize = blockSize, Cylinders = cyls, Heads = heads, SectorsPerTrack = sectors,
            Vendor = vendor, Product = product, Revision = revision,
            ChecksumValid = checksumOk, Partitions = partitions,
        };
    }

    public static string Render(RigidDiskBlock rdb)
    {
        ArgumentNullException.ThrowIfNull(rdb);
        var sb = new StringBuilder();
        sb.AppendLine(rdb.Summary());
        foreach (var p in rdb.Partitions)
            sb.AppendLine($"  {p.Name,-12} {p.FileSystem} ({p.DosType})  cyl {p.LowCylinder}–{p.HighCylinder}, " +
                          $"{p.SizeBytes / (1024.0 * 1024):0.0} MiB{(p.Bootable ? $", boot pri {p.BootPriority}" : "")}");
        return sb.ToString().TrimEnd();
    }

    // ---- DOSType ------------------------------------------------------------

    /// <summary>Render a DOSType as its 3 ASCII chars plus the version byte, e.g. "DOS\1".</summary>
    public static string RenderDosType(uint dosType)
    {
        var c = new[] { (byte)(dosType >> 24), (byte)(dosType >> 16), (byte)(dosType >> 8) };
        var sb = new StringBuilder();
        foreach (var b in c) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        sb.Append('\\').Append((byte)dosType);
        return sb.ToString();
    }

    /// <summary>A friendly filesystem name for a DOSType.</summary>
    public static string DosTypeName(uint dosType)
    {
        uint fam = dosType & 0xFFFFFF00;
        byte ver = (byte)dosType;
        return fam switch
        {
            0x444F5300 => ver switch   // "DOS"
            {
                0 => "OFS", 1 => "FFS", 2 => "OFS-INTL", 3 => "FFS-INTL",
                4 => "OFS-DC", 5 => "FFS-DC", 6 => "OFS-LNFS", 7 => "FFS-LNFS",
                _ => "AmigaDOS",
            },
            0x50465300 or 0x50445300 => "Professional File System",   // "PFS"/"PDS"
            0x53465300 => "Smart File System",                        // "SFS"
            0x6D754653 or 0x6D754600 => "muFS",                       // "muFS"/"muF"
            0x4E474653 => "NGFS",
            _ => "unknown",
        };
    }

    // ---- helpers ------------------------------------------------------------

    private static bool ChecksumValid(byte[] image, long rdb)
    {
        int longs = (int)U32(image, rdb + 0x04);   // size_of_block, in longwords
        if (longs is < 1 or > 128) return false;
        if (rdb + (long)longs * 4 > image.Length) return false;
        uint sum = 0;
        for (int i = 0; i < longs; i++) sum += U32(image, rdb + i * 4);
        return sum == 0;
    }

    private static uint U32(byte[] b, long o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)o));

    private static string Ascii(byte[] b, long o, int len)
    {
        int start = (int)o;
        int count = 0;
        for (int i = 0; i < len && start + i < b.Length; i++)
        {
            byte c = b[start + i];
            if (c == 0) break;
            count = i + 1;
        }
        return Encoding.ASCII.GetString(b, start, count).Trim();
    }

    /// <summary>A BCPL string: a length byte followed by that many characters.</summary>
    private static string BcplString(byte[] b, long o)
    {
        if (o >= b.Length) return "";
        int len = b[o];
        if (len is < 0 or > 31 || o + 1 + len > b.Length) return "";
        return Encoding.ASCII.GetString(b, (int)o + 1, len).Trim();
    }
}
