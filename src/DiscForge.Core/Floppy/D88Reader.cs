// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Floppy;

/// <summary>One sector's identity/metadata within a D88 track.</summary>
public sealed record D88Sector
{
    public required int Cylinder { get; init; }
    public required int Head { get; init; }
    public required int Record { get; init; }
    /// <summary>Size code N; the sector holds 128 &lt;&lt; N bytes.</summary>
    public required int SizeCode { get; init; }
    public int SizeBytes => 128 << SizeCode;
    public required int DataSize { get; init; }
    public bool Deleted { get; init; }
    public bool SingleDensity { get; init; }
    public int FdcStatus { get; init; }
}

/// <summary>A parsed PC-98 (and PC-88) D88 floppy image — one logical disk.</summary>
public sealed record D88Disk
{
    public required string Name { get; init; }
    public required bool WriteProtected { get; init; }
    public required int DiskTypeCode { get; init; }
    public string DiskTypeName => DiskTypeCode switch
    {
        0x00 => "2D", 0x10 => "2DD", 0x20 => "2HD", 0x30 => "1D", 0x40 => "1DD", _ => $"0x{DiskTypeCode:X2}",
    };
    public required long DiskSize { get; init; }
    /// <summary>Populated tracks (index in the 164-entry table) → their sectors.</summary>
    public required IReadOnlyList<(int Track, IReadOnlyList<D88Sector> Sectors)> Tracks { get; init; }
    /// <summary>True if more logical disks follow this one in the file (multi-disk D88).</summary>
    public bool MoreDisksFollow { get; init; }

    public int TrackCount => Tracks.Count;
    public int SectorCount => Tracks.Sum(t => t.Sectors.Count);
}

/// <summary>
/// Reads a D88 floppy disk image — the standard preservation container for the Japanese
/// PC-98 (and PC-88) platform, which DiscForge had no coverage of. This parses the 688-byte
/// header (name, write-protect, media type, the 164-entry track offset table) and walks each
/// present track's sector headers to report the disk's geometry. Clean-room, from the public
/// D88 layout; multi-disk D88 files are detected. No protection concerns.
/// </summary>
public static class D88Reader
{
    public const int HeaderSize = 0x2B0;
    public const int MaxTracks = 164;

    public static bool IsD88(ReadOnlySpan<byte> head)
    {
        // D88 has no magic; validate structurally: a sane media-type byte and a disk-size field
        // that fits the file, with the first track offset at/after the header.
        if (head.Length < HeaderSize) return false;
        int type = head[0x1B];
        if (type is not (0x00 or 0x10 or 0x20 or 0x30 or 0x40)) return false;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(0x1C, 4));
        if (size < HeaderSize) return false;
        uint firstTrack = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(0x20, 4));
        return firstTrack == 0 || firstTrack >= HeaderSize;
    }

    public static D88Disk Read(Stream s)
    {
        s.Position = 0;
        var all = new byte[s.Length];
        s.ReadExactly(all, 0, all.Length);
        return Parse(all);
    }

    public static D88Disk Parse(byte[] all) => Parse(all, 0);

    public static D88Disk Parse(byte[] all, long baseOffset)
    {
        if (all.Length - baseOffset < HeaderSize)
            throw new InvalidDataException("File is too small to hold a D88 header.");
        var h = all.AsSpan((int)baseOffset);
        if (!IsD88(h))
            throw new InvalidDataException("Not a D88 image (header is not structurally valid).");

        string name = Encoding.ASCII.GetString(h[..17].ToArray()).Split('\0')[0].TrimEnd();
        bool wp = h[0x1A] == 0x10;
        int type = h[0x1B];
        long diskSize = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(0x1C, 4));

        var tracks = new List<(int, IReadOnlyList<D88Sector>)>();
        for (int t = 0; t < MaxTracks; t++)
        {
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(0x20 + t * 4, 4));
            if (off == 0) continue;
            long p = baseOffset + off;
            var sectors = ReadTrack(all, p, diskSize + baseOffset);
            if (sectors.Count > 0) tracks.Add((t, sectors));
        }

        bool more = baseOffset + diskSize + HeaderSize <= all.Length && diskSize > 0;
        return new D88Disk
        {
            Name = name,
            WriteProtected = wp,
            DiskTypeCode = type,
            DiskSize = diskSize,
            Tracks = tracks,
            MoreDisksFollow = more,
        };
    }

    private static List<D88Sector> ReadTrack(byte[] all, long start, long limit)
    {
        var sectors = new List<D88Sector>();
        long p = start;
        int declared = -1;
        while (p + 16 <= all.Length && (limit <= 0 || p < limit))
        {
            var s = all.AsSpan((int)p);
            int c = s[0], head = s[1], r = s[2], n = s[3];
            int count = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(4, 2));
            bool single = s[6] == 0x40;
            bool deleted = s[7] == 0x10;
            int status = s[8];
            int dataSize = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(0x0E, 2));
            if (n > 7 || count == 0 || count > 256) break;      // not a sane sector header

            sectors.Add(new D88Sector
            {
                Cylinder = c, Head = head, Record = r, SizeCode = n,
                DataSize = dataSize, Deleted = deleted, SingleDensity = single, FdcStatus = status,
            });

            if (declared < 0) declared = count;
            p += 16 + dataSize;
            if (sectors.Count >= declared) break;               // this track's sectors are all read
        }
        return sectors;
    }
}
