// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// ps2mc-ecc verifies and repairs the per-page Hamming ECC of a PS2 memory-card dump. The ECC math is
/// cross-validated in-cloud against the independent mymcplus reference (identical output on thousands of
/// random blocks and identical single-bit repairs); these CI tests exercise the round-trip: a card whose
/// ECC is self-consistent reads CLEAN, a single flipped data bit is detected and repaired back to the
/// original bytes, and two bit-flips in one chunk are reported as uncorrectable.
/// </summary>
public class Ps2CardEccTests
{
    private const int Clusters = 16, PagesPerCluster = 2, PhysPage = 528;

    /// <summary>Build a small "with-ECC" PS2 card whose spare bytes hold correct Hamming codes.</summary>
    private static byte[] BuildCard()
    {
        int totalPages = Clusters * PagesPerCluster;
        var card = new byte[totalPages * PhysPage];
        var rnd = new Random(1234);
        Span<byte> ecc = stackalloc byte[3];
        for (int p = 0; p < totalPages; p++)
        {
            int baseOff = p * PhysPage;
            rnd.NextBytes(card.AsSpan(baseOff, 512));
            if (p == 0)
            {
                Encoding.ASCII.GetBytes("Sony PS2 Memory Card Format ").CopyTo(card, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(card.AsSpan(baseOff + 0x28), 512);
                BinaryPrimitives.WriteUInt16LittleEndian(card.AsSpan(baseOff + 0x2A), PagesPerCluster);
                BinaryPrimitives.WriteUInt32LittleEndian(card.AsSpan(baseOff + 0x30), Clusters);
            }
            for (int c = 0; c < 4; c++)
            {
                Ps2CardEcc.Calculate(card.AsSpan(baseOff + c * 128, 128), ecc);
                ecc.CopyTo(card.AsSpan(baseOff + 512 + c * 3, 3));
            }
        }
        return card;
    }

    [Fact]
    public void A_consistent_card_reads_clean()
    {
        var r = Ps2CardEcc.Verify(BuildCard());
        Assert.True(r.HasEcc);
        Assert.Equal(Ps2EccStatus.Clean, r.Status);
        Assert.Equal(Clusters * PagesPerCluster, r.CleanPages);
    }

    [Fact]
    public void A_single_bit_flip_is_corrected_back_to_the_original()
    {
        var original = BuildCard();
        var corrupted = (byte[])original.Clone();
        int off = 5 * PhysPage + 2 * 128 + 30;   // page 5, chunk 2
        corrupted[off] ^= 0x08;

        var (report, repaired) = Ps2CardEcc.Repair(corrupted);
        Assert.Equal(Ps2EccStatus.Corrected, report.Status);
        Assert.Equal(1, report.CorrectedPages);
        Assert.Equal(original, repaired);         // repaired byte-for-byte back to the original
    }

    [Fact]
    public void Two_bit_flips_in_one_chunk_are_uncorrectable()
    {
        var card = BuildCard();
        int b = 7 * PhysPage;
        card[b + 10] ^= 0x01;
        card[b + 40] ^= 0x02;                     // two errors in the same 128-byte chunk

        var r = Ps2CardEcc.Verify(card);
        Assert.Equal(Ps2EccStatus.Failed, r.Status);
        Assert.Equal(1, r.FailedPages);
    }

    [Fact]
    public void Verify_does_not_mutate_the_input()
    {
        var card = BuildCard();
        int off = 3 * PhysPage + 60;
        card[off] ^= 0x10;                        // introduce a correctable error
        var snapshot = (byte[])card.Clone();

        Ps2CardEcc.Verify(card);                  // read-only — must not fix it in place
        Assert.Equal(snapshot, card);
    }
}
