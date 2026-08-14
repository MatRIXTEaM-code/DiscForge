// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Xbox;

/// <summary>One file or directory in an XDVDFS volume.</summary>
public sealed record XdvdfsEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required long Size { get; init; }
    /// <summary>Start sector of the entry's data, relative to the volume base.</summary>
    public required uint StartSector { get; init; }
    public required byte Attributes { get; init; }
}

/// <summary>What was found on an Original Xbox game disc's XDVDFS filesystem.</summary>
public sealed record XdvdfsVolume
{
    /// <summary>Sector offset of the game partition within the image (0 for a
    /// trimmed XISO; non-zero for a full XGD dump).</summary>
    public required long BaseSector { get; init; }
    public required uint RootSector { get; init; }
    public required uint RootSize { get; init; }
    public required IReadOnlyList<XdvdfsEntry> Entries { get; init; }

    public IEnumerable<XdvdfsEntry> Files => Entries.Where(e => !e.IsDirectory);
    public IEnumerable<XdvdfsEntry> Directories => Entries.Where(e => e.IsDirectory);
    public long TotalBytes => Files.Sum(f => f.Size);
}

public sealed class XdvdfsFormatException(string message) : Exception(message);

/// <summary>
/// Reads XDVDFS — the filesystem on an Original Xbox game disc, and the format an
/// "XISO" carries. It is a plain, unencrypted filesystem: a volume descriptor at
/// a fixed sector, then a directory laid out as a binary tree of entries, each
/// naming a file or subdirectory and where its data sits. Reading it is exactly
/// like reading ISO 9660 or UDF — DiscForge lists and extracts the files a
/// person's own backup already holds. It decrypts nothing (XDVDFS carries no
/// encryption) and defeats no protection; the Xbox's disc *security* lives
/// elsewhere and is neither read nor touched here.
///
/// Clean-room, from the public format description:
///
///   Volume descriptor (sector 32 of the game partition):
///     0x000  20  magic "MICROSOFT*XBOX*MEDIA"
///     0x014   4  root directory table start sector (LE)
///     0x018   4  root directory table size in bytes (LE)
///     0x01C   8  creation timestamp (FILETIME)
///     0x7EC  20  magic "MICROSOFT*XBOX*MEDIA" (trailer)
///
///   Directory entry (within a directory table, offsets in 4-byte units):
///     0x00  2  left sub-tree offset  (0xFFFF = none)
///     0x02  2  right sub-tree offset (0xFFFF = none)
///     0x04  4  start sector of this entry's data (LE)
///     0x08  4  size in bytes (LE)
///     0x0C  1  attributes (0x10 = directory)
///     0x0D  1  filename length N
///     0x0E  N  filename (ASCII)
///            → padded to a 4-byte boundary
///
/// Sector addresses are relative to the game partition's base. A trimmed XISO
/// has base 0; a full XGD1 dump places the partition at sector 0x30600. The
/// reader auto-detects the base by finding the magic, or takes it explicitly.
/// </summary>
public static class XdvdfsReader
{
    public const int SectorSize = 2048;
    private const int VolumeDescriptorSector = 32;
    private const int MaxDepth = 32;
    private const ushort NoSubtree = 0xFFFF;
    private const byte AttrDirectory = 0x10;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

    /// <summary>Documented game-partition base sectors, in the order Xbox tools
    /// use them: a trimmed XISO (0), a full XGD1 original-Xbox redump (0x30600),
    /// and the Xbox 360 XGD2 (0x1FB20) and XGD3 (0x4100) offsets — the extract-xiso
    /// constants 0x18300000 / 0x0FD90000 / 0x02080000 bytes, divided by the
    /// 2048-byte sector. A wrong base cannot be chosen by accident: the volume
    /// descriptor's signature AND trailer must both match at (base + 32). The
    /// XGD2/XGD3 bases follow the documented offsets; a real 360 dump would confirm
    /// them, but the signature check guards against a false positive regardless.</summary>
    private static readonly long[] CandidateBases = { 0, 0x30600, 0x1FB20, 0x4100 };

