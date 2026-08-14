// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The DVD RS-PC product code (PI = RS(182,172) per row, PO = RS(208,192) per column) is validated software-first
/// by round-trip against DiscForge's own RS encoder: encode a block, injure it beyond the inner code, correct it,
/// and require the original data back. The tests pin the erasure boundary that Reed-Solomon theory predicts — the
/// 16 outer-parity rows recover up to 16 whole destroyed rows and honestly fail at 17. (The logical block layout
/// is validated here; mapping a real DVD dump's physical byte stream into it is out of scope, as documented.)
/// </summary>
public class DvdEccTests
{
    private static byte[] SampleData()
    {
        var d = new byte[DvdEcc.DataRows * DvdEcc.DataCols];
        for (int i = 0; i < d.Length; i++) d[i] = (byte)((i * 179 + 71) % 256);
        return d;
    }

    private static (bool corrected, bool matches) Injure(int destroyedRows, int scatter = 0)
    {
        var data = SampleData();
        var block = DvdEcc.EncodeBlock(data);
        int C = DvdEcc.Cols;
        for (int r = 40; r < 40 + destroyedRows; r++)
            for (int c = 0; c < C; c++) block[r * C + c] ^= 0xFF;
        for (int r = 0; r < scatter; r++) block[(r * 5) * C + (r * 11 % C)] ^= 0x33;

        var res = DvdEcc.Correct(block);
        var recovered = DvdEcc.ExtractData(block);
        return (res.Corrected, recovered.AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void A_clean_block_round_trips()
    {
        var data = SampleData();
        var block = DvdEcc.EncodeBlock(data);
        var res = DvdEcc.Correct(block);
        Assert.True(res.Corrected);
        Assert.Equal(data, DvdEcc.ExtractData(block));
    }

    [Fact]
    public void Recovers_destroyed_rows_plus_scattered_errors()
    {
        var (corrected, matches) = Injure(destroyedRows: 12, scatter: 10);
        Assert.True(corrected);
        Assert.True(matches);
    }

    [Fact]
    public void Recovers_exactly_the_outer_parity_capacity_of_erased_rows()
    {
        var (corrected, matches) = Injure(destroyedRows: DvdEcc.PoParity);   // 16
        Assert.True(corrected);
        Assert.True(matches);
    }

    [Fact]
    public void Honestly_fails_beyond_the_erasure_capacity()
    {
        var (corrected, matches) = Injure(destroyedRows: DvdEcc.PoParity + 1); // 17
        Assert.False(corrected);
        Assert.False(matches);
    }
}
