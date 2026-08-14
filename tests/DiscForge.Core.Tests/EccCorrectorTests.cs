// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

public class EccCorrectorTests
{
    /// <summary>A valid Mode 1 sector: sync, header, pseudo-random user data,
    /// and real EDC/ECC computed by the encoder.</summary>
    private static byte[] GoodSector(int seed = 1)
    {
        var s = new byte[2352];
        s[0] = 0x00;
        for (int i = 1; i <= 10; i++) s[i] = 0xFF;
        s[11] = 0x00;
        s[12] = 0x00; s[13] = 0x02; s[14] = 0x00; s[15] = 0x01;   // MSF + Mode 1

        var rng = new Random(seed);
        for (int i = 16; i < 2064; i++) s[i] = (byte)rng.Next(256);

        EdcEcc.FillMode1(s);
        return s;
    }

    [Fact]
    public void An_intact_sector_is_left_alone()
    {
        var s = GoodSector();
        var original = (byte[])s.Clone();

        var r = EccCorrector.CorrectMode1(s, Array.Empty<int>());

        Assert.True(r.Success);
        Assert.Equal(0, r.BytesCorrected);
        Assert.Equal(original, s);
    }

    [Fact]
    public void A_single_flagged_byte_is_rebuilt_from_parity()
    {
        var s = GoodSector();
        var original = (byte[])s.Clone();

        s[1000] ^= 0x5A;

        var r = EccCorrector.CorrectMode1(s, new[] { 1000 });

        Assert.True(r.Success);
        Assert.Equal(original, s);
    }

    [Fact]
    public void Two_erasures_in_one_codeword_are_both_recovered()
    {
        // Offsets 12 and 12+86 are row 0 and row 1 of the same P codeword —
        // exactly the two-erasure case the code is dimensioned for.
        var s = GoodSector();
        var original = (byte[])s.Clone();

        s[12] ^= 0xFF;
        s[12 + 86] ^= 0x33;

        var r = EccCorrector.CorrectMode1(s, new[] { 12, 12 + 86 });

        Assert.True(r.Success);
        Assert.Equal(original, s);
    }

    [Fact]
    public void A_hundred_byte_burst_is_fully_corrected()
    {
        // The case that justifies the interleave. A scratch this size would be
        // hopeless in a single codeword; spread across 86 of them it is not.
        var s = GoodSector(7);
        var original = (byte[])s.Clone();

        var erasures = new List<int>();
        for (int i = 500; i < 600; i++)
        {
            s[i] ^= (byte)(i * 31 + 7);
            erasures.Add(i);
        }

        var r = EccCorrector.CorrectMode1(s, erasures);

        Assert.True(r.Success);
        Assert.Equal(original, s);
        Assert.Empty(r.StillUncertain);
    }

    [Fact]
    public void Scattered_damage_across_the_sector_is_corrected()
    {
        var s = GoodSector(11);
        var original = (byte[])s.Clone();

        var erasures = new List<int>();
        for (int i = 100; i < 2000; i += 97)
        {
            s[i] ^= 0xC3;
            erasures.Add(i);
        }

        var r = EccCorrector.CorrectMode1(s, erasures);

        Assert.True(r.Success);
        Assert.Equal(original, s);
    }

    [Fact]
    public void Damaged_sync_bytes_are_restored_from_the_known_pattern()
    {
        // Sync carries no parity, but it is identical on every sector ever
        // pressed — so it is repaired by knowing rather than by decoding.
        var s = GoodSector();
        var original = (byte[])s.Clone();

        s[0] ^= 0xFF;
        s[5] ^= 0xFF;

        var r = EccCorrector.CorrectMode1(s, new[] { 0, 5 });

        Assert.True(r.Success);
        Assert.Equal(original, s);
    }

    [Fact]
    public void An_unflagged_single_error_is_still_caught()
    {
        // C2 under-reports; a byte the drive silently got wrong is exactly what
        // the unflagged-error path exists for.
        var s = GoodSector(3);
        var original = (byte[])s.Clone();

        s[777] ^= 0x0F;

        var r = EccCorrector.CorrectMode1(s, Array.Empty<int>());

        Assert.True(r.Success);
        Assert.Equal(original, s);
    }

    [Fact]
    public void Unflagged_correction_can_be_declined()
    {
        var s = GoodSector(3);
        s[777] ^= 0x0F;

        var r = EccCorrector.CorrectMode1(s, Array.Empty<int>(),
                                          correctUnflaggedErrors: false);

        Assert.False(r.Success);
        Assert.Equal(0, r.BytesCorrected);
    }

    [Fact]
    public void Damage_too_concentrated_for_P_alone_falls_to_Q()
    {
        // Four bytes 86 apart all land in the same P codeword — twice what two
        // parity symbols can resolve there. But they sit on four different Q
        // diagonals, so Q takes them one apiece.
        //
        // This is the P/Q product code earning its keep: neither code alone
        // covers this damage, and alternating between them does. The test was
        // originally written expecting failure, and the decoder was right.
        var s = GoodSector(5);
        var original = (byte[])s.Clone();

        var erasures = new List<int>();
        for (int k = 0; k < 4; k++)
        {
            int off = 12 + 86 * k;
            s[off] ^= 0x77;
            erasures.Add(off);
        }

        var r = EccCorrector.CorrectMode1(s, erasures);

        Assert.True(r.Success);
        Assert.Equal(original, s);
    }

