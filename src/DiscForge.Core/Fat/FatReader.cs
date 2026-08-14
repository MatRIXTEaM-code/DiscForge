// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Fat;

/// <summary>Which FAT variant a volume is, decided by its cluster count (the Microsoft rule).</summary>
public enum FatType { Fat12, Fat16, Fat32 }

/// <summary>One file or directory in a FAT volume.</summary>
public sealed record FatEntry
{
    /// <summary>The name (long name when present, else 8.3).</summary>
    public required string Name { get; init; }
    /// <summary>Full path from the root with '/' separators.</summary>
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required long Size { get; init; }
    public required uint FirstCluster { get; init; }
}

/// <summary>A parsed FAT volume: its label, type and full tree.</summary>
public sealed record FatVolume
{
    public required FatType Type { get; init; }
    public required string VolumeLabel { get; init; }
    public required IReadOnlyList<FatEntry> Entries { get; init; }

    public IEnumerable<FatEntry> Files => Entries.Where(e => !e.IsDirectory);
    public IEnumerable<FatEntry> Directories => Entries.Where(e => e.IsDirectory);
    public long TotalBytes => Files.Sum(f => f.Size);
}

public sealed class FatFormatException(string message) : Exception(message);

/// <summary>
/// General reader for FAT <b>16</b> and <b>32</b> volumes — the larger cousins of the floppy-only
/// <c>Fat12Reader</c>. FAT is the filesystem inside El Torito hard-disk-emulation boot images, on the FAT
/// partition of a hybrid disc, and on the UMD/memory media a great deal of retro software shipped on, yet a
/// plain ISO reader sees straight past it. This walks the BPB geometry, decides the FAT type from the cluster
/// count, follows cluster chains (12-, 16- or 32-bit entries), reassembles VFAT long file names, and recurses
/// the directory tree — surfacing every file with its full path and letting any one be extracted. It also
/// reads FAT12 for completeness, so one entry point covers all three. Reading and enumeration only.
/// </summary>
public static class FatReader
{
    private const int DirEntrySize = 32;
    private const int MaxEntries = 2_000_000;   // loop guard across the whole tree

