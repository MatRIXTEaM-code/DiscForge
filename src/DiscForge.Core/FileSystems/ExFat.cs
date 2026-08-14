// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;

namespace DiscForge.Core.FileSystems;

/// <summary>
/// Read-only exFAT filesystem reader — list directories and extract files from an exFAT volume image.
/// exFAT is the modern removable-media filesystem (SDXC cards, large USB media) that DiscForge's FAT
/// reader didn't cover and that Aaru does. It parses the boot sector, follows the FAT (or a
/// contiguous NoFatChain run), and decodes the exFAT directory entry SET — a File entry (0x85), a
/// Stream Extension (0xC0), and one or more File Name entries (0xC1) — into names, sizes and cluster
/// chains. Pure and validated by a synthetic-volume round-trip test. It reads user files; it decrypts
/// and defeats nothing.
/// </summary>
public static class ExFat
{
    public sealed record VolumeInfo(int BytesPerSector, int SectorsPerCluster, uint FatOffset,
                                    uint ClusterHeapOffset, uint ClusterCount, uint RootDirCluster, string? Label)
    {
        public int ClusterBytes => BytesPerSector * SectorsPerCluster;
    }

    public sealed record DirEntry(string Name, long Size, bool IsDirectory, uint FirstCluster, bool NoFatChain);

    private const uint EndOfChain = 0xFFFFFFFF;

    // ---- volume ------------------------------------------------------------

    public static VolumeInfo ReadInfo(Stream s)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Position = 0;
        var boot = new byte[512];
        ReadExact(s, boot);
        if (Encoding.ASCII.GetString(boot, 3, 8) != "EXFAT   ")
            throw new InvalidDataException("Not an exFAT volume (missing the 'EXFAT   ' signature).");

