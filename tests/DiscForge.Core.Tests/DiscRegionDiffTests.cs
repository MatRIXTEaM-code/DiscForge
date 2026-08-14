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
/// Tests for the region-level (shift-tolerant) disc diff: identical images are recognised, a localized
/// change reports as a small number of regions with high similarity, and — the key property — an insertion
/// does not cascade into a whole-file difference the way a byte diff would, because content-defined
/// chunking realigns after the shift.
/// </summary>
public class DiscRegionDiffTests
{
    private static byte[] Rand(int n, int s)
    {
        var b = new byte[n];
        new Random(s).NextBytes(b);
        return b;
    }

    [Fact]
    public void Identical_images_are_recognised()
    {
        var a = Rand(200_000, 5);
        Assert.True(DiscRegionDiff.Compare(a, (byte[])a.Clone()).Identical);
    }

    [Fact]
    public void A_localized_change_reports_as_a_few_regions_with_high_similarity()
    {
        var a = Rand(500_000, 5);
        var b = (byte[])a.Clone();
        Rand(20_000, 9).CopyTo(b, 250_000);

        var d = DiscRegionDiff.Compare(a, b);
        Assert.False(d.Identical);
        Assert.True(d.SimilarityA > 0.90, $"similarity {d.SimilarityA:P1}");
        Assert.True(d.RegionsA.Count <= 3);
        Assert.Contains(d.RegionsA, r => r.Offset < 260_000 && r.Offset + r.Length > 250_000);
    }

    [Fact]
    public void An_insertion_does_not_cascade_into_a_whole_file_difference()
    {
        var a = Rand(500_000, 11);
        var inserted = Rand(10_000, 3).Concat(a).ToArray();
        var d = DiscRegionDiff.Compare(a, inserted);
        Assert.True(d.SimilarityA > 0.90, $"expected shift-tolerance, similarity {d.SimilarityA:P1}");
    }
}
