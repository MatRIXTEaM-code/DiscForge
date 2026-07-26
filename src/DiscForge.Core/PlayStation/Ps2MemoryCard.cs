// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.PlayStation;

public sealed class Ps2McFormatException(string message) : Exception(message);

/// <summary>One file or directory on a PS2 memory card.</summary>
public sealed record Ps2McEntry
{
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required long Size { get; init; }
    /// <summary>First data cluster (logical, relative to alloc_offset).</summary>
    public required uint FirstCluster { get; init; }
}

public sealed record Ps2McVolume
{
    public required bool HasEcc { get; init; }
    public required int ClustersPerCard { get; init; }
    public required IReadOnlyList<Ps2McEntry> Entries { get; init; }

    public IEnumerable<Ps2McEntry> Files => Entries.Where(e => !e.IsDirectory);
    public IEnumerable<Ps2McEntry> Saves => Entries.Where(e => e.IsDirectory && !e.Path.TrimStart('/').Contains('/'));
}

/// <summary>
/// Reads a Sony PlayStation 2 memory-card image (a ".ps2" file) — the "MyMC" job:
/// list the saves and extract their files. The PS2 memory card is a plain FAT-like
/// filesystem; this reads a person's own card dump, decrypts nothing and defeats
/// nothing. It complements DiscForge's Dreamcast VMU support with the PS2 save
/// side.
///
/// Clean-room, from the public PS2 memory-card filesystem description:
///   Pages of 512 data bytes (+ 16 spare/ECC bytes on "with-ECC" dumps), 2 pages
///   per 1024-byte cluster. Superblock at page 0: magic "Sony PS2 Memory Card
///   Format ", page_len (0x28), pages_per_cluster (0x2A), clusters_per_card (0x30),
///   alloc_offset (0x34), rootdir_cluster (0x3C), and ifc_list[32] (0x50). The FAT
///   is double-indirect: ifc_list → indirect clusters → FAT clusters, each a
///   256-entry table of 32-bit words (MSB = allocated, low 31 bits = next cluster,
///   0xFFFFFFFF = last). Cluster numbers are relative to alloc_offset. A 512-byte
///   directory entry holds mode (0x00), length (0x04), first cluster (0x10) and a
///   32-byte name (0x40); a save is a directory of files.
/// </summary>
public static class Ps2MemoryCard
{
    private const int PageDataLen = 512;
    private const int ClusterSize = 1024;
    private const int EntriesPerFatCluster = ClusterSize / 4;   // 256
    private const int DirEntrySize = 512;
    private const uint FatLast = 0xFFFFFFFF;
    private const int MaxDepth = 8;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("Sony PS2 Memory Card Format ");

    // Directory-entry mode bits.
    private const ushort ModeFile = 0x0010;
    private const ushort ModeDirectory = 0x0020;
    private const ushort ModeExists = 0x8000;