    /// <summary>True if this stream carries an XDVDFS volume at any known base.</summary>
    public static bool IsXdvdfs(Stream image)
    {
        ArgumentNullException.ThrowIfNull(image);
        try { return FindBase(image) is not null; }
        catch { return false; }
    }

    /// <summary>Read the filesystem. Auto-detects the game-partition base unless
    /// <paramref name="baseSector"/> is given.</summary>
    public static XdvdfsVolume Read(Stream image, long? baseSector = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanSeek)
            throw new ArgumentException("Reading XDVDFS requires a seekable stream.", nameof(image));

        long baseLba = baseSector ?? FindBase(image)
            ?? throw new XdvdfsFormatException(
                "No XDVDFS volume descriptor found — the \"MICROSOFT*XBOX*MEDIA\" signature is not at " +
                "sector 32 of any known base, nor within the leading scan window. This does not look " +
                "like an Xbox game image (or its partition begins past the scan window — pass an " +
                "explicit baseSector if you know the offset).");

        var vd = ReadSector(image, baseLba + VolumeDescriptorSector);
        if (!StartsWith(vd, 0, Magic))
            throw new XdvdfsFormatException("The volume descriptor signature is missing at the chosen base.");

        uint rootSector = BinaryPrimitives.ReadUInt32LittleEndian(vd.AsSpan(0x14, 4));
        uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(vd.AsSpan(0x18, 4));

        var entries = new List<XdvdfsEntry>();
        if (rootSize > 0)
            WalkDirectory(image, baseLba, rootSector, rootSize, "", entries, 0);

