// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Partition;

/// <summary>One partition from a PlayStation 2 HDD (Sony APA scheme).</summary>
public sealed record ApaPartition
{
    /// <summary>1-based position while walking the chain from the head.</summary>
    public required int Index { get; init; }
    /// <summary>The ASCII partition id/name (e.g. "__mbr", "__system", a game code).</summary>
    public required string Id { get; init; }
    /// <summary>The raw APA partition-type word.</summary>
    public required uint Type { get; init; }
    /// <summary>A best-effort name for <see cref="Type"/>.</summary>
    public required string TypeName { get; init; }
    /// <summary>Starting sector (512-byte units) of this partition header.</summary>
    public required long StartSector { get; init; }
    /// <summary>Length in 512-byte sectors.</summary>
    public required long SectorCount { get; init; }
    public long StartByte => StartSector * 512;
    public long SizeBytes => SectorCount * 512;
}

/// <summary>A parsed PS2 APA disk: its partitions, walked from the head.</summary>
public sealed record ApaDisk
{
    public required IReadOnlyList<ApaPartition> Partitions { get; init; }
}

/// <summary>
/// Best-effort reader for the PlayStation 2 HDD "APA" (Aligned Partition
/// Allocation) scheme. Clean-room, from the public format description — and it is
/// deliberately conservative: the fields pinned confidently below are the header
/// magic, the doubly-linked next/prev sector pointers, the ASCII id/name, the
/// partition type word and the sector count; other header fields (passwords,
/// per-partition directory, etc.) are not decoded.
///
/// Layout used (offsets within the 0x400-byte header; all little-endian):
///   0x000 u32 checksum
///   0x004 u32 magic       — bytes 'A','P','A',0x00
///   0x008 u32 next        — sector of the next partition header
///   0x00C u32 prev        — sector of the previous partition header
///   0x010 char[32] id     — ASCII partition name
///   0x040 u32 start       — partition start sector
///   0x044 u32 nsector     — partition size in sectors
///   0x048 u32 type        — partition type
///
/// The partitions form a circular doubly-linked list; enumeration starts at the
/// head (sector 0) and follows <c>next</c>, with a visited-set guard so a corrupt
/// or self-referential chain cannot loop forever. Because this cannot be validated
/// here against a real PS2 HDD, treat the surfaced ids/types as best-effort.
/// </summary>
public static class ApaReader
{
    public const int SectorSize = 512;
    public const int HeaderSize = 0x400;
    /// <summary>The APA header magic: 'A','P','A',0x00 read as a little-endian u32.</summary>
    public const uint Magic = 0x00415041;

    private const int OffMagic = 0x004;
    private const int OffNext = 0x008;
    private const int OffId = 0x010;
    private const int OffStart = 0x040;
    private const int OffNsector = 0x044;
    private const int OffType = 0x048;
    private const int IdMax = 32;

    /// <summary>True if the first sector carries the APA header magic.</summary>
    public static bool IsApa(byte[] firstSector)
    {
        ArgumentNullException.ThrowIfNull(firstSector);
        if (firstSector.Length < HeaderSize) return false;
        return BinaryPrimitives.ReadUInt32LittleEndian(firstSector.AsSpan(OffMagic)) == Magic;
    }

    public static bool IsApa(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length < HeaderSize) return false;
        var head = new byte[HeaderSize];
        stream.Seek(0, SeekOrigin.Begin);
        if (ReadFull(stream, head, HeaderSize) < HeaderSize) return false;
        return IsApa(head);
    }

    public static ApaDisk Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var ms = new MemoryStream(data, writable: false);
        return Read(ms);
    }

    public static ApaDisk Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("Reading a partition table needs a seekable stream.", nameof(stream));
        long len = stream.Length;
        if (len < HeaderSize)
            throw new PartitionFormatException("Image is too small to hold an APA header.");

        var header = new byte[HeaderSize];
        stream.Seek(0, SeekOrigin.Begin);
        if (ReadFull(stream, header, HeaderSize) < HeaderSize)
            throw new PartitionFormatException("Truncated APA header.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffMagic)) != Magic)
            throw new PartitionFormatException("No APA header magic in the first sector.");

        var partitions = new List<ApaPartition>();
        var visited = new HashSet<long>();
        long sector = 0;
        int index = 1;

        for (int guard = 0; guard < 8192; guard++)
        {
            if (!visited.Add(sector)) break;
            long offset = sector * SectorSize;
            if (offset < 0 || offset + HeaderSize > len) break;

            stream.Seek(offset, SeekOrigin.Begin);
            if (ReadFull(stream, header, HeaderSize) < HeaderSize) break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffMagic)) != Magic) break;

            long next = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffNext));
            long nsector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffNsector));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffType));
            string id = ReadAsciiId(header, OffId, IdMax);

            partitions.Add(new ApaPartition
            {
                Index = index++,
                Id = id,
                Type = type,
                TypeName = ApaTypeName(type),
                StartSector = sector,
                SectorCount = nsector,
            });

            if (next == 0) break;   // back to the head / end of chain
            sector = next;
        }

        return new ApaDisk { Partitions = partitions };
    }

    private static string ReadAsciiId(byte[] data, int at, int max)
    {
        int end = at;
        int limit = Math.Min(at + max, data.Length);
        while (end < limit && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, at, end - at).TrimEnd();
    }

    // Best-effort: the id/name is the reliable descriptor; the numeric type is
    // surfaced as-is with only the two commonest values named.
    private static string ApaTypeName(uint type) => type switch
    {
        0x0000 => "Free",
        0x0001 => "System (PFS)",
        _ => $"Type 0x{type:X4}",
    };

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
