// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Saves;

public sealed class GcSaveFormatException(string message) : Exception(message);

/// <summary>One save on a GameCube memory card (one directory entry + its data blocks).</summary>
public sealed record GcSave
{
    /// <summary>Four-character game code, e.g. "GALE" (Melee).</summary>
    public required string GameCode { get; init; }
    /// <summary>Two-character maker/company code, e.g. "01" (Nintendo).</summary>
    public required string Maker { get; init; }
    /// <summary>Internal save file name (ASCII).</summary>
    public required string FileName { get; init; }
    /// <summary>The two 32-byte comment lines from the save data (best-effort ASCII), joined by " / ".</summary>
    public required string Comment { get; init; }
    /// <summary>Number of 0x2000-byte blocks the save occupies.</summary>
    public required int BlockCount { get; init; }
    /// <summary>First data block (5-based physical block on a card; entry value as stored for a .gci).</summary>
    public required int FirstBlock { get; init; }

    /// <summary>The raw 0x40-byte directory entry, kept so a card save can be re-emitted as a .gci.</summary>
    internal byte[] Entry { get; init; } = Array.Empty<byte>();
}

/// <summary>The contents of a GameCube memory-card image (.raw / .bin).</summary>
public sealed record GcMemoryCard
{
    public required IReadOnlyList<GcSave> Saves { get; init; }
}

/// <summary>
/// Shared GameCube memory-card layout constants and the directory-entry parse used
/// by both the single-save (.gci) and whole-card readers. GameCube saves are
/// BIG-ENDIAN throughout.
///
/// Clean-room, from the public GameCube memory-card description:
///   A card is a multiple of 0x2000-byte blocks (a power-of-two block count, 64…2048).
///   Block 0 is the card header; block 1 is the directory (a backup copy at block 2),
///   127 entries of 0x40 bytes; blocks 3–4 are the block-allocation table (BAT) and its
///   backup; data blocks start at block 5. A directory entry (0x40 bytes, big-endian):
///     0x00 game code (4), 0x04 maker (2), 0x06 0xFF, 0x07 banner flags, 0x08 file name
///     (0x20 ASCII), 0x28 modification time (u32), 0x2C image/banner offset (u32),
///     0x30 icon format (u16), 0x32 animation speed (u16), 0x34 permission (u8),
///     0x35 copy counter (u8), 0x36 first block (u16), 0x38 block count (u16),
///     0x3C comment offset (u32, into the save data — two 32-byte lines).
///   The BAT map is an array of u16 "next block" links at offset 0x0A of block 3, one
///   per data block (5-based); 0x0000 = free, 0xFFFF = last block of a chain. A .gci is
///   one save: the 0x40 directory entry followed by blockCount × 0x2000 bytes of data.
/// </summary>
internal static class GcLayout
{
    public const int BlockSize = 0x2000;
    public const int EntrySize = 0x40;
    public const int MaxDirEntries = 127;
    public const int DirBlock = 1;
    public const int BatBlock = 3;
    public const int FirstDataBlock = 5;
    public const int BatMapOffset = 0x0A;   // within the BAT block
    public const ushort ChainEnd = 0xFFFF;

    public static string AsciiFixed(ReadOnlySpan<byte> s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (byte b in s)
        {
            if (b == 0) break;
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : ' ');
        }
        return sb.ToString().TrimEnd();
    }

    public static bool GameCodeIsEmpty(ReadOnlySpan<byte> entry) =>
        entry[0] == 0xFF && entry[1] == 0xFF && entry[2] == 0xFF && entry[3] == 0xFF;

    public static int FirstBlock(ReadOnlySpan<byte> entry) => BinaryPrimitives.ReadUInt16BigEndian(entry[0x36..]);
    public static int BlockCount(ReadOnlySpan<byte> entry) => BinaryPrimitives.ReadUInt16BigEndian(entry[0x38..]);
    public static long CommentOffset(ReadOnlySpan<byte> entry) => BinaryPrimitives.ReadUInt32BigEndian(entry[0x3C..]);

    /// <summary>Read the two 32-byte comment lines from the save payload at the entry's comment offset.</summary>
    public static string ReadComment(ReadOnlySpan<byte> payload, long commentOffset)
    {
        if (commentOffset < 0 || commentOffset + 32 > payload.Length) return "";
        string line1 = AsciiFixed(payload.Slice((int)commentOffset, 32));
        string line2 = commentOffset + 64 <= payload.Length
            ? AsciiFixed(payload.Slice((int)commentOffset + 32, 32))
            : "";
        return line2.Length > 0 ? $"{line1} / {line2}" : line1;
    }

    public static GcSave ParseEntry(byte[] entry, string comment, int firstBlock, int blockCount) => new()
    {
        GameCode = AsciiFixed(entry.AsSpan(0x00, 4)),
        Maker = AsciiFixed(entry.AsSpan(0x04, 2)),
        FileName = AsciiFixed(entry.AsSpan(0x08, 0x20)),
        Comment = comment,
        FirstBlock = firstBlock,
        BlockCount = blockCount,
        Entry = entry,
    };
}

