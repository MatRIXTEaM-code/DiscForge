// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for <see cref="RawSubchannel.CompareRawAndCorrected"/> — cross-checking a raw interleaved
/// P-W capture against the drive's own corrected (de-interleaved) capture of the same range, the
/// "raw + corrected" half of full sub-channel capture. Built with <see cref="SubcodeFrame"/> so the
/// two representations author from the exact same Q content, the same way a real drive's two
/// capture modes describe the same physical sub-channel two different ways.
/// </summary>
public class RawSubchannelCompareTests
{
    private static (byte[] raw, byte[] corrected) AuthorMatching(int sectors)
    {
        var raw = new byte[sectors * 96];
        var corrected = new byte[sectors * 96];
        for (int s = 0; s < sectors; s++)
        {
            var q = SubQ.Position(QControl.Data, 1, 1, Msf.FromSectors(s), Msf.FromSectors(s + 150));
            var frame = new SubcodeFrame { P = false, Q = q };
            frame.EmitInterleaved96(raw.AsSpan(s * 96, 96));
            frame.EmitPacked96(corrected.AsSpan(s * 96, 96));
        }
        return (raw, corrected);
    }

    [Fact]
    public void Matching_raw_and_corrected_captures_report_full_agreement()
    {
        var (raw, corrected) = AuthorMatching(50);
        var cmp = RawSubchannel.CompareRawAndCorrected(raw, corrected, 50);

        Assert.Equal(50, cmp.SectorsCompared);
        Assert.Equal(50, cmp.QAgree);
        Assert.Equal(0, cmp.QDisagree);
        Assert.Equal(0, cmp.ValidityFlips);
        Assert.Equal(50, cmp.RawQValid);
        Assert.Equal(50, cmp.CorrectedQValid);
        Assert.Empty(cmp.DisagreeingSectors);
        Assert.Contains("agree", cmp.Summary);
    }

    [Fact]
    public void A_byte_level_disagreement_that_stays_CRC_valid_is_not_counted_as_a_validity_flip()
    {
        var (raw, corrected) = AuthorMatching(10);
        // Corrupt sector 3's corrected Q address (byte within the packed Q block, offset 12..23)
        // and recompute its CRC, so it's a different — but still CRC-valid — Q frame.
        int qOffset = 3 * 96 + 12 + 3; // packed Q byte 3 (track/index area)
        corrected[qOffset] ^= 0x01;
        var crcAt = 3 * 96 + 12;
        var crc = Crc16.ComputeInverted(corrected.AsSpan(crcAt, 10));
        corrected[crcAt + 10] = (byte)(crc >> 8);
        corrected[crcAt + 11] = (byte)crc;

        var cmp = RawSubchannel.CompareRawAndCorrected(raw, corrected, 10);
        Assert.Equal(1, cmp.QDisagree);
        Assert.Equal(0, cmp.ValidityFlips);
        Assert.Equal(new long[] { 3 }, cmp.DisagreeingSectors);
        Assert.Equal(10, cmp.RawQValid);
        Assert.Equal(10, cmp.CorrectedQValid);
    }

    [Fact]
    public void A_corrupted_CRC_in_only_one_capture_is_counted_as_a_validity_flip()
    {
        var (raw, corrected) = AuthorMatching(10);
        // Flip a byte in the corrected Q WITHOUT fixing its CRC — raw stays valid, corrected doesn't.
        corrected[5 * 96 + 12 + 4] ^= 0xFF;

        var cmp = RawSubchannel.CompareRawAndCorrected(raw, corrected, 10);
        Assert.Equal(1, cmp.QDisagree);
        Assert.Equal(1, cmp.ValidityFlips);
        Assert.Equal(10, cmp.RawQValid);
        Assert.Equal(9, cmp.CorrectedQValid);
        Assert.Contains("flip", cmp.Summary);
    }

    [Fact]
    public void Mismatched_lengths_are_refused()
    {
        var (raw, corrected) = AuthorMatching(10);
        Assert.Throws<ArgumentException>(() => RawSubchannel.CompareRawAndCorrected(raw, corrected, 20));
        Assert.Throws<ArgumentException>(() => RawSubchannel.CompareRawAndCorrected(raw[..(9 * 96)], corrected, 10));
    }
}
