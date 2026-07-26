// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Floppy;

/// <summary>One entry (file or directory) in an Amiga ADF volume.</summary>
public sealed record AdfEntry
{
    public required string Name { get; init; }
    /// <summary>Full path from the root, e.g. "/DIR/FILE".</summary>
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required long Size { get; init; }
    /// <summary>The block number of this entry's file/dir header block.</summary>
    public required int HeaderBlock { get; init; }
}

/// <summary>A parsed Amiga ADF disk image.</summary>
public sealed record AdfDisk
{
    public required string DiskName { get; init; }
    /// <summary>True if the filesystem is FFS (Fast File System); false = OFS (Old File System).</summary>
    public required bool Ffs { get; init; }
    public required IReadOnlyList<AdfEntry> Entries { get; init; }
}

/// <summary>
/// Reads an Amiga ADF disk image (OFS or FFS) — directory tree and file
/// extraction. Clean-room, from the public AmigaDOS on-disk layout.
///
/// The image is a flat array of 512-byte blocks, all big-endian. The bootblock
/// (blocks 0–1) starts "DOS" followed by a flag byte whose bit 0 selects FFS over
/// OFS. The root block (block totalBlocks/2, i.e. 880 for a DD disk) holds the
/// disk name and a 72-entry hash table; each non-zero slot points at a file or
/// directory header block, and header blocks in the same bucket are chained via
/// their hash-chain field. A file header block records the file size and a list of
/// up to 72 data-block pointers (stored high address to low); more pointers live in
/// extension blocks chained from the header. Data blocks are read honouring the
/// filesystem: OFS blocks carry a 24-byte header then up to 488 data bytes, FFS
/// blocks are 512 raw bytes. Names are BCPL strings (a length byte then characters).
///
/// Coverage: the common case — files and directories reachable through the hash
/// tables, both OFS and FFS, including multi-block files via the header pointer list
/// and extension-block chains. Hard/soft links and directory-cache (dircache) blocks
/// are not resolved.
/// </summary>
public static class AdfReader
{
    public const int BlockSize = 512;
    public const int DdSize = 901120;    // 1760 blocks
    public const int HdSize = 1802240;   // 3520 blocks

    private const int T_HEADER = 2;
    private const int T_LIST = 16;
    private const int ST_ROOT = 1;
    private const int ST_USERDIR = 2;
    private const int ST_FILE = -3;

    // Byte offsets within a 512-byte block.
    private const int OffType = 0x000;
    private const int OffHeaderKey = 0x004;
    private const int OffHashTable = 0x018;   // 72 longs
    private const int HashTableEntries = 72;
    private const int OffByteSize = 0x144;    // file size
    private const int OffName = 0x1B0;        // BCPL name (len byte + chars)
    private const int OffHashChain = 0x1F0;   // next entry in the same hash bucket
    private const int OffExtension = 0x1F4;   // next file-extension block
    private const int OffSecType = 0x1FC;     // secondary type

    /// <summary>Highest data-block pointer in a header/list block (they run high → low from here).</summary>
    private const int OffFirstDataPtr = 0x134;