    /// <summary>True if the buffer's BPB and 0x55AA signature look like a FAT volume.</summary>
    public static bool IsFat(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 512) return false;
        if (data[0x1FE] != 0x55 || data[0x1FF] != 0xAA) return false;
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0B));
        if (bps is not (512 or 1024 or 2048 or 4096)) return false;
        int spc = data[0x0D];
        if (spc == 0 || (spc & (spc - 1)) != 0 || spc > 128) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0E)) == 0) return false;   // reserved sectors
        int numFats = data[0x10];
        if (numFats is < 1 or > 2) return false;
        if (data[0x15] < 0xF0) return false;   // media descriptor
        return true;
    }

    public static FatVolume Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsFat(data))
            throw new FatFormatException("Not a FAT volume (BPB sanity or 0x55AA boot signature failed).");

        var g = Geometry.From(data);
        var entries = new List<FatEntry>();
        string label = "";

        IReadOnlyList<RawEntry> rootRaw = g.Type == FatType.Fat32
            ? ReadDir(data, g, ReadClusterChain(data, g, g.RootCluster, long.MaxValue))
            : ReadDir(data, g, ReadRegion(data, g.RootDirByteOffset, g.RootDirSectors * g.BytesPerSector));

        foreach (var r in rootRaw)
        {
            if (r.IsVolumeLabel) { if (label.Length == 0) label = r.ShortName; continue; }
            AddEntry(data, g, entries, r, "", 0);
        }

        return new FatVolume { Type = g.Type, VolumeLabel = label, Entries = entries };
    }

    /// <summary>Extract a file's bytes by following its cluster chain, truncated to its recorded size.</summary>
    public static byte[] ExtractFile(byte[] data, FatEntry entry)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory)
            throw new FatFormatException($"'{entry.Path}' is a directory, not a file.");
        var g = Geometry.From(data);
        return ReadClusterChain(data, g, entry.FirstCluster, entry.Size);
    }

    // --- tree walk -------------------------------------------------------------

    private static void AddEntry(byte[] data, Geometry g, List<FatEntry> entries, RawEntry raw, string parent, int depth)
    {
        if (entries.Count >= MaxEntries || depth > 64) return;
        string name = raw.Name;
        string path = parent + "/" + name;
        entries.Add(new FatEntry
        {
            Name = name, Path = path, IsDirectory = raw.IsDirectory,
            Size = raw.Size, FirstCluster = raw.FirstCluster,
        });

        if (raw.IsDirectory && raw.FirstCluster >= 2)
        {
            var dir = ReadClusterChain(data, g, raw.FirstCluster, long.MaxValue);
            foreach (var child in ReadDir(data, g, dir))
            {
                if (child.IsVolumeLabel || child.Name is "." or "..") continue;
                AddEntry(data, g, entries, child, path, depth + 1);
            }
        }
    }

    // --- directory parsing (with VFAT long names) ------------------------------

    private static List<RawEntry> ReadDir(byte[] src, Geometry g, byte[] dir)
    {
        var result = new List<RawEntry>();
        var lfn = new List<(int Seq, string Part)>();
        long count = dir.Length / DirEntrySize;

        for (long i = 0; i < count; i++)
        {
            int e = (int)(i * DirEntrySize);
            byte first = dir[e];
            if (first == 0x00) break;          // end of directory
            if (first == 0xE5) { lfn.Clear(); continue; }  // deleted

            byte attr = dir[e + 0x0B];
            if ((attr & 0x0F) == 0x0F)         // long-file-name fragment
            {
                int seq = dir[e] & 0x1F;
                lfn.Add((seq, LfnChars(dir, e)));
                continue;
            }

            bool isVolume = (attr & 0x08) != 0 && (attr & 0x10) == 0;
            bool isDir = (attr & 0x10) != 0;

            string shortName = ShortName(dir, e, isVolume);
            string longName = "";
            if (lfn.Count > 0)
            {
                lfn.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                longName = string.Concat(lfn.Select(p => p.Part)).TrimEnd('￿', '\0', ' ');
            }
            lfn.Clear();

            uint hi = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(e + 0x14));
            uint lo = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(e + 0x1A));
            uint firstCluster = (hi << 16) | lo;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(e + 0x1C));

            result.Add(new RawEntry
            {
                Name = longName.Length > 0 ? longName : shortName,
                ShortName = shortName,
                IsDirectory = isDir,
                IsVolumeLabel = isVolume,
                Size = isDir ? 0 : size,
                FirstCluster = firstCluster,
            });
        }
        return result;
    }

    private static string LfnChars(byte[] d, int e)
    {
        // 13 UTF-16LE code units at offsets 1(×5), 14(×6), 28(×2).
        Span<char> chars = stackalloc char[13];
        int n = 0;
        foreach (int off in new[] { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 })
            chars[n++] = (char)BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(e + off));
        return new string(chars);
    }

    private static string ShortName(byte[] d, int e, bool isVolume)
    {
        string raw = Encoding.Latin1.GetString(d, e, 11);
        if (isVolume) return raw.TrimEnd();
        byte n0 = d[e];
        string namePart = (n0 == 0x05 ? (char)0xE5 + raw.Substring(1, 7) : raw.Substring(0, 8)).TrimEnd();
        string extPart = raw.Substring(8, 3).TrimEnd();
        return extPart.Length > 0 ? namePart + "." + extPart : namePart;
    }

    // --- cluster / FAT plumbing ------------------------------------------------

    private static byte[] ReadClusterChain(byte[] data, Geometry g, uint firstCluster, long limit)
    {
        using var outBytes = new MemoryStream();
        uint cluster = firstCluster;
        int clusterBytes = g.SectorsPerCluster * g.BytesPerSector;
        var seen = new HashSet<uint>();

        while (cluster >= 2 && cluster < g.EndOfChain)
        {
            if (!seen.Add(cluster))
                throw new FatFormatException("FAT cluster chain loops back on itself.");
            long off = g.ClusterByteOffset(cluster);
            if (off < 0 || off + clusterBytes > data.Length)
                throw new FatFormatException($"FAT cluster {cluster} runs past the end of the image.");
            int take = (int)Math.Min(clusterBytes, limit == long.MaxValue ? clusterBytes : limit - outBytes.Length);
            outBytes.Write(data, (int)off, take);
            if (limit != long.MaxValue && outBytes.Length >= limit) break;
            cluster = NextCluster(data, g, cluster);
        }
        return outBytes.ToArray();
    }

    private static uint NextCluster(byte[] data, Geometry g, uint cluster)
    {
        long fatBase = (long)g.FatStartSector * g.BytesPerSector;
        switch (g.Type)
        {
            case FatType.Fat12:
            {
                long k = fatBase + cluster * 3 / 2;
                if (k + 1 >= data.Length) return g.EndOfChain;
                int v = (cluster & 1) == 0
                    ? (data[(int)k] | (data[(int)k + 1] << 8)) & 0x0FFF
                    : (data[(int)k] >> 4) | (data[(int)k + 1] << 4);
                return (uint)v;
            }
            case FatType.Fat16:
            {
                long k = fatBase + cluster * 2;
                if (k + 2 > data.Length) return g.EndOfChain;
                return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)k));
            }
            default:
            {
                long k = fatBase + cluster * 4;
                if (k + 4 > data.Length) return g.EndOfChain;
                return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)k)) & 0x0FFFFFFF;
            }
        }
    }

    private static byte[] ReadRegion(byte[] data, long offset, long length)
    {
        if (offset < 0 || offset >= data.Length) return Array.Empty<byte>();
        length = Math.Min(length, data.Length - offset);
        var buf = new byte[length];
        Array.Copy(data, offset, buf, 0, length);
        return buf;
    }

    private sealed record RawEntry
    {
        public required string Name { get; init; }
        public required string ShortName { get; init; }
        public required bool IsDirectory { get; init; }
        public required bool IsVolumeLabel { get; init; }
        public required long Size { get; init; }
        public required uint FirstCluster { get; init; }
    }

    private sealed record Geometry
    {
        public required int BytesPerSector { get; init; }
        public required int SectorsPerCluster { get; init; }
        public required int ReservedSectors { get; init; }
        public required int NumFats { get; init; }
        public required int RootEntries { get; init; }
        public required long FatSize { get; init; }
        public required long TotalSectors { get; init; }
        public required uint RootCluster { get; init; }
        public required FatType Type { get; init; }

        public int FatStartSector => ReservedSectors;
        public int RootDirSectors => (RootEntries * DirEntrySize + BytesPerSector - 1) / BytesPerSector;
        public long RootDirStartSector => ReservedSectors + (long)NumFats * FatSize;
        public long DataStartSector => RootDirStartSector + RootDirSectors;
        public long RootDirByteOffset => RootDirStartSector * BytesPerSector;
        public uint EndOfChain => Type switch { FatType.Fat12 => 0xFF8, FatType.Fat16 => 0xFFF8, _ => 0x0FFFFFF8 };

        public long ClusterByteOffset(uint cluster) =>
            (DataStartSector + (long)(cluster - 2) * SectorsPerCluster) * BytesPerSector;

        public static Geometry From(byte[] d)
        {
            int bps = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x0B));
            int spc = d[0x0D];
            int reserved = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x0E));
            int numFats = d[0x10];
            int rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x11));
            long fatSz16 = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x16));
            long fatSz32 = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x24));
            long total16 = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x13));
            long total32 = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x20));
            uint rootClus = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x2C));

            long fatSize = fatSz16 != 0 ? fatSz16 : fatSz32;
            long total = total16 != 0 ? total16 : total32;
            if (bps == 0 || spc == 0 || fatSize == 0)
                throw new FatFormatException("Degenerate BPB (zero bytes/sector, sectors/cluster or FAT size).");

            int rootDirSectors = (rootEntries * DirEntrySize + bps - 1) / bps;
            long firstDataSector = reserved + (long)numFats * fatSize + rootDirSectors;
            long dataSectors = total - firstDataSector;
            long clusters = dataSectors / spc;
            FatType type = clusters < 4085 ? FatType.Fat12 : clusters < 65525 ? FatType.Fat16 : FatType.Fat32;

            return new Geometry
            {
                BytesPerSector = bps, SectorsPerCluster = spc, ReservedSectors = reserved,
                NumFats = numFats, RootEntries = rootEntries, FatSize = fatSize,
                TotalSectors = total, RootCluster = rootClus, Type = type,
            };
        }
    }
}
