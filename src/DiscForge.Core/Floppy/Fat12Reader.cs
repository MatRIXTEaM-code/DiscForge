// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Floppy;

/// <summary>One entry (file or directory) in a FAT12 volume.</summary>
public sealed record Fat12Entry
{
    /// <summary>Bare 8.3 name, e.g. "FILE.TXT".</summary>
    public required string Name { get; init; }
    /// <summary>Full path from the root, e.g. "/SUBDIR/FILE.TXT".</summary>
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required uint Size { get; init; }
    public required int FirstCluster { get; init; }
}

/// <summary>A parsed FAT12 floppy image.</summary>
public sealed record Fat12Disk
{
    public required string VolumeLabel { get; init; }
    /// <summary>Every file and directory, flattened, with full paths.</summary>
    public required IReadOnlyList<Fat12Entry> Entries { get; init; }
}

/// <summary>
/// Reads a DOS FAT12 floppy image (.img) — directory tree and file extraction.
/// Clean-room, from the public FAT specification.
///
/// The BPB in the boot sector gives the geometry (bytes/sector, sectors/cluster,
/// reserved sectors, FAT count and size, root-directory entry count). The volume
/// is laid out as: reserved sectors, then the FAT(s), then a fixed-size root
/// directory, then the data area, whose first cluster is numbered 2. Directory
/// entries are 32 bytes each (8.3 name, attributes, first-cluster-low, size); a
/// directory is itself a cluster chain of such entries, so the whole tree can be
/// walked and files identified by full path. Each 12-bit FAT entry chains a
/// cluster to the next; values ≥ 0xFF8 terminate the chain. Long-file-name (0x0F)
/// entries are skipped; only 8.3 names are surfaced.
/// </summary>
public static class Fat12Reader
{
    private const int DirEntrySize = 32;

