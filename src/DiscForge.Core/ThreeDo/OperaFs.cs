// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.ThreeDo;

/// <summary>One file or directory in a 3DO Opera volume.</summary>
public sealed record OperaEntry(
    string Path, string Name, bool IsDirectory,
    uint ByteCount, uint BlockCount, uint FirstBlock, string TypeTag);

/// <summary>A parsed 3DO Opera volume.</summary>
public sealed record OperaVolume
{
    public required string Label { get; init; }
    public required uint BlockSize { get; init; }
    public required uint BlockCount { get; init; }
    public required IReadOnlyList<OperaEntry> Entries { get; init; }

    public IEnumerable<OperaEntry> Files => Entries.Where(e => !e.IsDirectory);
    public IEnumerable<OperaEntry> Directories => Entries.Where(e => e.IsDirectory);
    public long TotalBytes => Files.Sum(f => (long)f.ByteCount);

    public string Summary()
        => $"3DO Opera volume \"{Label}\": {Files.Count()} file(s), {Directories.Count()} dir(s), " +
           $"{TotalBytes:N0} bytes, {BlockCount:N0} × {BlockSize}-byte blocks.";
}

public sealed class OperaFormatException(string message) : Exception(message);

/// <summary>
/// opera-fs — the reader for the 3DO's Opera file system, the console's own CD layout in place of ISO
/// 9660. A volume label sits in block 0 — record type 1, the "ZZZZZ" sync, the volume name, block size and
/// count, and the avatar (block-address) copies of the root directory. Each directory is a run of blocks,
/// every block headed by a small record (links and the offset of the first free byte) followed by fixed
/// entries: flags carrying the file/directory type, byte and block counts, a 32-byte name, and the avatar
/// list of the entry's own blocks. All fields are big-endian, as the console is. This validates the label,
/// walks the root directory and recurses into subdirectories, and returns the full file tree. Read-only;
/// it parses and reports.
/// </summary>
public static class OperaFs
{
    private const byte RecordType = 1;
    private static readonly byte[] Sync = { 0x5A, 0x5A, 0x5A, 0x5A, 0x5A };   // "ZZZZZ"
    private const int DirHeaderSize = 20;
    private const int EntryFixedSize = 72;
    private const uint NoBlock = 0xFFFFFFFF;
    private const int MaxEntries = 100_000;

    /// <summary>True if the first block carries the Opera volume label.</summary>
    public static bool IsVolume(ReadOnlySpan<byte> image)
    {
        if (image.Length < 132) return false;
        if (image[0] != RecordType) return false;
        for (int i = 0; i < 5; i++) if (image[1 + i] != Sync[i]) return false;
        return true;
    }

    /// <summary>Read the file tree from a cooked (2048-byte/block) Opera image.</summary>
    public static OperaVolume Read(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!IsVolume(image))
            throw new OperaFormatException("Not a 3DO Opera volume (missing the record-type/ZZZZZ label).");

        string label = Ascii(image.AsSpan(40, 32));
        uint blockSize = U32(image, 76);
        uint blockCount = U32(image, 80);
        if (blockSize is < 128 or > 8192) throw new OperaFormatException($"Implausible block size {blockSize}.");

        uint rootDirBlocks = U32(image, 88);
        uint lastRootCopy = U32(image, 96);
        if (lastRootCopy > 64) lastRootCopy = 0;                       // guard a corrupt count
        uint rootFirstBlock = U32(image, 100);                         // avatar copy 0

        var entries = new List<OperaEntry>();
        var visited = new HashSet<uint>();
        WalkDirectory(image, blockSize, rootFirstBlock, rootDirBlocks, "", entries, visited, depth: 0);

        return new OperaVolume
        {
            Label = label, BlockSize = blockSize, BlockCount = blockCount, Entries = entries,
        };
    }

    // ---- directory walk -----------------------------------------------------

    private static void WalkDirectory(byte[] image, uint blockSize, uint firstBlock, uint blockSpan,
        string parentPath, List<OperaEntry> entries, HashSet<uint> visited, int depth)
    {
        if (depth > 32 || entries.Count >= MaxEntries) return;
        if (blockSpan == 0) blockSpan = 1;

        for (uint bi = 0; bi < blockSpan; bi++)
        {
            uint block = firstBlock + bi;
            if (!visited.Add(block)) continue;
            long baseOff = (long)block * blockSize;
            if (baseOff + DirHeaderSize > image.Length) return;

            uint firstFree = U32(image, baseOff + 12);
            uint firstEntry = U32(image, baseOff + 16);
            if (firstEntry < DirHeaderSize) firstEntry = DirHeaderSize;
            long limit = baseOff + Math.Min(blockSize, firstFree == 0 ? blockSize : firstFree);

            long o = baseOff + firstEntry;
            while (o + EntryFixedSize <= limit && entries.Count < MaxEntries)
            {
                uint flags = U32(image, o);
                if (flags == NoBlock) break;                           // no more entries in this block

                string tag = Ascii(image.AsSpan((int)(o + 8), 4));
                uint byteCount = U32(image, o + 16);
                uint blkCount = U32(image, o + 20);
                string name = Ascii(image.AsSpan((int)(o + 32), 32));
                uint lastAvatar = U32(image, o + 64);
                if (lastAvatar > 64) break;                            // corrupt entry — stop this block
                uint firstAvatar = U32(image, o + 68);

                bool isDir = (flags & 0xFF) == 0x02 || tag == "*dir";
                string path = parentPath.Length == 0 ? "/" + name : parentPath + "/" + name;

                if (name.Length > 0)
                    entries.Add(new OperaEntry(path, name, isDir, byteCount, blkCount, firstAvatar, tag));

                int entrySize = EntryFixedSize + 4 * (int)(lastAvatar + 1);
                if (isDir && name.Length > 0)
                    WalkDirectory(image, blockSize, firstAvatar, blkCount, path, entries, visited, depth + 1);

                o += entrySize;
            }
        }
    }

    public static string Render(OperaVolume vol)
    {
        ArgumentNullException.ThrowIfNull(vol);
        var sb = new StringBuilder();
        sb.AppendLine(vol.Summary());
        foreach (var e in vol.Entries.Take(200))
            sb.AppendLine(e.IsDirectory
                ? $"  [dir]  {e.Path}"
                : $"  {e.ByteCount,10:N0}  {e.Path}  ({e.TypeTag})");
        if (vol.Entries.Count > 200) sb.AppendLine($"  … and {vol.Entries.Count - 200} more");
        return sb.ToString().TrimEnd();
    }

    // ---- helpers ------------------------------------------------------------

    private static uint U32(byte[] b, long o)
        => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);   // big-endian

    private static string Ascii(ReadOnlySpan<byte> b)
    {
        int end = 0;
        while (end < b.Length && b[end] != 0) end++;
        return Encoding.ASCII.GetString(b[..end]).TrimEnd();
    }
}
