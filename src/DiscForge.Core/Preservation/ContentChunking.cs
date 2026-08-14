// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text;

namespace DiscForge.Core.Preservation;

/// <summary>One content-defined chunk: where it sits in the file, its length, and its SHA-256.</summary>
public sealed record Chunk(long Offset, int Length, byte[] Sha256);

/// <summary>A file reduced to an ordered list of content-defined chunks plus a Merkle root over them —
/// the manifest from which the file is exactly reconstructable, and against which reconstruction is
/// provable (replay the chunks, verify each hash, verify the root and the whole-file hash).</summary>
public sealed record ChunkManifest
{
    public string? Name { get; init; }
    public required long FileLength { get; init; }
    public required byte[] FileSha256 { get; init; }
    public required IReadOnlyList<Chunk> Chunks { get; init; }
    public required byte[] MerkleRoot { get; init; }

    public int ChunkCount => Chunks.Count;
    public double AvgChunkSize => Chunks.Count == 0 ? 0 : FileLength / (double)Chunks.Count;
    public string RootHex => System.Convert.ToHexString(MerkleRoot).ToLowerInvariant();
}

/// <summary>What a collection dedups to once split into content-defined chunks.</summary>
public sealed record ChunkDedupReport
{
    public required IReadOnlyList<ChunkManifest> Files { get; init; }
    public required int TotalChunks { get; init; }
    public required int UniqueChunks { get; init; }
    public required long TotalBytes { get; init; }
    public required long UniqueBytes { get; init; }

    /// <summary>How much smaller the deduplicated store is than storing every file whole.</summary>
    public double DedupRatio => UniqueBytes == 0 ? 1 : TotalBytes / (double)UniqueBytes;

    public string Summary() =>
        $"{Files.Count} file(s) → {TotalChunks:N0} chunk(s), {UniqueChunks:N0} unique " +
        $"({TotalBytes:N0} B → {UniqueBytes:N0} B, {DedupRatio:0.00}× smaller).";
}

/// <summary>
/// chunk-manifest — split files with FastCDC content-defined chunking, so a collection dedups to unique
/// chunks (not whole files) and near-identical images that differ by a few sectors still collapse. Each
/// file becomes an ordered chunk list under a Merkle root; reconstruction is provable — replay the chunks,
/// verify each SHA-256 and that the concatenation matches the recorded whole-file hash and Merkle root.
/// Because chunk boundaries are content-derived, a one-byte insertion re-chunks only locally instead of
/// shifting every boundary (the failure mode of fixed-block dedup). Read/verify preservation tooling only.
/// </summary>
public static class ContentChunking
{
    // FastCDC parameters (bytes). Boundaries are decided by content, so these bound — not fix — chunk size.
    public const int MinSize = 2 * 1024;
    public const int AvgSize = 8 * 1024;
    public const int MaxSize = 64 * 1024;

    // Gear table: 256 stable pseudo-random 64-bit values (SplitMix64 from a fixed seed — reproducible).
    private static readonly ulong[] Gear = BuildGear(0x1234567890ABCDEFUL);

    // Normalized-chunking masks: a harder mask before the average size (resists cutting early), an easier
    // one after (encourages cutting), which concentrates chunk sizes near the average.
    private static readonly int AvgBits = (int)Math.Log2(AvgSize);         // 13 for 8 KiB
    private static readonly ulong MaskS = (1UL << (AvgBits + 2)) - 1;      // 15 low bits — harder cut
    private static readonly ulong MaskL = (1UL << (AvgBits - 2)) - 1;      // 11 low bits — easier cut

    /// <summary>Find the next content-defined cut point within <paramref name="data"/> starting at 0.</summary>
    public static int NextCut(ReadOnlySpan<byte> data)
    {
        int n = data.Length;
        if (n <= MinSize) return n;
        if (n > MaxSize) n = MaxSize;

        int normal = Math.Min(AvgSize, n);
        ulong fp = 0;
        int i = MinSize;
        // Region 1: [MinSize, AvgSize) with the harder mask.
        for (; i < normal; i++)
        {
            fp = (fp << 1) + Gear[data[i]];
            if ((fp & MaskS) == 0) return i;
        }
        // Region 2: [AvgSize, n) with the easier mask.
        for (; i < n; i++)
        {
            fp = (fp << 1) + Gear[data[i]];
            if ((fp & MaskL) == 0) return i;
        }
        return n;
    }