        return new XdvdfsVolume
        {
            BaseSector = baseLba,
            RootSector = rootSector,
            RootSize = rootSize,
            Entries = entries,
        };
    }

    /// <summary>Copy a file's bytes out of the volume.</summary>
    public static void ExtractFile(Stream image, XdvdfsVolume volume, XdvdfsEntry entry, Stream output)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        if (entry.IsDirectory)
            throw new ArgumentException($"'{entry.Path}' is a directory.", nameof(entry));

        long at = (volume.BaseSector + entry.StartSector) * SectorSize;
        image.Seek(at, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        long remaining = entry.Size;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int n = image.Read(buffer, 0, want);
            if (n <= 0)
                throw new EndOfStreamException(
                    $"'{entry.Path}' ends past the end of the image — it claims {entry.Size:N0} bytes " +
                    $"at sector {entry.StartSector}, but the image ran out {remaining:N0} bytes early.");
            output.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    // ---- base detection -----------------------------------------------------

    /// <summary>How far (in sectors) to scan for the volume descriptor when none of the documented
    /// bases match — a dump with an unusual leading offset. Bounded so a huge non-XDVDFS image isn't
    /// swept end to end; a base past this still reads with an explicit <c>baseSector</c>.</summary>
    private const long ScanLimitSectors = 0x8000;   // 64 MiB of leading offset

    private static long? FindBase(Stream image)
    {
        // The documented bases first (cheap, and the common case).
        foreach (long b in CandidateBases)
            if (HasDescriptor(image, b))
                return b;

        // Fallback: a raw dump whose partition begins at an undocumented offset. Scan sector-aligned
        // descriptor positions for the signature AND trailer (both must match, so a stray copy of the
        // string in file data can't be mistaken for the descriptor), and derive the base from it.
        long total = image.Length / SectorSize;
        long limit = Math.Min(total, VolumeDescriptorSector + ScanLimitSectors);
        for (long ds = VolumeDescriptorSector; ds < limit; ds++)
        {
            long b = ds - VolumeDescriptorSector;
            if (CandidateBases.Contains(b)) continue;                 // already tried above
            if (MagicAt(image, ds * SectorSize) && MagicAt(image, ds * SectorSize + 0x7EC))
                return b;
        }
        return null;
    }

    private static bool HasDescriptor(Stream image, long baseSector)
    {
        long at = (baseSector + VolumeDescriptorSector) * SectorSize;
        if (baseSector < 0 || at + SectorSize > image.Length) return false;
        var vd = ReadSector(image, baseSector + VolumeDescriptorSector);
        // Both the leading signature and the trailer must match — the trailer guards against a
        // stray copy of the string in file data.
        return StartsWith(vd, 0, Magic) && StartsWith(vd, 0x7EC, Magic);
    }

    private static bool MagicAt(Stream image, long byteOffset)
    {
        if (byteOffset < 0 || byteOffset + Magic.Length > image.Length) return false;
        Span<byte> buf = stackalloc byte[20];
        image.Seek(byteOffset, SeekOrigin.Begin);
        image.ReadExactly(buf);
        for (int i = 0; i < Magic.Length; i++) if (buf[i] != Magic[i]) return false;
        return true;
    }

    // ---- the directory tree -------------------------------------------------

    private static void WalkDirectory(Stream image, long baseLba, uint tableSector, uint tableSize,
                                      string prefix, List<XdvdfsEntry> acc, int depth)
    {
        if (depth > MaxDepth)
            throw new XdvdfsFormatException(
                $"Directory nesting exceeds {MaxDepth} levels at '{prefix}' — possible loop or corruption.");

        var table = ReadRange(image, (baseLba + tableSector) * SectorSize, (int)tableSize);

        // Traverse the binary tree from the root node at offset 0. A visited-set
        // makes the walk robust to either "no child" convention (0xFFFF, or 0
        // pointing back at the root) and to malformed cyclic pointers.
        var visited = new HashSet<int>();
        var pending = new List<(uint TableSector, uint TableSize, string Path)>();

        void Visit(ushort nodeOffset)
        {
            if (nodeOffset == NoSubtree) return;
            int at = nodeOffset * 4;
            if (at < 0 || at + 14 > table.Length) return;
            if (!visited.Add(at)) return;

            ushort left = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(at, 2));
            ushort right = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(at + 2, 2));
            uint startSector = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at + 4, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at + 8, 4));
            byte attributes = table[at + 12];
            int nameLen = table[at + 13];

            Visit(left);

            if (nameLen > 0 && at + 14 + nameLen <= table.Length)
            {
                string name = Encoding.ASCII.GetString(table, at + 14, nameLen);
                bool isDir = (attributes & AttrDirectory) != 0;
                string path = prefix + "/" + name;

                acc.Add(new XdvdfsEntry
                {
                    Name = name,
                    Path = path,
                    IsDirectory = isDir,
                    Size = size,
                    StartSector = startSector,
                    Attributes = attributes,
                });

                if (isDir && size > 0)
                    pending.Add((startSector, size, path));
            }

            Visit(right);
        }

        Visit(0);

        // Recurse into subdirectories after finishing this table, so the tree
        // walk above is not interleaved with fresh reads.
        foreach (var (sec, sz, path) in pending)
            WalkDirectory(image, baseLba, sec, sz, path, acc, depth + 1);
    }

    // ---- raw access ---------------------------------------------------------

    private static byte[] ReadSector(Stream image, long sector) =>
        ReadRange(image, sector * SectorSize, SectorSize);

    private static byte[] ReadRange(Stream image, long offset, int length)
    {
        if (length < 0) length = 0;
        if (offset < 0 || offset >= image.Length)
            throw new XdvdfsFormatException(
                $"The image is truncated: a structure at offset {offset:N0} lies past the end " +
                $"({image.Length:N0} bytes).");

        length = (int)Math.Min(length, image.Length - offset);
        var buf = new byte[length];
        image.Seek(offset, SeekOrigin.Begin);
        image.ReadExactly(buf, 0, length);
        return buf;
    }

    private static bool StartsWith(byte[] data, int at, byte[] needle)
    {
        if (at < 0 || at + needle.Length > data.Length) return false;
        for (int i = 0; i < needle.Length; i++)
            if (data[at + i] != needle[i]) return false;
        return true;
    }
}
