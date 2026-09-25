// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.Partition;

/// <summary>
/// One partition-table entry — a primary (from the MBR), a logical partition
/// (from an EBR chain), or the extended container itself. All offsets are in
/// 512-byte LBA units, as the MBR records them.
/// </summary>
public sealed record PartitionEntry
{
    /// <summary>1-based position in the enumerated list (primaries then logicals).</summary>
    public required int Index { get; init; }
    /// <summary>The 0x80 "active"/bootable flag.</summary>
    public required bool Bootable { get; init; }
    /// <summary>The raw partition-type byte.</summary>
    public required byte Type { get; init; }
    /// <summary>A human name for <see cref="Type"/> (e.g. "FAT32", "Linux").</summary>
    public required string TypeName { get; init; }
    /// <summary>First absolute LBA of the partition.</summary>
    public required long StartLba { get; init; }
    /// <summary>Length in 512-byte sectors.</summary>
    public required long SectorCount { get; init; }
    public long StartByte => StartLba * 512;
    public long SizeBytes => SectorCount * 512;
    /// <summary>True for an extended container (type 0x05/0x0F/0x85).</summary>
    public required bool IsExtended { get; init; }
    /// <summary>True for a logical partition discovered by walking the EBR chain.</summary>
    public required bool IsLogical { get; init; }
}

/// <summary>A parsed MBR partition table: its primary and logical partitions.</summary>
public sealed record MbrDisk
{
    public required IReadOnlyList<PartitionEntry> Partitions { get; init; }
}

/// <summary>
/// Reads a classic (MBR / MS-DOS) partition table. Clean-room, from the public
/// description: sector 0 is 512 bytes, the four 16-byte primary entries live at
/// offset 0x1BE, and the boot signature 0x55 0xAA sits at 0x1FE. Each entry
/// carries a status byte (0x80 = bootable), a type byte, and a little-endian LBA
/// start and sector count (the CHS fields are ignored). An entry whose type is an
/// extended container (0x05/0x0F/0x85) is not a partition itself; its area holds a
/// singly-linked chain of EBRs whose logical partitions are enumerated here, with
/// a visited-set guard against malformed loops.
/// </summary>
public static class MbrReader
{
    /// <summary>Offset of the four primary entries within sector 0.</summary>
    public const int PartitionTableOffset = 0x1BE;
    public const int EntrySize = 16;

    /// <summary>True if the 512-byte sector carries the 0x55AA signature and a plausible entry.</summary>
    public static bool IsMbr(byte[] firstSector)
    {
        ArgumentNullException.ThrowIfNull(firstSector);
        if (firstSector.Length < 512) return false;
        if (firstSector[0x1FE] != 0x55 || firstSector[0x1FF] != 0xAA) return false;
        return HasPlausibleEntry(firstSector);
    }

    /// <summary>True if at least one primary entry has a non-zero type and sector count.</summary>
    public static bool HasPlausibleEntry(byte[] firstSector)
    {
        if (firstSector.Length < 512) return false;
        for (int i = 0; i < 4; i++)
        {
            int off = PartitionTableOffset + i * EntrySize;
            byte type = firstSector[off + 4];
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(firstSector.AsSpan(off + 12));
            if (type != 0 && count > 0) return true;
        }
        return false;
    }