    /// <summary>Split a buffer into content-defined chunks with their SHA-256 hashes.</summary>
    public static IReadOnlyList<Chunk> Split(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var chunks = new List<Chunk>();
        int pos = 0;
        while (pos < data.Length)
        {
            int len = NextCut(data.AsSpan(pos));
            if (len <= 0) len = Math.Min(MaxSize, data.Length - pos);
            chunks.Add(new Chunk(pos, len, SHA256.HashData(data.AsSpan(pos, len))));
            pos += len;
        }
        return chunks;
    }

    public static ChunkManifest BuildManifest(string? name, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var chunks = Split(data);
        return new ChunkManifest
        {
            Name = name,
            FileLength = data.LongLength,
            FileSha256 = SHA256.HashData(data),
            Chunks = chunks,
            MerkleRoot = MerkleRoot(chunks.Select(c => c.Sha256).ToList()),
        };
    }

    public static ChunkManifest BuildManifestFromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return BuildManifest(Path.GetFileName(path), File.ReadAllBytes(path));
    }

    /// <summary>Dedup a set of files by content-defined chunk: total vs unique chunks and bytes.</summary>
    public static ChunkDedupReport Dedup(IEnumerable<(string Name, byte[] Data)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var manifests = new List<ChunkManifest>();
        var unique = new Dictionary<string, int>();   // chunk hash -> length
        int total = 0;
        long totalBytes = 0;
        foreach (var (name, data) in files)
        {
            var m = BuildManifest(name, data);
            manifests.Add(m);
            foreach (var c in m.Chunks)
            {
                total++;
                totalBytes += c.Length;
                unique.TryAdd(System.Convert.ToHexString(c.Sha256), c.Length);
            }
        }
        return new ChunkDedupReport
        {
            Files = manifests,
            TotalChunks = total,
            UniqueChunks = unique.Count,
            TotalBytes = totalBytes,
            UniqueBytes = unique.Values.Sum(v => (long)v),
        };
    }

    /// <summary>Prove a manifest reconstructs its file: fetch each chunk by hash from <paramref name="store"/>,
    /// verify the chunk's own SHA-256, and that the concatenation matches the recorded whole-file hash and
    /// Merkle root. <paramref name="failIndex"/> is the first bad chunk, or -1 on success.</summary>
    public static bool VerifyReconstruction(ChunkManifest manifest, Func<byte[], byte[]?> store, out int failIndex)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(store);
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int k = 0; k < manifest.Chunks.Count; k++)
        {
            var c = manifest.Chunks[k];
            byte[]? bytes = store(c.Sha256);
            if (bytes is null || bytes.Length != c.Length ||
                !SHA256.HashData(bytes).AsSpan().SequenceEqual(c.Sha256))
            {
                failIndex = k;
                return false;
            }
            whole.AppendData(bytes);
        }
        failIndex = -1;
        bool rootOk = MerkleRoot(manifest.Chunks.Select(c => c.Sha256).ToList()).AsSpan().SequenceEqual(manifest.MerkleRoot);
        bool wholeOk = whole.GetHashAndReset().AsSpan().SequenceEqual(manifest.FileSha256);
        return rootOk && wholeOk;
    }

    /// <summary>A binary Merkle root over the chunk hashes (leaf = H(0x00‖hash), node = H(0x01‖L‖R); an odd
    /// node is promoted unchanged). Empty input hashes the empty string.</summary>
    public static byte[] MerkleRoot(IReadOnlyList<byte[]> leaves)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        if (leaves.Count == 0) return SHA256.HashData(Array.Empty<byte>());

        var level = leaves.Select(h =>
        {
            var b = new byte[1 + h.Length];
            b[0] = 0x00;
            h.CopyTo(b, 1);
            return SHA256.HashData(b);
        }).ToList();

        while (level.Count > 1)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (int i = 0; i < level.Count; i += 2)
            {
                if (i + 1 == level.Count) { next.Add(level[i]); break; }
                var b = new byte[1 + level[i].Length + level[i + 1].Length];
                b[0] = 0x01;
                level[i].CopyTo(b, 1);
                level[i + 1].CopyTo(b, 1 + level[i].Length);
                next.Add(SHA256.HashData(b));
            }
            level = next;
        }
        return level[0];
    }

    private static ulong[] BuildGear(ulong seed)
    {
        var g = new ulong[256];
        ulong x = seed;
        for (int i = 0; i < 256; i++)
        {
            // SplitMix64
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            g[i] = z ^ (z >> 31);
        }
        return g;
    }

    public static string Render(ChunkDedupReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder(r.Summary());
        foreach (var f in r.Files)
            sb.Append($"\n  {f.Name}: {f.ChunkCount} chunk(s), avg {f.AvgChunkSize:N0} B, root {f.RootHex[..12]}…");
        return sb.ToString();
    }
}
