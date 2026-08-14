// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Vmu;

/// <summary>
/// Writes a Sega Dreamcast VMU flash image — the other half of
/// <see cref="VmuImage"/>: format a blank card and add save (VMS) files, so a save
/// pulled off one card can be repacked onto another. It builds the same plain
/// filesystem the reader parses (root, FAT, directory), and the round-trip test —
/// create, add, read back, extract — is what validates it.
///
/// Layout follows the public description: data saves allocate from the highest free
/// user block downward, linked through the FAT, with a 32-byte directory entry.
/// Deterministic (a fixed timestamp), so the same inputs yield the same image.
/// </summary>
public static class VmuBuilder
{
    private const int RootBlock = 255;
    private const int FatBlock = 254;
    private const int DirBlock = 253;
    private const int DirBlocks = 13;
    private const int UserBlocks = 200;
    private const ushort FatLast = 0xFFFA;
    private const ushort FatFree = 0xFFFC;

    /// <summary>A blank, formatted 128 KB VMU card.</summary>
    public static byte[] CreateFormatted()
    {
        var d = new byte[VmuImage.ImageSize];

        int root = RootBlock * VmuImage.BlockSize;
        for (int i = 0; i < 16; i++) d[root + i] = 0x55;    // formatted marker
        // A fixed BCD timestamp (2026-01-01 00:00:00) for deterministic output.
        d[root + 0x30] = 0x20; d[root + 0x31] = 0x26;       // century, year
        d[root + 0x32] = 0x01; d[root + 0x33] = 0x01;       // month, day
        U16(d, root + 0x46, FatBlock);                       // FAT location
        U16(d, root + 0x48, 1);                              // FAT size (blocks)
        U16(d, root + 0x4A, DirBlock);                       // directory location
        U16(d, root + 0x4C, DirBlocks);                      // directory size (blocks)
        U16(d, root + 0x50, UserBlocks);                     // user block count

        // FAT: mark every block free, then the system blocks as their own last-block.
        int fat = FatBlock * VmuImage.BlockSize;
        for (int i = 0; i < 256; i++) U16(d, fat + i * 2, FatFree);
        U16(d, fat + RootBlock * 2, FatLast);
        U16(d, fat + FatBlock * 2, FatLast);
        for (int b = DirBlock; b > DirBlock - DirBlocks; b--)
            U16(d, fat + b * 2, (ushort)(b == DirBlock - DirBlocks + 1 ? FatLast : b - 1));

        return d;
    }

    /// <summary>Add a save to the card, returning the modified image. Throws if
    /// there is not enough free space or the directory is full.</summary>
    public static byte[] Add(byte[] image, string name, byte[] vms, bool isGame = false, bool copyProtected = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(vms);
        if (image.Length < VmuImage.ImageSize) throw new VmuFormatException("Not a 128 KB VMU image.");

        var d = (byte[])image.Clone();
        int sizeBlocks = (vms.Length + VmuImage.BlockSize - 1) / VmuImage.BlockSize;
        if (sizeBlocks == 0) throw new ArgumentException("An empty save has no blocks.", nameof(vms));

        var fat = ReadFat(d);
        // Data saves take the highest free blocks first; games need a contiguous run
        // from block 0.
        var blocks = isGame ? AllocateContiguousFromZero(fat, sizeBlocks) : AllocateHighestFirst(fat, sizeBlocks);
        if (blocks.Count < sizeBlocks)
            throw new InvalidOperationException(
                $"Not enough free space for '{name}': needs {sizeBlocks} block(s), " +
                $"{blocks.Count} free.");

        // Write the data across the chosen blocks, and link them in the FAT.
        for (int i = 0; i < blocks.Count; i++)
        {
            int at = blocks[i] * VmuImage.BlockSize;
            int srcOff = i * VmuImage.BlockSize;
            int len = Math.Min(VmuImage.BlockSize, vms.Length - srcOff);
            if (len > 0) Array.Copy(vms, srcOff, d, at, len);
            fat[blocks[i]] = (ushort)(i == blocks.Count - 1 ? FatLast : blocks[i + 1]);
        }
        WriteFat(d, fat);

        WriteDirectoryEntry(d, name, blocks[0], sizeBlocks, isGame, copyProtected);
        return d;
    }

    // ---- allocation ---------------------------------------------------------

    private static List<int> AllocateHighestFirst(ushort[] fat, int count)
    {
        var chosen = new List<int>(count);
        for (int b = UserBlocks - 1; b >= 0 && chosen.Count < count; b--)
            if (fat[b] == FatFree) chosen.Add(b);
        return chosen;
    }

    private static List<int> AllocateContiguousFromZero(ushort[] fat, int count)
    {
        var chosen = new List<int>(count);
        for (int b = 0; b < UserBlocks && chosen.Count < count; b++)
        {
            if (fat[b] == FatFree) chosen.Add(b);
            else chosen.Clear();   // must be a contiguous run
        }
        return chosen.Count >= count ? chosen.GetRange(0, count) : chosen;
    }

    private static void WriteDirectoryEntry(byte[] d, string name, int firstBlock, int sizeBlocks,
                                            bool isGame, bool copyProtected)
    {
        for (int b = 0; b < DirBlocks; b++)
        {
            int block = DirBlock - b;
            int baseOff = block * VmuImage.BlockSize;
            for (int e = 0; e < VmuImage.BlockSize / 32; e++)
            {
                int at = baseOff + e * 32;
                if (d[at] != 0x00) continue;   // occupied

                d[at] = (byte)(isGame ? 0xCC : 0x33);
                d[at + 1] = (byte)(copyProtected ? 0xFF : 0x00);
                U16(d, at + 2, firstBlock);
                var nameBytes = Encoding.ASCII.GetBytes(name);
                Array.Copy(nameBytes, 0, d, at + 4, Math.Min(12, nameBytes.Length));
                // Fixed BCD creation timestamp.
                d[at + 0x10] = 0x20; d[at + 0x11] = 0x26; d[at + 0x12] = 0x01; d[at + 0x13] = 0x01;
                U16(d, at + 0x18, sizeBlocks);
                U16(d, at + 0x1A, 0);          // VMS header at the file's first block
                return;
            }
        }
        throw new InvalidOperationException("The VMU directory is full (200 entries).");
    }

    // ---- FAT ----------------------------------------------------------------

    private static ushort[] ReadFat(byte[] d)
    {
        int at = FatBlock * VmuImage.BlockSize;
        var fat = new ushort[256];
        for (int i = 0; i < 256; i++) fat[i] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at + i * 2, 2));
        return fat;
    }

    private static void WriteFat(byte[] d, ushort[] fat)
    {
        int at = FatBlock * VmuImage.BlockSize;
        for (int i = 0; i < 256; i++) U16(d, at + i * 2, fat[i]);
    }

    private static void U16(byte[] d, int at, int v) =>
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at, 2), (ushort)v);
}