    /// <summary>True if the buffer is a plausible ADF ("DOS" magic + a known DD/HD size).</summary>
    public static bool IsAdf(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < BlockSize) return false;
        if (data[0] != (byte)'D' || data[1] != (byte)'O' || data[2] != (byte)'S') return false;
        return data.Length == DdSize || data.Length == HdSize;
    }

    public static AdfDisk Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Read(ms.ToArray());
    }

    public static AdfDisk Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsAdf(data))
            throw new InvalidDataException("Not an ADF image (missing 'DOS' magic or unexpected size).");

        bool ffs = (data[3] & 1) != 0;
        int totalBlocks = data.Length / BlockSize;
        int rootBlock = totalBlocks / 2;

        string diskName = ReadName(data, rootBlock);

        var entries = new List<AdfEntry>();
        var visited = new HashSet<int>();
        WalkDirectory(data, ffs, rootBlock, "", entries, visited);

        return new AdfDisk { DiskName = diskName, Ffs = ffs, Entries = entries };
    }

    private static void WalkDirectory(byte[] data, bool ffs, int dirBlock, string parentPath,
                                      List<AdfEntry> entries, HashSet<int> visited)
    {
        if (!visited.Add(dirBlock)) return;
        int block = dirBlock * BlockSize;
        if (block + BlockSize > data.Length) return;

        for (int i = 0; i < HashTableEntries; i++)
        {
            int header = ReadInt(data, block + OffHashTable + i * 4);
            while (header > 0)
            {
                int hb = header * BlockSize;
                if (hb + BlockSize > data.Length) break;

                int secType = ReadInt(data, hb + OffSecType);
                string name = ReadName(data, header);
                string path = parentPath + "/" + name;

                if (secType == ST_USERDIR)
                {
                    entries.Add(new AdfEntry
                    {
                        Name = name, Path = path, IsDirectory = true, Size = 0, HeaderBlock = header,
                    });
                    WalkDirectory(data, ffs, header, path, entries, visited);
                }
                else if (secType == ST_FILE)
                {
                    long size = (uint)ReadInt(data, hb + OffByteSize);
                    entries.Add(new AdfEntry
                    {
                        Name = name, Path = path, IsDirectory = false, Size = size, HeaderBlock = header,
                    });
                }
                // Other secondary types (links) are not resolved.

                header = ReadInt(data, hb + OffHashChain);   // next entry in this bucket
            }
        }
    }

    /// <summary>Extract a file's bytes from its header block, honouring OFS vs FFS.</summary>
    public static byte[] ExtractFile(byte[] data, AdfEntry entry)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory)
            throw new InvalidOperationException($"'{entry.Path}' is a directory, not a file.");

        bool ffs = (data[3] & 1) != 0;
        long remaining = entry.Size;
        var outBytes = new List<byte>((int)Math.Min(remaining, int.MaxValue));

        int headerBlock = entry.HeaderBlock;
        var visitedHeaders = new HashSet<int>();

        while (headerBlock > 0 && remaining > 0)
        {
            if (!visitedHeaders.Add(headerBlock))
                throw new InvalidDataException("ADF file-extension chain loops back on itself.");
            int hb = headerBlock * BlockSize;
            if (hb + BlockSize > data.Length)
                throw new InvalidDataException($"ADF header block {headerBlock} is out of range.");

            // Data-block pointers run high address to low from OffFirstDataPtr.
            for (int p = 0; p < HashTableEntries && remaining > 0; p++)
            {
                int dataBlock = ReadInt(data, hb + OffFirstDataPtr - p * 4);
                if (dataBlock <= 0) continue;
                remaining -= AppendDataBlock(data, ffs, dataBlock, remaining, outBytes);
            }

            headerBlock = ReadInt(data, hb + OffExtension);
        }

        return outBytes.ToArray();
    }

    /// <summary>Append one data block's payload; returns how many bytes were taken.</summary>
    private static long AppendDataBlock(byte[] data, bool ffs, int dataBlock, long remaining, List<byte> outBytes)
    {
        int db = dataBlock * BlockSize;
        if (db < 0 || db + BlockSize > data.Length)
            throw new InvalidDataException($"ADF data block {dataBlock} is out of range.");

        int payloadStart, payloadLen;
        if (ffs)
        {
            payloadStart = db;
            payloadLen = BlockSize;
        }
        else
        {
            // OFS: 24-byte header, then up to 488 data bytes; data_size @ 0x0C is authoritative.
            payloadStart = db + 24;
            int dataSize = ReadInt(data, db + 0x0C);
            payloadLen = dataSize is > 0 and <= 488 ? dataSize : 488;
        }

        long take = Math.Min(payloadLen, remaining);
        for (long i = 0; i < take; i++) outBytes.Add(data[payloadStart + (int)i]);
        return take;
    }

    // --- helpers -------------------------------------------------------------

    private static int ReadInt(byte[] data, int at)
    {
        if (at < 0 || at + 4 > data.Length) return 0;
        return BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at));
    }

    /// <summary>Read the BCPL name (length byte + chars) at OffName of the given block. Robust to junk.</summary>
    private static string ReadName(byte[] data, int block)
    {
        int at = block * BlockSize + OffName;
        if (at < 0 || at + 1 > data.Length) return "";
        int len = data[at];
        if (len <= 0 || len > 30) return "";
        if (at + 1 + len > data.Length) return "";
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte b = data[at + 1 + i];
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        }
        return sb.ToString();
    }
}