    public static bool IsPs2MemoryCard(byte[] data) =>
        data.Length >= Magic.Length && data.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    public static Ps2McVolume Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsPs2MemoryCard(data))
            throw new Ps2McFormatException("Missing the \"Sony PS2 Memory Card Format\" signature.");

        int pageLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x28));
        int pagesPerCluster = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x2A));
        uint clustersPerCard = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x30));
        uint allocOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x34));
        uint rootDir = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x3C));
        if (pageLen != PageDataLen || pagesPerCluster < 1)
            throw new Ps2McFormatException($"Unexpected page geometry (page_len {pageLen}, ppc {pagesPerCluster}).");

        var ifc = new uint[32];
        for (int i = 0; i < 32; i++) ifc[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x50 + i * 4));

        // Detect the physical page size: raw (512) or with-ECC (528), from the file
        // length against the card geometry.
        long totalPages = (long)clustersPerCard * pagesPerCluster;
        int physPage = data.LongLength >= totalPages * 528 ? 528 : PageDataLen;

        var ctx = new Context(data, physPage, pagesPerCluster, (int)allocOffset, ifc);

        var entries = new List<Ps2McEntry>();
        WalkDirectory(ctx, rootDir, "", entries, 0);

        return new Ps2McVolume
        {
            HasEcc = physPage == 528,
            ClustersPerCard = (int)clustersPerCard,
            Entries = entries,
        };
    }

    /// <summary>Extract a file's bytes by following its FAT chain.</summary>
    public static byte[] Extract(byte[] data, Ps2McVolume volume, Ps2McEntry entry)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory) throw new ArgumentException($"'{entry.Path}' is a directory.", nameof(entry));

        // Re-derive the context from the superblock (cheap, keeps the API simple).
        int pagesPerCluster = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x2A));
        uint clustersPerCard = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x30));
        uint allocOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x34));
        var ifc = new uint[32];
        for (int i = 0; i < 32; i++) ifc[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x50 + i * 4));
        long totalPages = (long)clustersPerCard * pagesPerCluster;
        int physPage = data.LongLength >= totalPages * 528 ? 528 : PageDataLen;
        var ctx = new Context(data, physPage, pagesPerCluster, (int)allocOffset, ifc);

        using var ms = new MemoryStream((int)entry.Size);
        long remaining = entry.Size;
        uint cluster = entry.FirstCluster;
        int guard = 0;
        while (remaining > 0 && cluster != FatLast)
        {
            var c = ctx.ReadDataCluster(cluster);
            int take = (int)Math.Min(ClusterSize, remaining);
            ms.Write(c, 0, take);
            remaining -= take;
            cluster = ctx.NextCluster(cluster);
            if (++guard > ctx.ClusterCountGuard)
                throw new Ps2McFormatException($"'{entry.Path}' has a cyclic FAT chain.");
        }
        return ms.ToArray();
    }

    // ---- traversal ----------------------------------------------------------

    private static void WalkDirectory(Context ctx, uint dirCluster, string prefix, List<Ps2McEntry> acc, int depth)
    {
        if (depth > MaxDepth) return;

        // The directory's first entry (".") holds the entry count in its length.
        var first = ctx.ReadDirEntry(dirCluster, 0);
        if (first is null) return;
        int count = (int)first.Value.Length;
        if (count < 0 || count > 100_000) return;

        for (int i = 0; i < count; i++)
        {
            var e = ctx.ReadDirEntry(dirCluster, i);
            if (e is null) break;
            var entry = e.Value;
            if ((entry.Mode & ModeExists) == 0) continue;
            if (entry.Name is "." or "..") continue;

            string path = prefix + "/" + entry.Name;
            bool isDir = (entry.Mode & ModeDirectory) != 0;

            acc.Add(new Ps2McEntry
            {
                Path = path,
                IsDirectory = isDir,
                Size = isDir ? 0 : entry.Length,
                FirstCluster = entry.Cluster,
            });

            if (isDir)
                WalkDirectory(ctx, entry.Cluster, path, acc, depth + 1);
        }
    }

    private readonly record struct DirEntry(ushort Mode, uint Length, uint Cluster, string Name);

    // ---- the physical/FAT context ------------------------------------------

    private sealed class Context
    {
        private readonly byte[] _data;
        private readonly int _physPage;
        private readonly int _ppc;
        private readonly int _allocOffset;
        private readonly uint[] _ifc;
        public readonly int ClusterCountGuard;

        public Context(byte[] data, int physPage, int ppc, int allocOffset, uint[] ifc)
        {
            _data = data; _physPage = physPage; _ppc = ppc; _allocOffset = allocOffset; _ifc = ifc;
            ClusterCountGuard = data.Length / ClusterSize + 16;
        }

        // Read a physical cluster (by absolute cluster index) as 1024 contiguous
        // data bytes, skipping the per-page spare area on ECC dumps.
        private byte[] ReadPhysicalCluster(uint physCluster)
        {
            var buf = new byte[ClusterSize];
            for (int p = 0; p < _ppc && p < 2; p++)
            {
                long pageIndex = (long)physCluster * _ppc + p;
                long at = pageIndex * _physPage;
                if (at + PageDataLen > _data.LongLength)
                    throw new Ps2McFormatException($"Cluster {physCluster} lies past the end of the image.");
                Array.Copy(_data, at, buf, p * PageDataLen, PageDataLen);
            }
            return buf;
        }

        // A data cluster addressed by its logical number (relative to alloc_offset).
        public byte[] ReadDataCluster(uint logical) => ReadPhysicalCluster((uint)(logical + _allocOffset));

        // The FAT successor of a logical cluster, via the double-indirect FAT.
        public uint NextCluster(uint logical)
        {
            int fatOffset = (int)(logical % EntriesPerFatCluster);
            int indirectIndex = (int)(logical / EntriesPerFatCluster);
            int ifcSlot = indirectIndex / EntriesPerFatCluster;
            int indirectOff = indirectIndex % EntriesPerFatCluster;

            if (ifcSlot >= _ifc.Length) return FatLast;
            uint indirectClusterPhys = _ifc[ifcSlot];
            var indirect = ReadPhysicalCluster(indirectClusterPhys);
            uint fatClusterPhys = BinaryPrimitives.ReadUInt32LittleEndian(indirect.AsSpan(indirectOff * 4));
            var fat = ReadPhysicalCluster(fatClusterPhys);
            uint entry = BinaryPrimitives.ReadUInt32LittleEndian(fat.AsSpan(fatOffset * 4));

            if ((entry & 0x80000000u) == 0) return FatLast;   // not allocated
            uint next = entry & 0x7FFFFFFFu;
            return next == 0x7FFFFFFFu ? FatLast : next;
        }

        // Read the i-th 512-byte directory entry of a directory whose data begins at
        // logical cluster dirCluster (two entries per cluster; walk the FAT chain).
        public DirEntry? ReadDirEntry(uint dirCluster, int index)
        {
            int clusterHops = index / 2;
            uint cluster = dirCluster;
            for (int h = 0; h < clusterHops; h++)
            {
                cluster = NextCluster(cluster);
                if (cluster == FatLast) return null;
            }
            var c = ReadDataCluster(cluster);
            int at = (index % 2) * DirEntrySize;

            ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(at + 0x00));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(at + 0x04));
            uint cl = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(at + 0x10));
            int nameLen = 0;
            while (nameLen < 32 && c[at + 0x40 + nameLen] != 0) nameLen++;
            string name = Encoding.ASCII.GetString(c, at + 0x40, nameLen);

            return new DirEntry(mode, length, cl, name);
        }
    }
}
