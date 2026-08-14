// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Hfs;

/// <summary>One run of contiguous allocation blocks belonging to a fork.</summary>
public readonly record struct HfsExtent(uint StartBlock, uint BlockCount);

/// <summary>One catalogued item on a classic Mac HFS volume.</summary>
public sealed record HfsEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }
    public required uint Cnid { get; init; }
    public required uint ParentCnid { get; init; }
    /// <summary>Data-fork logical size (0 for folders).</summary>
    public required long DataSize { get; init; }
    /// <summary>Resource-fork logical size — Mac files carry a second fork ordinary tools ignore.</summary>
    public required long ResourceSize { get; init; }
    /// <summary>First allocation block of the data fork (for locating/extracting).</summary>
    public uint DataStartBlock { get; init; }
    /// <summary>First allocation block of the resource fork.</summary>
    public uint ResourceStartBlock { get; init; }
    /// <summary>The data fork's stored extent record (up to three contiguous runs).</summary>
    public IReadOnlyList<HfsExtent> DataExtents { get; init; } = Array.Empty<HfsExtent>();
    /// <summary>The resource fork's stored extent record (up to three contiguous runs).</summary>
    public IReadOnlyList<HfsExtent> ResourceExtents { get; init; } = Array.Empty<HfsExtent>();
}

/// <summary>A parsed HFS volume: its name and its full file/folder tree.</summary>
public sealed record HfsVolume
{
    public required string VolumeName { get; init; }
    public required IReadOnlyList<HfsEntry> Entries { get; init; }
    /// <summary>Allocation-block size in bytes (volume geometry, needed to locate any fork).</summary>
    public int AllocBlockSize { get; init; }
    /// <summary>First allocation block's position, in 512-byte sectors from the image start.</summary>
    public int AllocBlockStartSector { get; init; }

    public IEnumerable<HfsEntry> Files => Entries.Where(e => !e.IsDirectory);
    public IEnumerable<HfsEntry> Directories => Entries.Where(e => e.IsDirectory);
    /// <summary>Total data-fork bytes (resource forks counted separately).</summary>
    public long TotalDataBytes => Files.Sum(f => f.DataSize);
    public long TotalResourceBytes => Files.Sum(f => f.ResourceSize);
}

public sealed class HfsFormatException(string message) : Exception(message);

/// <summary>
/// Reader for classic Apple HFS — the filesystem on the Mac side of the hybrid CDs so much retro Mac
/// software shipped on. Where the rest of the toolkit walks ISO 9660 and UDF, this walks the HFS catalog
/// B-tree: it reads the Master Directory Block for the volume geometry and catalog location, follows the
/// catalog's leaf-node chain, and enumerates every folder and file with its full Mac path, its data-fork
/// size and — the part ISO/Joliet extraction silently drops — its <b>resource fork</b> size. That turns a
/// Mac hybrid disc from "the half your OS won't mount" into a fully-enumerable tree, so orphan-data
/// analysis and preservation can cover it too. Reading and enumeration only.
/// </summary>
public static class HfsReader
{
    private const int MdbOffset = 0x400;         // Master Directory Block: sector 2
    private const ushort HfsSignature = 0x4244;  // "BD"
    private const uint RootFolderCnid = 2;
    private const int MaxNodes = 100_000;        // loop guard

