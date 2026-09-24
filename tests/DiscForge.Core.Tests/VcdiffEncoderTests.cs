// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Patch;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// VcdiffEncoder round-trips through VcdiffPatch. (Its output was also checked against xdelta3 3.0.11's
/// own decoder — `xdelta3 -d` — on 300–360 MB images with scattered edits, a 120 MB insertion, a 90 MB
/// deletion and a moved block; that check needs xdelta3 installed, so it isn't part of this suite.)
/// Small window/span sizes here force the multi-window, sliding-slice and relocation paths on
/// test-sized data.
/// </summary>
public class VcdiffEncoderTests
{
    private static byte[] Random(int seed, int n)
    {
        var b = new byte[n];
        new System.Random(seed).NextBytes(b);
        return b;
    }

    private static void RoundTrip(byte[] source, byte[] target, int window = VcdiffEncoder.DefaultWindowSize,
        int span = VcdiffEncoder.DefaultSourceSpan, int? maxPatch = null)
    {
        var patch = VcdiffEncoder.Create(source, target, window, span);
        var info = VcdiffPatch.Inspect(patch);
        Assert.True(info.Supported);
        Assert.True(info.HasChecksums);
        Assert.Equal(target.Length, info.TargetSize);
        Assert.Equal(target, VcdiffPatch.Apply(patch, source));
        if (maxPatch is { } m) Assert.True(patch.Length <= m, $"patch is {patch.Length:N0} bytes, expected <= {m:N0}");
    }

    [Fact]
    public void Scattered_in_place_edits_make_a_small_patch()
    {
        var src = Random(1, 2_000_000);
        var tgt = (byte[])src.Clone();
        var rnd = new System.Random(2);
        for (int i = 0; i < 50; i++) rnd.NextBytes(tgt.AsSpan(rnd.Next(tgt.Length - 64), 32));
        RoundTrip(src, tgt, maxPatch: 50 * 32 + 4000);
    }

    [Fact]
    public void Identical_files_are_nearly_free()
    {
        var src = Random(3, 500_000);
        RoundTrip(src, src, maxPatch: 100);
    }

    [Fact]
    public void Runs_and_repeats_inside_the_target_are_used()
    {
        var tgt = new byte[300_000];
        Array.Fill(tgt, (byte)0xFF, 0, 100_000);
        var phrase = "DiscForge VCDIFF encoder "u8.ToArray();
        for (int i = 100_000; i < tgt.Length; i++) tgt[i] = phrase[i % phrase.Length];
        RoundTrip(Array.Empty<byte>(), tgt, maxPatch: 1000);
    }

    [Fact]
    public void Insertion_and_deletion_shift_within_the_slice()
    {
        var src = Random(4, 1_500_000);
        var tgt = src[..200_000].Concat(Random(5, 3000)).Concat(src[200_000..900_000]).Concat(src[950_000..]).ToArray();
        RoundTrip(src, tgt, window: 64 * 1024, span: 256 * 1024, maxPatch: 3000 + 8000);
    }

    [Fact]
    public void Data_moved_beyond_the_slice_is_relocated_through_the_anchor_index()
    {
        // 400 KB inserted at the front, with a 64 KB slice: the rest of the target sits ~400 KB past
        // where the slice would look, so only the anchor index can find it again.
        var src = Random(6, 2_000_000);
        var tgt = Random(7, 400_000).Concat(src).ToArray();
        RoundTrip(src, tgt, window: 16 * 1024, span: 64 * 1024, maxPatch: 400_000 + 60_000);
    }

    [Fact]
    public void Multi_window_unrelated_data_still_round_trips()
    {
        RoundTrip(Random(8, 300_000), Random(9, 350_000), window: 32 * 1024, span: 64 * 1024);
    }

    [Fact]
    public void Empty_target_is_refused()
    {
        Assert.Throws<ArgumentException>(() => VcdiffEncoder.Create(Random(1, 10), Array.Empty<byte>()));
    }
}
