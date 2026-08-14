// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Raw;

/// <summary>
/// The CD-ROM sector scrambler (ECMA-130 Annex B): a 15-bit LFSR
/// (x^15 + x + 1, seeded 0x0001) whose output is XORed over bytes 12..2351 of
/// a data sector — everything after the 12-byte sync. Audio is never scrambled.
///
/// Why it exists here: when writing in RAW modes the drive's own encoder is
/// bypassed, so the HOST must supply data sectors already scrambled, exactly
/// as they sit on the disc surface. (Readers descramble transparently, which
/// is why stored images hold the unscrambled form.) XOR makes the operation
/// its own inverse — scrambling twice is the identity, which the tests use.
/// </summary>
public static class CdScrambler
{
    /// <summary>The 2340-byte XOR sequence for bytes 12..2351, precomputed.</summary>
    private static readonly byte[] Sequence = BuildSequence();

    private static byte[] BuildSequence()
    {
        var seq = new byte[2340];
        int lfsr = 0x0001;
        for (int i = 0; i < seq.Length; i++)
        {
            int b = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                b |= (lfsr & 1) << bit;                    // LSB first
                int fb = (lfsr ^ (lfsr >> 1)) & 1;         // x^15 + x + 1
                lfsr = (lfsr >> 1) | (fb << 14);
            }
            seq[i] = (byte)b;
        }
        return seq;
    }

    /// <summary>
    /// Scramble (or descramble — same operation) a full 2352-byte sector in
    /// place. The 12 sync bytes are left untouched.
    /// </summary>
    public static void ScrambleInPlace(Span<byte> sector2352)
    {
        if (sector2352.Length != 2352)
            throw new ArgumentException("A raw sector is 2352 bytes.", nameof(sector2352));
        for (int i = 0; i < 2340; i++)
            sector2352[12 + i] ^= Sequence[i];
    }
}
