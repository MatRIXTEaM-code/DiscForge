// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Gdi;

/// <summary>
/// The Dreamcast <c>1ST_READ.BIN</c> scramble — the transform "bin2boot" applies.
/// On a CD (not a GD-ROM) the main binary is not loaded contiguously but
/// scatter-loaded, so it is stored "scrambled": its 32-byte slices are permuted by
/// a seeded shuffle. This descrambles a disc's main binary back to the plain
/// executable (to inspect or extract it) and scrambles a plain binary the way the
/// boot ROM expects.
///
/// This is a plain, documented byte-permutation, not encryption or copy
/// protection — the byte histogram is unchanged, only slice order moves. DiscForge
/// implements the transform only; it does NOT build a self-boot (MIL-CD) disc,
/// which is the console-security trick that stays outside the clean-room rule
/// (docs/COMPARISON.md §13).
///
/// Clean-room, from the public description of the algorithm:
///   seed = fileSize &amp; 0xFFFF
///   rand(): seed = (seed*2109 + 9273) &amp; 0x7FFF; return (seed + 0xC000) &amp; 0xFFFF
///   The file (its length rounded down to a multiple of 32) is split into chunks
///   of decreasing size — 2 MB, then 1 MB, … down to 32 bytes — and each chunk's
///   32-byte slices are shuffled with a Fisher-Yates pass driven by rand(). Any
///   tail of fewer than 32 bytes is copied straight through.
///
/// Scramble and descramble are exact inverses (same permutation, opposite copy
/// direction), which is what the round-trip test proves.
/// </summary>
public static class DreamcastScramble
{
    private const int MaxChunk = 2048 * 1024;   // 2 MB
    private const int SliceSize = 32;

    /// <summary>Scramble a plain binary into the boot-ROM's scattered form.</summary>
    public static byte[] Scramble(byte[] plain) => Transform(plain, descramble: false);

    /// <summary>Descramble a scattered <c>1ST_READ.BIN</c> back to the plain binary.</summary>
    public static byte[] Descramble(byte[] scrambled) => Transform(scrambled, descramble: true);

    private static byte[] Transform(byte[] src, bool descramble)
    {
        ArgumentNullException.ThrowIfNull(src);
        var dst = new byte[src.Length];

        uint seed = (uint)(src.Length & 0xFFFF);
        int sliced = src.Length & ~31;   // bytes covered by the slicing
        int pos = 0;
        int remaining = sliced;

        for (int chunk = MaxChunk; chunk >= SliceSize; chunk >>= 1)
            while (remaining >= chunk)
            {
                SliceChunk(src, dst, pos, chunk, ref seed, descramble);
                pos += chunk;
                remaining -= chunk;
            }

        // Tail (< 32 bytes): straight copy.
        for (int i = sliced; i < src.Length; i++) dst[i] = src[i];
        return dst;
    }

    private static void SliceChunk(byte[] src, byte[] dst, int off, int size, ref uint seed, bool descramble)
    {
        int slices = size / SliceSize;
        var idx = new int[slices];
        for (int i = 0; i < slices; i++) idx[i] = i;

        int seq = 0;   // the sequential slice position within this chunk
        for (int i = slices - 1; i >= 0; i--)
        {
            uint r = Rand(ref seed);
            int x = (int)((r * (uint)i) >> 16);
            (idx[i], idx[x]) = (idx[x], idx[i]);

            int permuted = idx[i];
            int seqAt = off + seq * SliceSize;
            int permAt = off + permuted * SliceSize;

            // Descramble: sequential scrambled slice -> its original position.
            // Scramble:   original-position slice   -> sequential scrambled slot.
            if (descramble)
                Array.Copy(src, seqAt, dst, permAt, SliceSize);
            else
                Array.Copy(src, permAt, dst, seqAt, SliceSize);

            seq++;
        }
    }

    private static uint Rand(ref uint seed)
    {
        seed = (seed * 2109 + 9273) & 0x7FFF;
        return (seed + 0xC000) & 0xFFFF;
    }
}
