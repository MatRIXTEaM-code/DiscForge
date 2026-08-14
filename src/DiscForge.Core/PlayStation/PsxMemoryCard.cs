// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.PlayStation;

public sealed class PsxMcFormatException(string message) : Exception(message);

/// <summary>One save on a PS1 memory card.</summary>
public sealed record PsxMcSave
{
    /// <summary>The product/save code, e.g. "BASLUS-00000GAMESAVE01".</summary>
    public required string Name { get; init; }
    /// <summary>The human title from the save's SC header (best-effort ASCII).</summary>
    public required string Title { get; init; }
    public required long Size { get; init; }
    /// <summary>Physical block numbers (1-15) the save occupies, in link order.</summary>
    public required IReadOnlyList<int> Blocks { get; init; }
}

public sealed record PsxMcVolume
{
    public required IReadOnlyList<PsxMcSave> Saves { get; init; }
    public int UsedBlocks => Saves.Sum(s => s.Blocks.Count);
    public int FreeBlocks => 15 - UsedBlocks;
}

/// <summary>
/// Reads a PlayStation 1 memory-card image (a ".mcr"/".mcd" file, the format pSX
/// and other emulators use) — lists the saves and extracts them. A PS1 card is a
/// plain 128 KB block filesystem; this reads a person's own card, defeating and
/// decrypting nothing. It completes DiscForge's save-card support alongside the
/// Dreamcast VMU and PS2 memory card.
///
/// Clean-room, from the public PS1 memory-card description:
///   128 KB = 16 blocks of 8 KB; each block is 64 frames of 128 bytes. Block 0 is
///   the directory: frame 0 is the "MC" header; frames 1-15 are one entry per save
///   block. A directory entry: allocation state (u32 — 0x51 first block, 0x52
///   middle, 0x53 last, 0xA0 free), file size (u32), next-block link (u16, index
///   into blocks 1-15, 0xFFFF = none), and the 20-byte product/save name. A save is
///   the chain of linked blocks; its first block opens with an "SC" header carrying
///   the display title (Shift-JIS; ASCII titles are read directly).
/// </summary>
public static class PsxMemoryCard
{
    public const int ImageSize = 128 * 1024;   // 131072
    private const int BlockSize = 8192;
    private const int FrameSize = 128;
    private const int SaveBlocks = 15;

    private const uint StateFirst = 0x51;
    private const uint StateMiddle = 0x52;
    private const uint StateLast = 0x53;
    private const uint StateFree = 0xA0;
    private const ushort NoLink = 0xFFFF;

    public static bool IsPsxMemoryCard(byte[] data) =>
        data.Length >= ImageSize && data[0] == (byte)'M' && data[1] == (byte)'C';

