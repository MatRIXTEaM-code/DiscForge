// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;

namespace DiscForge.Core.Preservation;

/// <summary>
/// A Merkle tree over an image's sectors: leaf i = SHA-256 of sector i's raw
/// bytes; parent = SHA-256(left ‖ right); an unpaired node at the end of a
/// level is PROMOTED unchanged (never duplicated — duplication lets an
/// attacker append a copy of the last sector without changing the root).
///
/// The root commits to every sector individually, which is the property the
/// Dump Certificate needs: given a 700 MB image whose root was signed at dump
/// time, any single 2352-byte slice can later be proven to be byte-identical
/// to what the drive delivered — using a proof of ~18 hashes — without
/// rehashing, or even possessing, the rest of the file.
/// </summary>
public static class SectorMerkle
{
    /// <summary>One step of an audit path: the sibling's hash and which side
    /// of the concatenation it takes.</summary>
    public sealed record PathStep(byte[] Hash, bool SiblingIsLeft);

    /// <summary>Hash every sector of <paramref name="image"/> into leaf hashes.
    /// Trailing bytes short of a full sector are ignored, matching how the rest
    /// of DiscForge treats truncated raw images.</summary>
    public static byte[][] LeafHashes(Stream image, int sectorSize = 2352)
    {
        long count = image.Length / sectorSize;
        var leaves = new byte[count][];
        var buf = new byte[sectorSize];
        image.Position = 0;
        for (long i = 0; i < count; i++)
        {
            image.ReadExactly(buf, 0, sectorSize);
            leaves[i] = SHA256.HashData(buf);
        }
        return leaves;
    }

    /// <summary>Fold leaf hashes to the root. An empty image has the root
    /// SHA-256 of nothing — defined, and never matches a real dump.</summary>
    public static byte[] Root(byte[][] leaves)
    {
        if (leaves.Length == 0) return SHA256.HashData(Array.Empty<byte>());
        var level = leaves;
        var pair = new byte[64];
        while (level.Length > 1)
        {
            var next = new byte[(level.Length + 1) / 2][];
            for (int i = 0; i + 1 < level.Length; i += 2)
            {
                level[i].CopyTo(pair, 0);
                level[i + 1].CopyTo(pair, 32);
                next[i / 2] = SHA256.HashData(pair);
            }
            if ((level.Length & 1) == 1) next[^1] = level[^1];   // promote, don't duplicate
            level = next;
        }
        return level[0];
    }

    /// <summary>Convenience: hash an image straight to its root.</summary>
    public static byte[] ComputeRoot(Stream image, int sectorSize, out long leafCount)
    {
        var leaves = LeafHashes(image, sectorSize);
        leafCount = leaves.Length;
        return Root(leaves);
    }

    /// <summary>Build the audit path proving leaf <paramref name="index"/>
    /// belongs to the tree over <paramref name="leaves"/>.</summary>
    public static List<PathStep> Prove(byte[][] leaves, long index)
    {
        if (index < 0 || index >= leaves.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        var path = new List<PathStep>();
        var level = leaves;
        long i = index;
        var pair = new byte[64];
        while (level.Length > 1)
        {
            long sibling = (i & 1) == 0 ? i + 1 : i - 1;
            if (sibling < level.Length)
                path.Add(new PathStep(level[sibling], SiblingIsLeft: (i & 1) == 1));
            // else: unpaired node promoted — no step at this level.

            var next = new byte[(level.Length + 1) / 2][];
            for (int j = 0; j + 1 < level.Length; j += 2)
            {
                level[j].CopyTo(pair, 0);
                level[j + 1].CopyTo(pair, 32);
                next[j / 2] = SHA256.HashData(pair);
            }
            if ((level.Length & 1) == 1) next[^1] = level[^1];
            level = next;
            i /= 2;
        }
        return path;
    }

    /// <summary>Replay an audit path from a sector's bytes and check it lands
    /// on <paramref name="root"/>.</summary>
    public static bool VerifySector(ReadOnlySpan<byte> sectorBytes, IReadOnlyList<PathStep> path, byte[] root)
    {
        var cur = SHA256.HashData(sectorBytes);
        var pair = new byte[64];
        foreach (var step in path)
        {
            if (step.SiblingIsLeft)
            {
                step.Hash.CopyTo(pair, 0);
                cur.CopyTo(pair, 32);
            }
            else
            {
                cur.CopyTo(pair, 0);
                step.Hash.CopyTo(pair, 32);
            }
            cur = SHA256.HashData(pair);
        }
        return cur.AsSpan().SequenceEqual(root);
    }
}