        int bps = 1 << boot[108];
        int spc = 1 << boot[109];
        var v = new VolumeInfo(bps, spc,
            FatOffset: U32(boot, 80), ClusterHeapOffset: U32(boot, 88),
            ClusterCount: U32(boot, 92), RootDirCluster: U32(boot, 96), Label: null);
        return v with { Label = FindLabel(s, v) };
    }

    /// <summary>List a directory by its first cluster (use <see cref="VolumeInfo.RootDirCluster"/> for root).
    /// Pass the directory's own <c>NoFatChain</c> flag so a contiguous multi-cluster subdirectory is walked
    /// contiguously rather than truncated at its first cluster; the root directory is always FAT-chained.
    /// For a NoFatChain directory, <paramref name="dirDataLength"/> (the directory entry's DataLength) bounds
    /// the contiguous walk — without it there is no chain terminator, and the read would run to the end of
    /// the cluster heap.</summary>
    public static IReadOnlyList<DirEntry> List(Stream s, VolumeInfo v, uint dirCluster,
                                               bool dirNoFatChain = false, long dirDataLength = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(v);
        var data = ReadChain(s, v, dirCluster, dirNoFatChain, dirNoFatChain ? dirDataLength : long.MaxValue);
        var entries = new List<DirEntry>();

        for (int o = 0; o + 32 <= data.Length;)
        {
            byte type = data[o];
            if (type == 0x00) break;                       // end of directory
            if (type != 0x85) { o += 32; continue; }       // only File entries start a set

            int secondary = data[o + 1];
            ushort attr = U16(data, o + 4);
            bool isDir = (attr & 0x10) != 0;

            int so = o + 32;                               // Stream Extension must follow
            if (so + 32 > data.Length || data[so] != 0xC0) { o += 32 * (secondary + 1); continue; }
            byte streamFlags = data[so + 1];
            int nameLen = data[so + 3];
            bool noFatChain = (streamFlags & 0x02) != 0;
            ulong dataLength = U64(data, so + 24);
            uint firstCluster = U32(data, so + 20);

            var name = new StringBuilder(nameLen);
            int no = so + 32, remaining = nameLen;
            while (remaining > 0 && no + 32 <= data.Length && data[no] == 0xC1)
            {
                int take = Math.Min(15, remaining);
                for (int c = 0; c < take; c++) name.Append((char)U16(data, no + 2 + c * 2));
                remaining -= take;
                no += 32;
            }

            entries.Add(new DirEntry(name.ToString(), (long)dataLength, isDir, firstCluster, noFatChain));
            o += 32 * (secondary + 1);
        }
        return entries;
    }

    /// <summary>Resolve a slash- or backslash-separated path to its directory entry, or null.</summary>
    public static DirEntry? Resolve(Stream s, VolumeInfo v, string path)
    {
        var parts = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        uint cluster = v.RootDirCluster;
        bool noFatChain = false;                        // root directory is FAT-chained
        long dirLength = long.MaxValue;
        DirEntry? found = null;
        for (int i = 0; i < parts.Length; i++)
        {
            found = List(s, v, cluster, noFatChain, dirLength).FirstOrDefault(e => string.Equals(e.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            if (found is null) return null;
            if (i < parts.Length - 1)
            {
                if (!found.IsDirectory) return null;
                cluster = found.FirstCluster;
                noFatChain = found.NoFatChain;
                dirLength = found.Size;                 // bounds a contiguous (NoFatChain) walk
            }
        }
        return found;
    }

    /// <summary>Write a file's data to <paramref name="output"/>. Returns bytes written.</summary>
    public static long ExtractFile(Stream s, VolumeInfo v, DirEntry file, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (file.IsDirectory) throw new InvalidOperationException($"'{file.Name}' is a directory.");
        var data = ReadChain(s, v, file.FirstCluster, file.NoFatChain, file.Size);
        long len = Math.Min(file.Size, data.Length);
        output.Write(data, 0, (int)len);
        return len;
    }

    // ---- cluster / FAT walking ---------------------------------------------

    private static byte[] ReadChain(Stream s, VolumeInfo v, uint startCluster, bool noFatChain, long maxBytes = long.MaxValue)
    {
        using var ms = new MemoryStream();
        var buf = new byte[v.ClusterBytes];
        uint cluster = startCluster;
        var seen = new HashSet<uint>();

        while (cluster >= 2 && cluster <= v.ClusterCount + 1 && cluster != EndOfChain && ms.Length < maxBytes)
        {
            if (!seen.Add(cluster)) break;                  // cycle guard
            long sector = v.ClusterHeapOffset + (long)(cluster - 2) * v.SectorsPerCluster;
            s.Position = sector * v.BytesPerSector;
            ReadExact(s, buf);
            ms.Write(buf, 0, buf.Length);

            cluster = noFatChain ? cluster + 1 : ReadFatEntry(s, v, cluster);
        }
        return ms.ToArray();
    }

    private static uint ReadFatEntry(Stream s, VolumeInfo v, uint cluster)
    {
        s.Position = (long)v.FatOffset * v.BytesPerSector + (long)cluster * 4;
        var b = new byte[4];
        ReadExact(s, b);
        return U32(b, 0);
    }

    private static string? FindLabel(Stream s, VolumeInfo v)
    {
        var data = ReadChain(s, v, v.RootDirCluster, noFatChain: false);
        for (int o = 0; o + 32 <= data.Length; o += 32)
        {
            if (data[o] == 0x00) break;
            if (data[o] == 0x83)                            // Volume Label entry
            {
                int chars = data[o + 1];
                var sb = new StringBuilder(chars);
                for (int c = 0; c < chars && c < 11; c++) sb.Append((char)U16(data, o + 2 + c * 2));
                return sb.ToString();
            }
        }
        return null;
    }

    // ---- little-endian helpers ---------------------------------------------

    private static void ReadExact(Stream s, byte[] b)
    {
        int off = 0;
        while (off < b.Length)
        {
            int r = s.Read(b, off, b.Length - off);
            if (r <= 0) throw new EndOfStreamException("Unexpected end of exFAT volume.");
            off += r;
        }
    }

    private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static ulong U64(byte[] b, int o) { ulong v = 0; for (int i = 7; i >= 0; i--) v = (v << 8) | b[o + i]; return v; }
}