    [Fact]
    public void Damage_beyond_both_codes_is_reported_rather_than_guessed()
    {
        // To exceed both codes at once the damage has to be dense enough that
        // some codeword carries three erasures in every direction. A solid run
        // through the user data does it, and the decoder must say so rather
        // than inventing an answer.
        var s = GoodSector(5);
        var rng = new Random(4242);

        var erasures = new List<int>();
        for (int i = 16; i < 1500; i++)
        {
            s[i] ^= (byte)rng.Next(1, 256);
            erasures.Add(i);
        }

        var r = EccCorrector.CorrectMode1(s, erasures);

        Assert.False(r.Success);
        Assert.False(r.EdcValid);
        Assert.NotEmpty(r.StillUncertain);
    }

    [Fact]
    public void False_alarms_are_cleared_without_altering_the_data()
    {
        // A drive that flags bytes it actually read correctly must not cause a
        // "correction" — and clearing the flag matters, because it frees
        // capacity for codewords with real damage.
        var s = GoodSector(9);
        var original = (byte[])s.Clone();

        var r = EccCorrector.CorrectMode1(s, new[] { 300, 301, 302, 900 });

        Assert.True(r.Success);
        Assert.Equal(original, s);
        Assert.Empty(r.StillUncertain);
    }

    [Fact]
    public void Correction_terminates_on_a_sector_it_cannot_fix()
    {
        var s = GoodSector(13);
        var rng = new Random(99);

        var erasures = new List<int>();
        for (int i = 16; i < 1200; i++)
        {
            s[i] ^= (byte)rng.Next(1, 256);
            erasures.Add(i);
        }

        var r = EccCorrector.CorrectMode1(s, erasures);

        Assert.False(r.Success);
        Assert.True(r.PassesUsed <= 8);
    }

    // ---- Mode 2 Form 1 (CD-XA / PlayStation data) --------------------------

    /// <summary>A valid Mode 2 Form 1 sector: sync, a distinctive header address,
    /// a Form 1 sub-header, pseudo-random user data, and real EDC/ECC.</summary>
    private static byte[] GoodXaSector(int seed = 1)
    {
        var s = new byte[2352];
        s[0] = 0x00;
        for (int i = 1; i <= 10; i++) s[i] = 0xFF;
        s[11] = 0x00;
        s[12] = 0x12; s[13] = 0x34; s[14] = 0x56; s[15] = 0x02;   // address + Mode 2
        s[16] = 0; s[17] = 0; s[18] = 0x08; s[19] = 0;            // sub-header: data, Form 1
        s[20] = 0; s[21] = 0; s[22] = 0x08; s[23] = 0;

        var rng = new Random(seed);
        for (int i = 24; i < 2072; i++) s[i] = (byte)rng.Next(256);

        EdcEcc.FillMode2Form1(s);
        return s;
    }

    [Fact]
    public void An_intact_xa_sector_is_left_alone()
    {
        var s = GoodXaSector();
        var original = (byte[])s.Clone();

        var r = EccCorrector.CorrectMode2Form1(s, Array.Empty<int>());

        Assert.True(r.Success);
        Assert.Equal(0, r.BytesCorrected);
        Assert.Equal(original, s);
    }

    [Fact]
    public void A_flagged_xa_byte_is_rebuilt_from_parity()
    {
        var s = GoodXaSector();
        var original = (byte[])s.Clone();

        s[900] ^= 0x5A;

        var r = EccCorrector.CorrectMode2Form1(s, new[] { 900 });

        Assert.True(r.Success);
        Assert.Equal(original, s);
    }

    [Fact]
    public void An_unflagged_xa_error_is_still_caught()
    {
        var s = GoodXaSector(3);
        var original = (byte[])s.Clone();

        s[1234] ^= 0x0F;

        var r = EccCorrector.CorrectMode2Form1(s, Array.Empty<int>());

        Assert.True(r.Success);
        Assert.Equal(original, s);
    }

    [Fact]
    public void An_xa_burst_is_fully_corrected_and_the_address_survives()
    {
        var s = GoodXaSector(7);
        var original = (byte[])s.Clone();

        var erasures = new List<int>();
        for (int i = 500; i < 600; i++)
        {
            s[i] ^= (byte)(i * 31 + 7);
            erasures.Add(i);
        }

        var r = EccCorrector.CorrectMode2Form1(s, erasures);

        Assert.True(r.Success);
        Assert.Equal(original, s);
        // the header address, excluded from ECC, is untouched by the repair
        Assert.Equal(0x12, s[12]);
        Assert.Equal(0x34, s[13]);
        Assert.Equal(0x56, s[14]);
    }

    [Fact]
    public void A_corrupt_xa_address_is_ignored_because_it_carries_no_ecc()
    {
        // The XA header is outside both the ECC and the EDC. A wrong address is
        // therefore invisible to this check — the decoder must not invent a
        // "correction" for it, and the sector still validates on its data.
        var s = GoodXaSector(5);
        s[13] ^= 0xFF;                              // corrupt the address only

        var r = EccCorrector.CorrectMode2Form1(s, Array.Empty<int>());

        Assert.True(r.Success);
        Assert.Equal(0, r.BytesCorrected);
    }
}