    public static PsxMcVolume Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < ImageSize)
            throw new PsxMcFormatException($"A PS1 memory card is {ImageSize:N0} bytes; got {data.Length:N0}.");
        if (data[0] != (byte)'M' || data[1] != (byte)'C')
            throw new PsxMcFormatException("Missing the \"MC\" header — not a PS1 memory card.");

        var saves = new List<PsxMcSave>();
        for (int idx = 0; idx < SaveBlocks; idx++)
        {
            uint state = DirState(data, idx);
            if (state != StateFirst) continue;   // only chase saves from their first block

            long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(DirFrame(idx) + 0x04));
            string name = ReadName(data, idx);
            var blocks = FollowChain(data, idx);
            string title = ReadTitle(data, blocks.Count > 0 ? blocks[0] : idx + 1);

            saves.Add(new PsxMcSave { Name = name, Title = title, Size = size, Blocks = blocks });
        }

        return new PsxMcVolume { Saves = saves };
    }

    /// <summary>Extract a save as a raw block image (its linked 8 KB blocks joined).</summary>
    public static byte[] Extract(byte[] data, PsxMcSave save)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(save);
        var ms = new MemoryStream(save.Blocks.Count * BlockSize);
        foreach (int block in save.Blocks)
        {
            long at = (long)block * BlockSize;
            if (at + BlockSize > data.Length)
                throw new PsxMcFormatException($"Save '{save.Name}' references block {block}, past the card end.");
            ms.Write(data, (int)at, BlockSize);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Build a freshly-formatted, empty PS1 memory card image (128 KB raw .mcr) —
    /// exactly what a console or a card formatter lays down on a blank card: the
    /// "MC" header, fifteen free directory frames, the broken-sector tables and
    /// zeroed data blocks. This is ordinary filesystem initialisation (the same
    /// structure <see cref="Read"/> parses), not an exploit: a formatted card holds
    /// no saves and boots nothing. <see cref="Read"/> reports it as 15 free blocks.
    /// </summary>
    public static byte[] Format()
    {
        var card = new byte[ImageSize];

        // Frame 0 — the "MC" header. The frame checksum is the XOR of the first
        // 127 bytes, which for "MC" followed by zeros is 'M' ^ 'C' = 0x0E.
        WriteHeaderFrame(card, 0);

        // Frames 1-15 — one directory entry per save block, all marked free (0xA0)
        // with no next-block link. Free-frame checksum = 0xA0 ^ 0xFF ^ 0xFF = 0xA0.
        for (int i = 1; i <= SaveBlocks; i++)
        {
            int off = i * FrameSize;
            card[off + 0x00] = (byte)StateFree;         // allocation state: free
            card[off + 0x08] = 0xFF;                    // next-block link (0xFFFF = none)
            card[off + 0x09] = 0xFF;
            card[off + FrameSize - 1] = (byte)StateFree; // XOR checksum
        }

        // Frames 16-35 — the broken-sector list. Every slot marks "no broken sector"
        // (position 0xFFFFFFFF, link 0xFFFF); its checksum is 0x00.
        for (int i = 16; i <= 35; i++)
        {
            int off = i * FrameSize;
            card[off + 0x00] = 0xFF; card[off + 0x01] = 0xFF;
            card[off + 0x02] = 0xFF; card[off + 0x03] = 0xFF;
            card[off + 0x08] = 0xFF; card[off + 0x09] = 0xFF;
        }

        // Frame 63 — the write-test frame, a duplicate of the header, as a real
        // card carries. Frames 36-62 and data blocks 1-15 stay zero.
        WriteHeaderFrame(card, 63);
        return card;
    }

    private static void WriteHeaderFrame(byte[] card, int frame)
    {
        int off = frame * FrameSize;
        card[off + 0] = (byte)'M';
        card[off + 1] = (byte)'C';
        card[off + FrameSize - 1] = 0x0E;   // 'M' ^ 'C'
    }

    // ---- internals ----------------------------------------------------------

    // Directory frame for save-block index (0-14): block idx+1, frame idx+1 of block 0.
    private static int DirFrame(int idx) => (idx + 1) * FrameSize;

    private static uint DirState(byte[] data, int idx) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(DirFrame(idx)));

    private static string ReadName(byte[] data, int idx)
    {
        int at = DirFrame(idx) + 0x0A;
        int len = 0;
        while (len < 20 && data[at + len] != 0) len++;
        return Encoding.ASCII.GetString(data, at, len);
    }

    private static List<int> FollowChain(byte[] data, int firstIdx)
    {
        var blocks = new List<int>();
        int idx = firstIdx;
        var seen = new HashSet<int>();
        while (idx >= 0 && idx < SaveBlocks && seen.Add(idx))
        {
            uint state = DirState(data, idx);
            if (state != StateFirst && state != StateMiddle && state != StateLast) break;
            blocks.Add(idx + 1);   // physical block number

            if (state == StateLast) break;
            ushort next = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(DirFrame(idx) + 0x08));
            if (next == NoLink) break;
            idx = next;
        }
        return blocks;
    }

    // The save's SC title frame: "SC" magic, then a Shift-JIS title at 0x04. English
    // titles are single-byte, so a printable-ASCII read recovers them; full-width /
    // kanji glyphs are not transcoded here.
    private static string ReadTitle(byte[] data, int block)
    {
        long at = (long)block * BlockSize;
        if (at + 0x44 > data.Length) return "";
        if (data[at] != (byte)'S' || data[at + 1] != (byte)'C') return "";

        var sb = new StringBuilder();
        for (int i = 0; i < 64; i++)
        {
            byte b = data[at + 0x04 + i];
            if (b == 0) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return sb.ToString().Trim();
    }
}
