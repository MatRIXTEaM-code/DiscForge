// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.GameCube;

/// <summary>
/// A clean-room reconstruction of the deterministic "junk" (garbage padding) a GameCube/Wii disc
/// writes into the gaps between its real data and out to the disc edge. The public description of
/// the scheme is a Lagged Fibonacci Generator (taps k=521, j=32, combined by XOR) whose 521-word
/// state is warmed from a per-block seed derived from the disc id and the block index; the disc is
/// generated in 0x40000-byte (256 KiB) blocks.
///
/// IMPORTANT — this is <b>unvalidated against a real disc</b>. The exact seed derivation is the
/// least-certain part of the public description, so this generator is NOT asserted to be
/// byte-identical to Nintendo's until it is proven against a Redump-verified un-scrubbed image (or
/// an NKit recovery-block CRC). It exists so the SELF-VALIDATING reconstructor
/// (<see cref="GcJunkReconstructor"/>) has an engine to test: that reconstructor regenerates the
/// image's OWN surviving junk first and only fills scrubbed regions if it matches byte-for-byte, so
/// a wrong constant here can only cause a decline, never a silent corruption. This is deliberately
/// clean-room — no Dolphin/GPL code is used or consulted.
/// </summary>
public static class GcJunkGenerator
{
    /// <summary>The disc is generated in 256 KiB blocks; each block re-seeds the generator.</summary>
    public const int BlockSize = 0x40000;

    // Lagged Fibonacci parameters (public description): x[n] = x[n-521] XOR x[n-32].
    private const int K = 521;   // long lag / state length in 32-bit words
    private const int J = 32;    // short lag

    // The classic Nintendo LCG used to warm the state (same constants as the SDK RNG).
    private const uint LcgMul = 0x41C64E6D;
    private const uint LcgInc = 0x3039;

    /// <summary>
    /// Fill <paramref name="dest"/> with the junk stream for byte range starting at
    /// <paramref name="absoluteOffset"/> on a disc whose 4-byte id is <paramref name="discId"/>.
    /// Honours 0x40000 block boundaries (each block is independently seeded), so a caller can ask
    /// for any sub-range of any region and get the same bytes the disc holds there.
    /// </summary>
    public static void Fill(ReadOnlySpan<byte> discId, long absoluteOffset, Span<byte> dest)
    {
        if (discId.Length < 4) throw new ArgumentException("Disc id must be at least 4 bytes.", nameof(discId));
        if (absoluteOffset < 0) throw new ArgumentOutOfRangeException(nameof(absoluteOffset));

        uint id = BinaryPrimitives.ReadUInt32BigEndian(discId[..4]);
        int written = 0;
        long offset = absoluteOffset;

        while (written < dest.Length)
        {
            long block = offset / BlockSize;
            int within = (int)(offset % BlockSize);
            int take = (int)Math.Min(dest.Length - written, BlockSize - within);

            // Regenerate this block's bytes [within, within+take) into dest.
            FillBlockRange(id, block, within, dest.Slice(written, take));

            written += take;
            offset += take;
        }
    }

    /// <summary>Convenience: allocate and return a region's junk bytes.</summary>
    public static byte[] Generate(ReadOnlySpan<byte> discId, long absoluteOffset, int length)
    {
        var buf = new byte[length];
        Fill(discId, absoluteOffset, buf);
        return buf;
    }

    /// <summary>Regenerate bytes [start, start+dest.Length) of a single 256 KiB block.</summary>
    private static void FillBlockRange(uint id, long block, int start, Span<byte> dest)
    {
        // Per-block seed: mix the disc id with the block index through one LCG step. (Documented as
        // the uncertain part; the self-validating reconstructor gates any real use on a byte match.)
        uint seed = (id ^ (uint)(block * 0x0000_0001u)) * LcgMul + LcgInc + (uint)block;

        // Warm the 521-word state with the LCG.
        Span<uint> state = stackalloc uint[K];
        uint x = seed;
        for (int i = 0; i < K; i++)
        {
            x = x * LcgMul + LcgInc;
            state[i] = x;
        }

        // Advance the LFG and emit bytes big-endian, skipping to `start` then filling `dest`.
        int pos = 0;
        int produced = 0;                       // bytes produced so far within the block
        int end = start + dest.Length;
        Span<byte> word = stackalloc byte[4];

        while (produced < end)
        {
            uint next = state[pos] ^ state[(pos + K - J) % K];
            state[pos] = next;
            pos = (pos + 1) % K;

            BinaryPrimitives.WriteUInt32BigEndian(word, next);
            for (int b = 0; b < 4 && produced < end; b++, produced++)
                if (produced >= start)
                    dest[produced - start] = word[b];
        }
    }
}
