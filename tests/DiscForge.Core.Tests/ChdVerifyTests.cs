// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// chd-verify checks a CHD's integrity — every hunk's map CRC-16 and the whole-image SHA-1 — without
/// extracting it. The verdicts are cross-validated in-cloud against chdman (a valid CHD verifies, a
/// corrupted one fails, and DiscForge's own writer produces a CHD chdman verifies with the same SHA-1).
/// These CI tests round-trip through DiscForge's own writer: a freshly written CHD verifies VALID, a
/// flipped hunk byte is caught, and a trashed header is UNSUPPORTED rather than mislabelled corrupt.
/// </summary>
public class ChdVerifyTests
{
    private static byte[] MakeRaw(int hunks)
    {
        var raw = new byte[hunks * 4096];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)((i * 37 + 11) % 256);
        return raw;
    }

    [Fact]
    public void A_freshly_written_chd_verifies_valid()
    {
        var chd = ChdWriter.CreateHd(MakeRaw(4));
        var r = ChdVerify.Check(chd);
        Assert.Equal(ChdVerifyVerdict.Valid, r.Verdict);
        Assert.True(r.Ok);
        Assert.Equal(5, r.Version);
    }

    [Fact]
    public void A_corrupted_hunk_is_detected()
    {
        var chd = ChdWriter.CreateHd(MakeRaw(4));
        chd[chd.Length / 2] ^= 0xFF;              // flip a byte in the compressed hunk data
        var r = ChdVerify.Check(chd);
        Assert.False(r.Ok);
        Assert.Equal(ChdVerifyVerdict.Corrupt, r.Verdict);
    }

    [Fact]
    public void A_trashed_header_is_unsupported_not_corrupt()
    {
        var chd = ChdWriter.CreateHd(MakeRaw(2));
        chd[0] ^= 0xFF;                            // break the 'MComprHD' magic
        var r = ChdVerify.Check(chd);
        Assert.Equal(ChdVerifyVerdict.Unsupported, r.Verdict);
        Assert.False(r.Ok);
    }
}
