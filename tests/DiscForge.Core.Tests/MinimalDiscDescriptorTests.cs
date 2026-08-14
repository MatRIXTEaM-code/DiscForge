// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The minimal disc descriptor factors an image into fill runs, duplicate sectors and unique
/// content, and the descriptor must reconstruct the original byte-for-byte — that round trip is the
/// proof it is COMPLETE (a lossy or wrong factoring would fail to rebuild). Also checks the accounting:
/// unique/duplicate/fill counts and the irreducible-content figure.
/// </summary>
public class MinimalDiscDescriptorTests
{
    private const int SS = 2048;

    private static byte[] Sector(int seed)
    {
        var s = new byte[SS];
        new Random(seed).NextBytes(s);
        return s;
    }

    private static byte[] Fill(byte v)
    {
        var s = new byte[SS];
        Array.Fill(s, v);
        return s;
    }

    private static byte[] Concat(params byte[][] sectors)
    {
        var outp = new byte[sectors.Length * SS];
        for (int i = 0; i < sectors.Length; i++) sectors[i].CopyTo(outp, i * SS);
        return outp;
    }

    [Fact]
    public void Factors_fill_duplicate_and_unique_and_counts_them()
    {
        var a = Sector(1);
        var b = Sector(2);
        // zero, zero, A, B, A(dup), 0xFF fill  → 2 unique, 1 duplicate, 3 fill (2×0x00 + 1×0xFF)
        var image = Concat(Fill(0x00), Fill(0x00), a, b, a, Fill(0xFF));

        var r = MinimalDiscDescriptor.Analyze(image, SS);

        Assert.Equal(6, r.TotalSectors);
        Assert.Equal(2, r.UniqueSectors);
        Assert.Equal(1, r.DuplicateSectors);
        Assert.Equal(3, r.FillSectors);
        Assert.Equal(2 * SS, r.UniqueBytes);          // only A and B are irreducible
        // Fill breakdown: 0x00 appears twice, 0xFF once.
        Assert.Equal((byte)0x00, r.FillBreakdown[0].Value);
        Assert.Equal(2, r.FillBreakdown[0].Sectors);
    }

    [Fact]
    public void The_descriptor_reconstructs_the_image_byte_for_byte()
    {
        var image = Concat(Fill(0x00), Sector(10), Sector(11), Sector(10), Fill(0xFF), Fill(0xFF), Sector(12));
        var r = MinimalDiscDescriptor.Analyze(image, SS);

        Assert.Equal(image, MinimalDiscDescriptor.Reconstruct(r));
    }

    [Fact]
    public void Consecutive_fills_of_the_same_value_coalesce_into_one_run()
    {
        var image = Concat(Fill(0x00), Fill(0x00), Fill(0x00), Sector(5));
        var r = MinimalDiscDescriptor.Analyze(image, SS);

        // One Fill op of run 3, then one Unique.
        Assert.Equal(2, r.Ops.Count);
        Assert.Equal(MddOpKind.Fill, r.Ops[0].Kind);
        Assert.Equal(3, r.Ops[0].SectorRun);
        Assert.Equal(MddOpKind.Unique, r.Ops[1].Kind);
        Assert.Equal(image, MinimalDiscDescriptor.Reconstruct(r));
    }

    [Fact]
    public void An_all_unique_image_has_no_reduction_but_still_round_trips()
    {
        var image = Concat(Sector(21), Sector(22), Sector(23));
        var r = MinimalDiscDescriptor.Analyze(image, SS);

        Assert.Equal(3, r.UniqueSectors);
        Assert.Equal(0, r.DuplicateSectors);
        Assert.Equal(0, r.FillSectors);
        Assert.True(r.ReductionRatio <= 0.01);        // essentially none (tiny op overhead aside)
        Assert.Equal(image, MinimalDiscDescriptor.Reconstruct(r));
    }

    [Fact]
    public void A_mostly_empty_image_reduces_almost_entirely()
    {
        // 1000 zero sectors + one real sector → almost all fill.
        var sectors = new List<byte[]>();
        for (int i = 0; i < 1000; i++) sectors.Add(Fill(0x00));
        sectors.Add(Sector(99));
        var image = Concat(sectors.ToArray());

        var r = MinimalDiscDescriptor.Analyze(image, SS);
        Assert.Equal(1000, r.FillSectors);
        Assert.Equal(1, r.UniqueSectors);
        Assert.True(r.ReductionRatio > 0.99);
        Assert.Equal(image, MinimalDiscDescriptor.Reconstruct(r));
    }

    [Fact]
    public void A_non_sector_multiple_length_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => MinimalDiscDescriptor.Analyze(new byte[SS + 1], SS));
    }
}
