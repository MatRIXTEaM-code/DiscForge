// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Vmu;

public sealed class VmuFormatException(string message) : Exception(message);

/// <summary>One saved file on a VMU — a game (mini-game) or a data save.</summary>
public sealed record VmuFile
{
    public required string Name { get; init; }
    /// <summary>0x33 = data save, 0xCC = game (mini-game).</summary>
    public required byte FileType { get; init; }
    public required bool CopyProtected { get; init; }
    public required int FirstBlock { get; init; }
    public required int SizeBlocks { get; init; }
    public required int HeaderOffsetBlocks { get; init; }
    /// <summary>The 16-char and 32-char descriptions from the VMS header, if read.</summary>
    public string? ShortDescription { get; init; }
    public string? LongDescription { get; init; }

    public bool IsGame => FileType == 0xCC;
    public long SizeBytes => (long)SizeBlocks * VmuImage.BlockSize;
}

/// <summary>The contents of a VMU (Visual Memory) flash dump.</summary>
public sealed record VmuVolume
{
    public required bool Formatted { get; init; }
    public required int TotalBlocks { get; init; }
    public required int UserBlocks { get; init; }
    public required int FreeBlocks { get; init; }
    public required IReadOnlyList<VmuFile> Files { get; init; }
}

/// <summary>
/// Reads a Sega Dreamcast VMU (Visual Memory Unit / VMS) flash filesystem — the
/// memory-card saves the "VMU Tool" / "VMU Dream Explorer" browse and extract. A
/// VMU is a plain 128 KB FAT-like filesystem; this lists the saves and extracts
/// each as a raw VMS file. It reads a person's own memory-card dump — no
/// protection is involved (the per-file "copy protect" flag is reported, never
/// defeated: an extract honours it by default).
///
/// Clean-room, from the public VMU flash-filesystem description (Marcus
/// Comstedt's documentation):
///   256 blocks of 512 bytes. Block 255 = root, 254 = FAT, 253-241 = directory,
///   0-199 = user files. Root holds the FAT/directory locations and sizes. The FAT
///   is 256 little-endian 16-bit entries: 0xFFFA = last block, 0xFFFC = free, else
///   the next block. A 32-byte directory entry: type (0x33 data / 0xCC game),
///   copy-protect, first block, 12-char name, timestamp, size in blocks, header
///   offset. A file is the FAT chain from its first block.
/// </summary>
public static class VmuImage
{
    public const int BlockSize = 512;
    public const int TotalBlocks = 256;
    public const int ImageSize = BlockSize * TotalBlocks;   // 131072

    private const int RootBlock = 255;
    private const ushort FatLastBlock = 0xFFFA;
    private const ushort FatUnallocated = 0xFFFC;

    public static bool IsVmu(byte[] data)
    {
        if (data.Length < ImageSize) return false;
        // A formatted card has 0x55 through the root block's first 16 bytes.
        int at = RootBlock * BlockSize;
        for (int i = 0; i < 16; i++) if (data[at + i] != 0x55) return false;
        return true;
    }