    public static MbrDisk Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var ms = new MemoryStream(data, writable: false);
        return Read(ms);
    }

    public static MbrDisk Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("Reading a partition table needs a seekable stream.", nameof(stream));

        var sector = new byte[512];
        stream.Seek(0, SeekOrigin.Begin);
        if (ReadFull(stream, sector, 512) < 512)
            throw new PartitionFormatException("Image is too small to hold an MBR (needs 512 bytes).");
        if (sector[0x1FE] != 0x55 || sector[0x1FF] != 0xAA)
            throw new PartitionFormatException("No MBR boot signature (0x55 0xAA at offset 0x1FE).");

        var list = new List<PartitionEntry>();
        int index = 1;

        for (int i = 0; i < 4; i++)
        {
            int off = PartitionTableOffset + i * EntrySize;
            byte type = sector[off + 4];
            if (type == 0) continue;   // empty slot

            bool bootable = (sector[off] & 0x80) != 0;
            long startLba = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(off + 8));
            long count = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(off + 12));
            bool extended = IsExtendedType(type);

            list.Add(new PartitionEntry
            {
                Index = index++,
                Bootable = bootable,
                Type = type,
                TypeName = MbrTypes.Name(type),
                StartLba = startLba,
                SectorCount = count,
                IsExtended = extended,
                IsLogical = false,
            });

            if (extended && startLba > 0)
                EnumerateLogical(stream, startLba, list, ref index);
        }

        return new MbrDisk { Partitions = list };
    }

    // Walk the EBR chain. Each EBR's first entry is the logical partition (its LBA
    // start is relative to that EBR sector); its second entry links to the next
    // EBR (relative to the extended container's base). A visited-set plus an
    // iteration cap guard against corrupt self-referential chains.
    private static void EnumerateLogical(Stream stream, long extBaseLba, List<PartitionEntry> list, ref int index)
    {
        var visited = new HashSet<long>();
        long ebrLba = extBaseLba;
        var sector = new byte[512];

        for (int guard = 0; guard < 1024; guard++)
        {
            if (!visited.Add(ebrLba)) break;
            if (ebrLba < 0) break;

            stream.Seek(ebrLba * 512, SeekOrigin.Begin);
            if (ReadFull(stream, sector, 512) < 512) break;

            // First entry: the logical partition, offset relative to this EBR.
            int e0 = PartitionTableOffset;
            byte type0 = sector[e0 + 4];
            long rel0 = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(e0 + 8));
            long cnt0 = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(e0 + 12));
            if (type0 != 0 && cnt0 > 0)
            {
                list.Add(new PartitionEntry
                {
                    Index = index++,
                    Bootable = (sector[e0] & 0x80) != 0,
                    Type = type0,
                    TypeName = MbrTypes.Name(type0),
                    StartLba = ebrLba + rel0,
                    SectorCount = cnt0,
                    IsExtended = false,
                    IsLogical = true,
                });
            }

            // Second entry: link to the next EBR, offset relative to the container base.
            int e1 = PartitionTableOffset + EntrySize;
            byte type1 = sector[e1 + 4];
            long rel1 = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(e1 + 8));
            if (type1 == 0 || rel1 == 0) break;
            ebrLba = extBaseLba + rel1;
        }
    }

    private static bool IsExtendedType(byte type) => type is 0x05 or 0x0F or 0x85;

    private static int ReadFull(Stream s, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int r = s.Read(buffer, total, count - total);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }
}

/// <summary>Names for the common MBR partition-type bytes.</summary>
public static class MbrTypes
{
    public static string Name(byte type) => type switch
    {
        0x00 => "Empty",
        0x01 => "FAT12",
        0x04 => "FAT16 (<32 MB)",
        0x06 => "FAT16",
        0x0E => "FAT16 (LBA)",
        0x0B => "FAT32 (CHS)",
        0x0C => "FAT32 (LBA)",
        0x07 => "NTFS/exFAT",
        0x83 => "Linux",
        0x82 => "Linux swap / Solaris",
        0x05 => "Extended (CHS)",
        0x0F => "Extended (LBA)",
        0x85 => "Linux extended",
        0xEE => "GPT protective",
        0xEF => "EFI system",
        0x42 => "Windows dynamic / LDM",
        0x27 => "Windows recovery",
        0xA5 => "FreeBSD",
        0xA6 => "OpenBSD",
        0xA8 => "Mac OS X UFS",
        0xAB => "Mac OS X boot",
        0xAF => "Mac OS X HFS/HFS+",
        0xDE => "Dell utility",
        0xFB => "VMware VMFS",
        0xFC => "VMware swap",
        0x11 => "Hidden FAT12",
        0x16 => "Hidden FAT16",
        0x1B => "Hidden FAT32",
        0x1C => "Hidden FAT32 (LBA)",
        _ => $"Unknown (0x{type:X2})",
    };
}
