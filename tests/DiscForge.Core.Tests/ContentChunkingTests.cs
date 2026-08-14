// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for FastCDC content-defined chunking + Merkle manifest: chunk-size bounds, the reconstruction
/// proof (including tamper detection), the shift-resistance that distinguishes CDC from fixed-block dedup,
/// and cross-file deduplication of a shared region.
/// </summary>
public class ContentChunkingTests
{
    private static byte[] Rand(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void Chunks_respect_the_size_bounds_and_average_near_target()
    {
        var m = ContentChunking.BuildManifest("a", Rand(1_000_000, 42));
        Assert.All(m.Chunks, c => Assert.True(c.Length <= ContentChunking.MaxSize));
        Assert.All(m.Chunks.Take(m.Chunks.Count - 1), c => Assert.True(c.Length >= ContentChunking.MinSize));
        Assert.InRange(m.AvgChunkSize, 4000, 16000);
    }

    [Fact]
    public void Reconstruction_verifies_and_a_tampered_chunk_is_caught()
    {
        var data = Rand(300_000, 7);
        var m = ContentChunking.BuildManifest("a", data);
        var store = m.Chunks.ToDictionary(
            c => System.Convert.ToHexString(c.Sha256),
            c => data[(int)c.Offset..((int)c.Offset + c.Length)]);
        byte[]? Get(byte[] h) => store.TryGetValue(System.Convert.ToHexString(h), out var b) ? b : null;

        Assert.True(ContentChunking.VerifyReconstruction(m, Get, out int fi));
        Assert.Equal(-1, fi);

        var key = System.Convert.ToHexString(m.Chunks[3].Sha256);
        var bad = (byte[])store[key].Clone(); bad[0] ^= 0xFF; store[key] = bad;
        Assert.False(ContentChunking.VerifyReconstruction(m, Get, out int fi2));
        Assert.Equal(3, fi2);
    }

    [Fact]
    public void Content_defined_boundaries_survive_a_one_byte_insertion()
    {
        var data = Rand(500_000, 11);
        var shifted = new byte[data.Length + 1];
        shifted[0] = 0x99;
        data.CopyTo(shifted, 1);

        var a = ContentChunking.BuildManifest("a", data);
        var b = ContentChunking.BuildManifest("b", shifted);
        var setA = a.Chunks.Select(c => System.Convert.ToHexString(c.Sha256)).ToHashSet();
        double shared = b.Chunks.Count(c => setA.Contains(System.Convert.ToHexString(c.Sha256))) / (double)b.Chunks.Count;
        Assert.True(shared > 0.9, $"expected >90% shared, got {shared:P0}");
    }

    [Fact]
    public void Two_files_sharing_a_region_deduplicate()
    {
        var common = Rand(400_000, 3);
        var f1 = common.Concat(Rand(80_000, 1)).ToArray();
        var f2 = Rand(60_000, 2).Concat(common).ToArray();
        var r = ContentChunking.Dedup(new[] { ("f1", f1), ("f2", f2) });
        Assert.True(r.UniqueChunks < r.TotalChunks);
        Assert.True(r.DedupRatio > 1.4, $"expected >1.4x, got {r.DedupRatio:0.00}");
    }

    [Fact]
    public void The_Merkle_root_changes_when_any_chunk_changes()
    {
        var a = ContentChunking.BuildManifest("a", Rand(100_000, 5));
        var d = Rand(100_000, 5); d[50_000] ^= 0xFF;
        var b = ContentChunking.BuildManifest("b", d);
        Assert.False(a.MerkleRoot.AsSpan().SequenceEqual(b.MerkleRoot));
    }
}