    public static VmuVolume Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < ImageSize)
            throw new VmuFormatException(
                $"A VMU image is {ImageSize:N0} bytes (256 × 512); got {data.Length:N0}.");

        int root = RootBlock * BlockSize;
        bool formatted = true;
        for (int i = 0; i < 16; i++) if (data[root + i] != 0x55) { formatted = false; break; }

        // Root fields (fall back to the standard layout if a field reads zero, as
        // some dumps leave them blank).
        int fatLoc = Read16(data, root + 0x46, 254);
        int dirLoc = Read16(data, root + 0x4A, 253);
        int dirSize = Read16(data, root + 0x4C, 13);
        int userBlocks = Read16(data, root + 0x50, 200);

        var fat = ReadFat(data, fatLoc);
        var files = ReadDirectory(data, dirLoc, dirSize, fat);

        int free = 0;
        for (int b = 0; b < userBlocks && b < fat.Length; b++)
            if (fat[b] == FatUnallocated) free++;

        return new VmuVolume
        {
            Formatted = formatted,
            TotalBlocks = TotalBlocks,
            UserBlocks = userBlocks,
            FreeBlocks = free,
            Files = files,
        };
    }

    /// <summary>Extract a save's raw VMS bytes by walking its FAT chain. Honours the
    /// copy-protect flag unless <paramref name="force"/> is set.</summary>
    public static byte[] Extract(byte[] data, VmuFile file, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(file);
        if (file.CopyProtected && !force)
            throw new InvalidOperationException(
                $"'{file.Name}' is flagged copy-protected. DiscForge reports the flag rather than " +
                "silently ignoring it; pass force to extract your own save anyway.");

        var fat = ReadFat(data, 254);
        using var ms = new MemoryStream(file.SizeBlocks * BlockSize);
        int block = file.FirstBlock;
        int guard = 0;
        for (int i = 0; i < file.SizeBlocks; i++)
        {
            if (block < 0 || (long)(block + 1) * BlockSize > data.Length)
                throw new VmuFormatException($"'{file.Name}' points at block {block}, outside the image.");
            ms.Write(data, block * BlockSize, BlockSize);

            if (i < file.SizeBlocks - 1)
            {
                block = block < fat.Length ? fat[block] : FatLastBlock;
                if (block == FatLastBlock) break;
            }
            if (++guard > TotalBlocks)
                throw new VmuFormatException($"'{file.Name}' has a cyclic FAT chain.");
        }
        return ms.ToArray();
    }

    // ---- internals ----------------------------------------------------------

    private static ushort[] ReadFat(byte[] data, int fatBlock)
    {
        int at = fatBlock * BlockSize;
        var fat = new ushort[TotalBlocks];
        for (int i = 0; i < TotalBlocks; i++)
            fat[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + i * 2, 2));
        return fat;
    }

    private static List<VmuFile> ReadDirectory(byte[] data, int dirLoc, int dirSize, ushort[] fat)
    {
        var files = new List<VmuFile>();
        // The directory occupies dirSize blocks descending from dirLoc.
        for (int b = 0; b < dirSize; b++)
        {
            int block = dirLoc - b;
            if (block < 0 || (block + 1) * BlockSize > data.Length) break;
            int baseOff = block * BlockSize;
            for (int e = 0; e < BlockSize / 32; e++)
            {
                int at = baseOff + e * 32;
                byte type = data[at];
                if (type != 0x33 && type != 0xCC) continue;   // 0x00 = empty entry

                int firstBlock = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2, 2));
                string name = Encoding.ASCII.GetString(data, at + 4, 12).TrimEnd('\0', ' ');
                int sizeBlocks = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 0x18, 2));
                int headerOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 0x1A, 2));

                var (shortDesc, longDesc) = ReadVmsDescriptions(data, firstBlock, headerOffset, fat, sizeBlocks);

                files.Add(new VmuFile
                {
                    Name = name,
                    FileType = type,
                    CopyProtected = data[at + 1] != 0x00,
                    FirstBlock = firstBlock,
                    SizeBlocks = sizeBlocks,
                    HeaderOffsetBlocks = headerOffset,
                    ShortDescription = shortDesc,
                    LongDescription = longDesc,
                });
            }
        }
        return files;
    }

    // The VMS header (16-char + 32-char descriptions) sits at headerOffset blocks
    // into the file. Walk the FAT chain that far, then read the two strings.
    private static (string?, string?) ReadVmsDescriptions(
        byte[] data, int firstBlock, int headerOffset, ushort[] fat, int sizeBlocks)
    {
        int block = firstBlock;
        for (int i = 0; i < headerOffset; i++)
        {
            if (block >= fat.Length) return (null, null);
            block = fat[block];
            if (block == FatLastBlock || block == FatUnallocated) return (null, null);
        }
        int at = block * BlockSize;
        if (at + 0x30 > data.Length) return (null, null);

        string s = Encoding.ASCII.GetString(data, at, 16).TrimEnd('\0', ' ');
        string l = Encoding.ASCII.GetString(data, at + 0x10, 32).TrimEnd('\0', ' ');
        return (s.Length > 0 ? s : null, l.Length > 0 ? l : null);
    }

    private static int Read16(byte[] data, int at, int fallback)
    {
        int v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at, 2));
        return v == 0 ? fallback : v;
    }
}