    /// <summary>True if the buffer looks like a FAT12 volume (BPB sanity + 0x55AA).</summary>
    public static bool IsFat12(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 512) return false;
        if (data[0x1FE] != 0x55 || data[0x1FF] != 0xAA) return false;

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0B));
        if (bytesPerSector != 512) return false;

        int sectorsPerCluster = data[0x0D];
        if (sectorsPerCluster == 0 || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0 || sectorsPerCluster > 128)
            return false;

        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0E));
        if (reserved == 0) return false;

        int numFats = data[0x10];
        if (numFats is < 1 or > 2) return false;

        int rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x11));
        if (rootEntries == 0) return false;

        int sectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x16));
        if (sectorsPerFat == 0) return false;

        byte media = data[0x15];
        if (media < 0xF0) return false;

        return true;
    }

    public static Fat12Disk Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Read(ms.ToArray());
    }

    public static Fat12Disk Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsFat12(data))
            throw new InvalidDataException("Not a FAT12 image (BPB sanity or 0x55AA boot signature failed).");

        var g = Geometry.From(data);

        string volumeLabel = "";
        var entries = new List<Fat12Entry>();

        // Root directory is a fixed run of sectors (not a cluster chain).
        var rootEntries = ReadDirEntries(data, g, RootDirBytesOffset(g), g.RootDirSectors * g.BytesPerSector, isRoot: true);
        foreach (var raw in rootEntries)
        {
            if (raw.IsVolumeLabel) { if (volumeLabel.Length == 0) volumeLabel = raw.RawName; continue; }
            AddEntry(data, g, entries, raw, "");
        }

        return new Fat12Disk { VolumeLabel = volumeLabel, Entries = entries };
    }

    private static void AddEntry(byte[] data, Geometry g, List<Fat12Entry> entries, RawEntry raw, string parentPath)
    {
        string path = parentPath + "/" + raw.Name;
        entries.Add(new Fat12Entry
        {
            Name = raw.Name,
            Path = path,
            IsDirectory = raw.IsDirectory,
            Size = raw.Size,
            FirstCluster = raw.FirstCluster,
        });

        if (raw.IsDirectory)
        {
            // A subdirectory is a cluster chain of 32-byte entries.
            byte[] dirBytes = ReadClusterChain(data, g, raw.FirstCluster, long.MaxValue);
            foreach (var child in ReadDirEntries(data, g, 0, dirBytes.Length, isRoot: false, buffer: dirBytes))
            {
                if (child.IsVolumeLabel) continue;
                if (child.Name is "." or "..") continue;
                AddEntry(data, g, entries, child, path);
            }
        }
    }

    /// <summary>Extract a file's bytes by following its cluster chain, truncated to its recorded size.</summary>
    public static byte[] ExtractFile(byte[] data, Fat12Entry entry)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory)
            throw new InvalidOperationException($"'{entry.Path}' is a directory, not a file.");
        var g = Geometry.From(data);
        return ReadClusterChain(data, g, entry.FirstCluster, entry.Size);
    }

    // --- internals -----------------------------------------------------------

    private sealed record RawEntry
    {
        public required string Name { get; init; }
        public required string RawName { get; init; }
        public required bool IsDirectory { get; init; }
        public required bool IsVolumeLabel { get; init; }
        public required uint Size { get; init; }
        public required int FirstCluster { get; init; }
    }

    private static List<RawEntry> ReadDirEntries(byte[] data, Geometry g, long baseOffset, long length, bool isRoot, byte[]? buffer = null)
    {
        var src = buffer ?? data;
        long start = buffer is null ? baseOffset : 0;
        var result = new List<RawEntry>();
        long count = length / DirEntrySize;

        for (long i = 0; i < count; i++)
        {
            long e = start + i * DirEntrySize;
            if (e + DirEntrySize > src.Length) break;
            byte first = src[(int)e];
            if (first == 0x00) break;        // end of directory
            if (first == 0xE5) continue;     // deleted

            byte attr = src[(int)(e + 0x0B)];
            if (attr == 0x0F) continue;      // long-file-name entry — skip

            bool isVolume = (attr & 0x08) != 0;
            bool isDir = (attr & 0x10) != 0;

            string rawName = Encoding.ASCII.GetString(src, (int)e, 11);
            string name;
            if (isVolume)
            {
                name = rawName.TrimEnd();
            }
            else
            {
                // KANJI lead byte 0x05 stands in for a real 0xE5 first character.
                byte n0 = src[(int)e];
                string namePart = (n0 == 0x05 ? "å" + rawName.Substring(1, 7) : rawName.Substring(0, 8)).TrimEnd();
                string extPart = rawName.Substring(8, 3).TrimEnd();
                name = extPart.Length > 0 ? namePart + "." + extPart : namePart;
            }

            int firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan((int)(e + 0x1A)));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan((int)(e + 0x1C)));

            result.Add(new RawEntry
            {
                Name = name,
                RawName = rawName.TrimEnd(),
                IsDirectory = isDir,
                IsVolumeLabel = isVolume,
                Size = isDir ? 0 : size,
                FirstCluster = firstCluster,
            });
        }
        return result;
    }

    private static byte[] ReadClusterChain(byte[] data, Geometry g, int firstCluster, long limit)
    {
        var outBytes = new List<byte>();
        int cluster = firstCluster;
        int clusterBytes = g.SectorsPerCluster * g.BytesPerSector;
        var seen = new HashSet<int>();

        while (cluster >= 2 && cluster < 0xFF8)
        {
            if (!seen.Add(cluster))
                throw new InvalidDataException("FAT12 cluster chain loops back on itself.");
            long off = ClusterByteOffset(g, cluster);
            if (off < 0 || off + clusterBytes > data.Length)
                throw new InvalidDataException($"FAT12 cluster {cluster} runs past the end of the image.");
            for (int i = 0; i < clusterBytes; i++)
            {
                outBytes.Add(data[(int)off + i]);
                if (outBytes.Count >= limit) return outBytes.ToArray();
            }
            cluster = NextCluster(data, g, cluster);
        }

        if (limit != long.MaxValue && outBytes.Count > limit)
            return outBytes.GetRange(0, (int)limit).ToArray();
        return outBytes.ToArray();
    }

    private static int NextCluster(byte[] data, Geometry g, int cluster)
    {
        int fatBase = g.FatStartSector * g.BytesPerSector;
        int k = fatBase + cluster * 3 / 2;
        if (k + 1 >= data.Length) return 0xFFF;
        int value = (cluster & 1) == 0
            ? (data[k] | (data[k + 1] << 8)) & 0x0FFF
            : (data[k] >> 4) | (data[k + 1] << 4);
        return value;
    }

    private static long RootDirBytesOffset(Geometry g) =>
        (long)g.RootDirStartSector * g.BytesPerSector;

    private static long ClusterByteOffset(Geometry g, int cluster) =>
        ((long)g.DataStartSector + (long)(cluster - 2) * g.SectorsPerCluster) * g.BytesPerSector;

    private sealed record Geometry
    {
        public required int BytesPerSector { get; init; }
        public required int SectorsPerCluster { get; init; }
        public required int ReservedSectors { get; init; }
        public required int NumFats { get; init; }
        public required int RootEntries { get; init; }
        public required int SectorsPerFat { get; init; }
        public required long TotalSectors { get; init; }

        public int FatStartSector => ReservedSectors;
        public int RootDirStartSector => ReservedSectors + NumFats * SectorsPerFat;
        public int RootDirSectors => (RootEntries * DirEntrySize + BytesPerSector - 1) / BytesPerSector;
        public int DataStartSector => RootDirStartSector + RootDirSectors;

        public static Geometry From(byte[] d)
        {
            int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x0B));
            long total16 = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x13));
            long total32 = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x20));
            return new Geometry
            {
                BytesPerSector = bytesPerSector,
                SectorsPerCluster = d[0x0D],
                ReservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x0E)),
                NumFats = d[0x10],
                RootEntries = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x11)),
                SectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x16)),
                TotalSectors = total16 != 0 ? total16 : total32,
            };
        }
    }
}