/// <summary>Reads a single GameCube save file (.gci): a 0x40 directory entry then its data.</summary>
public static class GciReader
{
    public static bool IsGci(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < GcLayout.EntrySize + GcLayout.BlockSize) return false;
        if (GcLayout.GameCodeIsEmpty(data)) return false;
        int blocks = GcLayout.BlockCount(data);
        if (blocks is < 1 or > 2043) return false;
        return (long)GcLayout.EntrySize + (long)blocks * GcLayout.BlockSize == data.Length;
    }

    public static GcSave Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < GcLayout.EntrySize + GcLayout.BlockSize)
            throw new GcSaveFormatException($"A .gci is at least {GcLayout.EntrySize + GcLayout.BlockSize:N0} bytes; got {data.Length:N0}.");

        int blockCount = GcLayout.BlockCount(data);
        if (blockCount < 1)
            throw new GcSaveFormatException("The .gci directory entry has a zero block count.");

        long need = (long)GcLayout.EntrySize + (long)blockCount * GcLayout.BlockSize;
        if (data.Length < need)
            throw new GcSaveFormatException($"The .gci claims {blockCount} block(s) ({need:N0} bytes) but is only {data.Length:N0}.");

        var entry = data.AsSpan(0, GcLayout.EntrySize).ToArray();
        var payload = data.AsSpan(GcLayout.EntrySize, blockCount * GcLayout.BlockSize);
        string comment = GcLayout.ReadComment(payload, GcLayout.CommentOffset(entry));
        return GcLayout.ParseEntry(entry, comment, GcLayout.FirstBlock(entry), blockCount);
    }

    /// <summary>The save data (payload) of a .gci, without the 0x40 header.</summary>
    public static byte[] Payload(byte[] data)
    {
        var save = Read(data);
        return data.AsSpan(GcLayout.EntrySize, save.BlockCount * GcLayout.BlockSize).ToArray();
    }
}

/// <summary>Reads a whole GameCube memory-card image and extracts saves as .gci files.</summary>
public static class GcMemoryCardReader
{
    public static bool IsGcMemoryCard(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || data.Length % GcLayout.BlockSize != 0) return false;
        int blocks = data.Length / GcLayout.BlockSize;
        bool powerOfTwo = (blocks & (blocks - 1)) == 0;
        return powerOfTwo && blocks is >= 64 and <= 2048;   // 4 Mbit … 128 Mbit cards
    }

    public static GcMemoryCard Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsGcMemoryCard(data))
            throw new GcSaveFormatException(
                $"Not a GameCube memory-card image — expected a power-of-two block count (64…2048) of {GcLayout.BlockSize:N0}-byte blocks; got {data.Length:N0} bytes.");

        var saves = new List<GcSave>();
        int dirBase = GcLayout.DirBlock * GcLayout.BlockSize;
        for (int i = 0; i < GcLayout.MaxDirEntries; i++)
        {
            var entry = data.AsSpan(dirBase + i * GcLayout.EntrySize, GcLayout.EntrySize).ToArray();
            if (GcLayout.GameCodeIsEmpty(entry)) continue;

            int firstBlock = GcLayout.FirstBlock(entry);
            int blockCount = GcLayout.BlockCount(entry);
            if (blockCount < 1) continue;

            byte[] payload = GatherPayload(data, firstBlock, blockCount);
            string comment = GcLayout.ReadComment(payload, GcLayout.CommentOffset(entry));
            saves.Add(GcLayout.ParseEntry(entry, comment, firstBlock, blockCount));
        }
        return new GcMemoryCard { Saves = saves };
    }

    /// <summary>Rebuild a save as a standalone .gci: its 0x40 directory entry + its data blocks (BAT chain).</summary>
    public static byte[] ExtractSaveToGci(byte[] data, GcSave save)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(save);
        if (save.Entry.Length != GcLayout.EntrySize)
            throw new GcSaveFormatException("The save has no directory entry to emit a .gci header.");

        byte[] payload = GatherPayload(data, save.FirstBlock, save.BlockCount);
        var gci = new byte[GcLayout.EntrySize + payload.Length];
        save.Entry.CopyTo(gci, 0);
        payload.CopyTo(gci, GcLayout.EntrySize);
        return gci;
    }

    /// <summary>Gather a save's data by following the BAT chain from its first block.</summary>
    private static byte[] GatherPayload(byte[] data, int firstBlock, int blockCount)
    {
        int mapBase = GcLayout.BatBlock * GcLayout.BlockSize + GcLayout.BatMapOffset;
        var payload = new byte[(long)blockCount * GcLayout.BlockSize <= int.MaxValue ? blockCount * GcLayout.BlockSize : 0];
        if (payload.Length == 0 && blockCount > 0)
            throw new GcSaveFormatException("Save is implausibly large.");

        int block = firstBlock;
        var seen = new HashSet<int>();
        for (int n = 0; n < blockCount; n++)
        {
            if (block < GcLayout.FirstDataBlock || !seen.Add(block))
                throw new GcSaveFormatException($"Save block chain is invalid at block {block}.");
            long off = (long)block * GcLayout.BlockSize;
            if (off + GcLayout.BlockSize > data.Length)
                throw new GcSaveFormatException($"Save references block {block}, past the card end.");
            Array.Copy(data, (int)off, payload, n * GcLayout.BlockSize, GcLayout.BlockSize);

            int mapIndex = mapBase + (block - GcLayout.FirstDataBlock) * 2;
            if (mapIndex + 2 > data.Length) break;
            int next = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(mapIndex));
            if (next == GcLayout.ChainEnd) break;
            block = next;
        }
        return payload;
    }
}