    public static bool IsHfs(byte[] image)
        => image.Length >= MdbOffset + 2 &&
           BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(MdbOffset)) == HfsSignature;

    public static HfsVolume Read(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!IsHfs(image))
            throw new HfsFormatException("No HFS Master Directory Block (\"BD\") at offset 0x400 — not an HFS volume.");

        int alBlkSize = (int)U32(image, MdbOffset + 20);
        int alBlkStart = U16(image, MdbOffset + 28);           // in 512-byte sectors
        if (alBlkSize <= 0 || alBlkSize % 512 != 0)
            throw new HfsFormatException($"Implausible allocation block size {alBlkSize}.");

        string volumeName = PascalString(image, MdbOffset + 36, 27);
        long catSize = U32(image, MdbOffset + 146);
        var catExtents = ReadExtents(image, MdbOffset + 150);

        byte[] catalog = ReadForkExtents(image, catExtents, catSize, alBlkStart, alBlkSize);
        if (catalog.Length < 14 + 22)
            throw new HfsFormatException("Catalog file is too small to hold a B-tree header.");

        // B-tree header node (node 0): descriptor is 14 bytes, header record follows.
        int nodeSize = U16(catalog, 14 + 18);
        uint firstLeaf = U32(catalog, 14 + 10);
        if (nodeSize < 512 || nodeSize % 512 != 0 || catalog.Length % nodeSize != 0)
            throw new HfsFormatException($"Implausible B-tree node size {nodeSize}.");

        var folders = new Dictionary<uint, (string Name, uint Parent)>();
        var raw = new List<HfsEntry>();

        uint node = firstLeaf;
        var visited = new HashSet<uint>();
        int guard = 0;
        while (node != 0 && visited.Add(node) && guard++ < MaxNodes)
        {
            long baseOff = (long)node * nodeSize;
            if (baseOff < 0 || baseOff + nodeSize > catalog.Length) break;

            sbyte type = (sbyte)catalog[baseOff + 8];
            int numRecords = U16(catalog, (int)baseOff + 10);
            uint fLink = U32(catalog, (int)baseOff);

            if (type == -1)   // leaf node (ndLeafNode)
            {
                for (int r = 0; r < numRecords; r++)
                {
                    int recOff = U16(catalog, (int)(baseOff + nodeSize - 2 * (r + 1)));
                    int recEnd = U16(catalog, (int)(baseOff + nodeSize - 2 * (r + 2)));
                    long recStart = baseOff + recOff;
                    if (recOff <= 0 || recEnd <= recOff || baseOff + recEnd > catalog.Length) continue;

                    int klen = catalog[recStart];
                    uint parent = U32(catalog, (int)recStart + 2);
                    int nameLen = catalog[recStart + 6];
                    string name = MacString(catalog, (int)recStart + 7, nameLen);

                    int used = 1 + klen;
                    if ((used & 1) != 0) used++;              // pad key to an even boundary
                    long data = recStart + used;
                    if (data + 2 > baseOff + recEnd) continue;

                    sbyte cdrType = (sbyte)catalog[data];
                    switch (cdrType)
                    {
                        case 1:   // folder record (cdrDirRec)
                        {
                            uint dirId = U32(catalog, (int)data + 6);
                            folders[dirId] = (name, parent);
                            if (dirId != RootFolderCnid)   // the root folder is the volume itself, not a subdir
                                raw.Add(new HfsEntry
                                {
                                    Path = "", Name = name, IsDirectory = true,
                                    Cnid = dirId, ParentCnid = parent, DataSize = 0, ResourceSize = 0,
                                });
                            break;
                        }
                        case 2:   // file record (cdrFilRec)
                        {
                            uint fileId = U32(catalog, (int)data + 20);
                            uint dataStart = (uint)U16(catalog, (int)data + 24);
                            long dataLen = U32(catalog, (int)data + 26);
                            uint rsrcStart = (uint)U16(catalog, (int)data + 34);
                            long rsrcLen = U32(catalog, (int)data + 36);
                            // Fork extent records: data-fork (filExtRec) at data + 74, resource-fork
                            // (filRExtRec) at data + 86 — each three (start,count) runs.
                            var dataExtents = ReadCatalogExtentRecord(catalog, (int)data + 74, (int)(baseOff + recEnd));
                            var rsrcExtents = ReadCatalogExtentRecord(catalog, (int)data + 86, (int)(baseOff + recEnd));
                            raw.Add(new HfsEntry
                            {
                                Path = "", Name = name, IsDirectory = false,
                                Cnid = fileId, ParentCnid = parent,
                                DataSize = dataLen, ResourceSize = rsrcLen, DataStartBlock = dataStart,
                                ResourceStartBlock = rsrcStart,
                                DataExtents = dataExtents, ResourceExtents = rsrcExtents,
                            });
                            break;
                        }
                        // 3 = folder thread, 4 = file thread — used for path lookup, not enumeration.
                    }
                }
            }

            node = fLink;
        }

        // Reconstruct full Mac paths (":"-free, using "/" for readability) from the parent chain.
        var entries = raw
            .Select(e => e with { Path = BuildPath(folders, e.ParentCnid) + "/" + e.Name })
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new HfsVolume
        {
            VolumeName = volumeName,
            Entries = entries,
            AllocBlockSize = alBlkSize,
            AllocBlockStartSector = alBlkStart,
        };
    }

    /// <summary>
    /// Read a file's <b>resource fork</b> bytes from the image. Returns an empty array when the file has no
    /// resource fork. Throws <see cref="HfsFormatException"/> if the fork is fragmented beyond the three
    /// extents recorded in the catalog (an extents-overflow file) or lies outside the image.
    /// </summary>
    public static byte[] ReadResourceFork(byte[] image, HfsVolume volume, HfsEntry entry)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.ResourceSize <= 0) return Array.Empty<byte>();
        var extents = entry.ResourceExtents.Select(e => ((int)e.StartBlock, (int)e.BlockCount)).ToArray();
        return ReadForkExtents(image, extents, entry.ResourceSize,
                               volume.AllocBlockStartSector, volume.AllocBlockSize);
    }

    /// <summary>
    /// Read a file's <b>data fork</b> bytes from the image. Returns an empty array when the file has no
    /// data fork. Throws <see cref="HfsFormatException"/> if the fork is fragmented beyond the three
    /// extents recorded in the catalog (an extents-overflow file) or lies outside the image.
    /// </summary>
    public static byte[] ReadDataFork(byte[] image, HfsVolume volume, HfsEntry entry)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.DataSize <= 0) return Array.Empty<byte>();
        var extents = entry.DataExtents.Select(e => ((int)e.StartBlock, (int)e.BlockCount)).ToArray();
        return ReadForkExtents(image, extents, entry.DataSize,
                               volume.AllocBlockStartSector, volume.AllocBlockSize);
    }

    private static IReadOnlyList<HfsExtent> ReadCatalogExtentRecord(byte[] cat, int off, int recordLimit)
    {
        int limit = Math.Min(recordLimit, cat.Length);
        if (off < 0 || off + 12 > limit) return Array.Empty<HfsExtent>();
        var list = new List<HfsExtent>(3);
        for (int i = 0; i < 3; i++)
        {
            uint start = (uint)U16(cat, off + i * 4);
            uint count = (uint)U16(cat, off + i * 4 + 2);
            if (count > 0) list.Add(new HfsExtent(start, count));
        }
        return list;
    }

    // ---- internals ----------------------------------------------------------

    private static string BuildPath(Dictionary<uint, (string Name, uint Parent)> folders, uint cnid)
    {
        var parts = new List<string>();
        var seen = new HashSet<uint>();
        while (cnid != RootFolderCnid && cnid != 0 && seen.Add(cnid) && folders.TryGetValue(cnid, out var f))
        {
            parts.Add(f.Name);
            cnid = f.Parent;
        }
        parts.Reverse();
        return parts.Count == 0 ? "" : "/" + string.Join("/", parts);
    }

    private static (int Start, int Count)[] ReadExtents(byte[] image, int off)
    {
        var e = new (int, int)[3];
        for (int i = 0; i < 3; i++)
            e[i] = (U16(image, off + i * 4), U16(image, off + i * 4 + 2));
        return e;
    }

    private static byte[] ReadForkExtents(byte[] image, (int Start, int Count)[] extents,
                                          long forkSize, int alBlkStart, int alBlkSize)
    {
        var outBuf = new byte[forkSize];
        long written = 0;
        foreach (var (start, count) in extents)
        {
            if (count == 0 || written >= forkSize) continue;
            long srcOff = (long)alBlkStart * 512 + (long)start * alBlkSize;
            long len = Math.Min((long)count * alBlkSize, forkSize - written);
            if (srcOff < 0 || srcOff + len > image.Length)
                throw new HfsFormatException("A catalog extent lies outside the image.");
            Array.Copy(image, srcOff, outBuf, written, len);
            written += len;
        }
        if (written < forkSize)
            throw new HfsFormatException("Catalog spans an extents-overflow file (fragmented) — not yet supported.");
        return outBuf;
    }

    private static string PascalString(byte[] b, int off, int max)
    {
        int len = Math.Min(b[off], max);
        return MacString(b, off + 1, len);
    }

    // MacRoman-ish: ASCII verbatim; high bytes rendered leniently so a name is never lost.
    private static string MacString(byte[] b, int off, int len)
    {
        if (off < 0 || len < 0 || off + len > b.Length) return "";
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte c = b[off + i];
            sb.Append(c < 0x80 ? (char)c : '?');
        }
        return sb.ToString();
    }

    private static int U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
}
