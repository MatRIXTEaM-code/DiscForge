// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscForge.Core.Preservation;

/// <summary>GF(2^8) arithmetic (primitive polynomial 0x11D) for the erasure code.</summary>
internal static class Gf256
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static Gf256()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    public static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
    public static byte Inv(byte a)
    {
        if (a == 0) throw new DivideByZeroException("GF(256) inverse of zero.");
        return Exp[255 - Log[a]];
    }
}

/// <summary>Systematic Reed-Solomon erasure coding over GF(256) using a Cauchy generator, so any
/// <c>k</c> of the <c>k+m</c> shards reconstruct the original — the recovery core of the vault.</summary>
internal static class ReedSolomon
{
    // Cauchy element for parity row p, data column j: 1 / ((k+p) XOR j). Never zero (k+p ≥ k > j),
    // and every square submatrix of a Cauchy matrix is invertible, giving an MDS code.
    private static byte Cauchy(int k, int p, int j) => Gf256.Inv((byte)((k + p) ^ j));

    public static byte[][] EncodeParity(byte[][] dataShards, int m)
    {
        int k = dataShards.Length;
        int len = dataShards[0].Length;
        var parity = new byte[m][];
        for (int p = 0; p < m; p++)
        {
            parity[p] = new byte[len];
            for (int j = 0; j < k; j++)
            {
                byte c = Cauchy(k, p, j);
                var d = dataShards[j];
                var acc = parity[p];
                for (int pos = 0; pos < len; pos++) acc[pos] ^= Gf256.Mul(c, d[pos]);
            }
        }
        return parity;
    }

    /// <summary>Recover the k data shards from any k present shards (nulls = lost).</summary>
    public static byte[][] RecoverData(byte[]?[] shards, int k, int m)
    {
        var present = new List<int>();
        for (int i = 0; i < shards.Length && present.Count < k; i++)
            if (shards[i] is not null) present.Add(i);
        if (present.Count < k)
            throw new InvalidOperationException($"Only {present.Count} of {k} required shards survive — unrecoverable.");

        int len = shards[present[0]]!.Length;

        // Build the k×k decode matrix from the generator rows of the chosen survivors.
        var d = new byte[k][];
        for (int t = 0; t < k; t++)
        {
            d[t] = new byte[k];
            int idx = present[t];
            if (idx < k) d[t][idx] = 1;                       // a data shard → identity row
            else for (int j = 0; j < k; j++) d[t][j] = Cauchy(k, idx - k, j);   // a parity shard → Cauchy row
        }
        var inv = Invert(d, k);

        var data = new byte[k][];
        for (int j = 0; j < k; j++) data[j] = new byte[len];
        for (int pos = 0; pos < len; pos++)
            for (int j = 0; j < k; j++)
            {
                byte acc = 0;
                for (int t = 0; t < k; t++)
                    acc ^= Gf256.Mul(inv[j][t], shards[present[t]]![pos]);
                data[j][pos] = acc;
            }
        return data;
    }

    private static byte[][] Invert(byte[][] mat, int n)
    {
        // Gauss-Jordan over GF(256): [mat | I] → [I | mat^-1].
        var a = new byte[n][];
        var inv = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            a[i] = (byte[])mat[i].Clone();
            inv[i] = new byte[n];
            inv[i][i] = 1;
        }

        for (int col = 0; col < n; col++)
        {
            if (a[col][col] == 0)
            {
                int swap = -1;
                for (int r = col + 1; r < n; r++) if (a[r][col] != 0) { swap = r; break; }
                if (swap < 0) throw new InvalidOperationException("Decode matrix is singular.");
                (a[col], a[swap]) = (a[swap], a[col]);
                (inv[col], inv[swap]) = (inv[swap], inv[col]);
            }

            byte pivInv = Gf256.Inv(a[col][col]);
            for (int j = 0; j < n; j++) { a[col][j] = Gf256.Mul(a[col][j], pivInv); inv[col][j] = Gf256.Mul(inv[col][j], pivInv); }

            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                byte f = a[r][col];
                if (f == 0) continue;
                for (int j = 0; j < n; j++)
                {
                    a[r][j] ^= Gf256.Mul(f, a[col][j]);
                    inv[r][j] ^= Gf256.Mul(f, inv[col][j]);
                }
            }
        }
        return inv;
    }
}

/// <summary>A self-healing preservation container: the disc image split into data blocks, plus
/// Reed-Solomon parity blocks and the metadata needed to verify and rebuild it.</summary>
public sealed class PreservationVault
{
    public string Schema { get; set; } = PreservationVaultOps.SchemaId;
    public int BlockSize { get; set; }
    public int DataBlocks { get; set; }
    public int ParityBlocks { get; set; }
    public long ImageLength { get; set; }
    public string ImageSha256 { get; set; } = "";
    /// <summary>SHA-256 of every block (data then parity), for corruption detection.</summary>
    public List<string> BlockHashes { get; set; } = new();
    /// <summary>Base64 of every block (data then parity); an empty entry marks a missing block.</summary>
    public List<string?> Blocks { get; set; } = new();
    public string? GenomeId { get; set; }
    public string? LineageDigest { get; set; }
}

/// <summary>Which blocks are intact, and whether the image can be healed.</summary>
public sealed record VaultHealth
{
    public required int TotalBlocks { get; init; }
    public required int IntactBlocks { get; init; }
    public required IReadOnlyList<int> DamagedBlocks { get; init; }
    public required bool Recoverable { get; init; }
    public required bool Pristine { get; init; }

    public string Summary() => Pristine
        ? $"Vault intact — all {TotalBlocks} blocks verify."
        : Recoverable
            ? $"{DamagedBlocks.Count} of {TotalBlocks} blocks damaged, but recoverable from parity."
            : $"{DamagedBlocks.Count} of {TotalBlocks} blocks damaged — too many to recover.";
}

public sealed record VaultHealReport
{
    public required bool Recovered { get; init; }
    public required IReadOnlyList<int> RepairedBlocks { get; init; }
    public required bool ImageValid { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Self-healing preservation container — the capstone of the platform. It wraps a disc image in
/// Reed-Solomon parity so the archive can repair its own bit-rot: split into k data blocks plus m
/// parity blocks, and any k of the k+m survive to rebuild the exact original. Every block is hashed
/// so silent corruption is detected, and the image's own SHA-256, its genome id and its lineage
/// digest travel inside the vault, so a healed image proves it is still authentic. A preservation
/// format designed to outlive the medium it sits on: prove faithful → watch for rot → survive it.
/// It only adds redundancy to and reconstructs data the owner already has — it defeats nothing.
/// </summary>
public static class PreservationVaultOps
{
    public const string SchemaId = "discforge-vault/1";
    public const int MaxShards = 256;   // GF(256) evaluation-point limit: DataBlocks + ParityBlocks

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Wrap an image in a vault with <paramref name="parityBlocks"/> of recovery redundancy
    /// (it can survive losing up to that many of its blocks).</summary>
    public static PreservationVault Create(byte[] image, int parityBlocks, int dataBlocks = 16,
                                           string? genomeId = null, string? lineageDigest = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (dataBlocks < 1) throw new ArgumentOutOfRangeException(nameof(dataBlocks));
        if (parityBlocks < 1) throw new ArgumentOutOfRangeException(nameof(parityBlocks));
        if (dataBlocks + parityBlocks > MaxShards)
            throw new ArgumentException($"data+parity blocks must be ≤ {MaxShards}.");

        int blockSize = Math.Max(1, (image.Length + dataBlocks - 1) / dataBlocks);

        var data = new byte[dataBlocks][];
        for (int i = 0; i < dataBlocks; i++)
        {
            data[i] = new byte[blockSize];
            long start = (long)i * blockSize;
            long len = Math.Min(blockSize, Math.Max(0, image.Length - start));
            if (len > 0) Array.Copy(image, start, data[i], 0, len);
        }
        var parity = ReedSolomon.EncodeParity(data, parityBlocks);

        var vault = new PreservationVault
        {
            BlockSize = blockSize,
            DataBlocks = dataBlocks,
            ParityBlocks = parityBlocks,
            ImageLength = image.Length,
            ImageSha256 = Sha(image),
            GenomeId = genomeId,
            LineageDigest = lineageDigest,
        };
        foreach (var b in data.Concat(parity))
        {
            vault.Blocks.Add(System.Convert.ToBase64String(b));
            vault.BlockHashes.Add(Sha(b));
        }
        return vault;
    }

    /// <summary>Inspect the vault: which blocks verify, and whether it can be healed.</summary>
    public static VaultHealth Check(PreservationVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var shards = DecodeShards(vault, out var damaged);
        int total = vault.DataBlocks + vault.ParityBlocks;
        int intact = total - damaged.Count;
        return new VaultHealth
        {
            TotalBlocks = total,
            IntactBlocks = intact,
            DamagedBlocks = damaged,
            Recoverable = intact >= vault.DataBlocks,
            Pristine = damaged.Count == 0,
        };
    }

    /// <summary>Heal the vault: reconstruct any damaged/missing blocks from parity, repair the vault
    /// in place, and return the rebuilt image (verified against the embedded SHA-256).</summary>
    public static byte[] Heal(PreservationVault vault, out VaultHealReport report)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var shards = DecodeShards(vault, out var damaged);

        if (damaged.Count == 0)
        {
            var img0 = Reassemble(vault, shards!);
            bool ok0 = Sha(img0) == vault.ImageSha256;
            report = new VaultHealReport
            {
                Recovered = true,
                RepairedBlocks = Array.Empty<int>(),
                ImageValid = ok0,
                Message = ok0 ? "Vault already intact." : "Vault intact but the image hash does not match.",
            };
            return img0;
        }

        int intact = (vault.DataBlocks + vault.ParityBlocks) - damaged.Count;
        if (intact < vault.DataBlocks)
        {
            report = new VaultHealReport
            {
                Recovered = false,
                RepairedBlocks = Array.Empty<int>(),
                ImageValid = false,
                Message = $"{damaged.Count} blocks damaged but only {intact} survive of {vault.DataBlocks} needed — unrecoverable.",
            };
            return Array.Empty<byte>();
        }

        var recoveredData = ReedSolomon.RecoverData(shards, vault.DataBlocks, vault.ParityBlocks);
        var recoveredParity = ReedSolomon.EncodeParity(recoveredData, vault.ParityBlocks);
        var all = recoveredData.Concat(recoveredParity).ToArray();

        // Repair the vault in place.
        for (int i = 0; i < all.Length; i++)
        {
            vault.Blocks[i] = System.Convert.ToBase64String(all[i]);
            vault.BlockHashes[i] = Sha(all[i]);
        }

        var image = Reassemble(vault, all);
        bool ok = Sha(image) == vault.ImageSha256;
        report = new VaultHealReport
        {
            Recovered = true,
            RepairedBlocks = damaged,
            ImageValid = ok,
            Message = ok
                ? $"Healed {damaged.Count} damaged block(s) from parity; image verified against its SHA-256."
                : $"Rebuilt {damaged.Count} block(s) but the image hash does not match — deeper damage.",
        };
        return image;
    }

    public static string ToJson(PreservationVault vault) => JsonSerializer.Serialize(vault, Json);

    public static PreservationVault FromJson(string json)
        => JsonSerializer.Deserialize<PreservationVault>(json, Json)
           ?? throw new ArgumentException("Empty or invalid vault.");

    // ---- internals ----------------------------------------------------------

    // Decode each block; a block that is missing, wrong-length, or whose hash fails is "damaged" (null).
    private static byte[]?[] DecodeShards(PreservationVault vault, out List<int> damaged)
    {
        int total = vault.DataBlocks + vault.ParityBlocks;
        var shards = new byte[]?[total];
        damaged = new List<int>();
        for (int i = 0; i < total; i++)
        {
            string? b64 = i < vault.Blocks.Count ? vault.Blocks[i] : null;
            if (string.IsNullOrWhiteSpace(b64)) { damaged.Add(i); continue; }
            try
            {
                var bytes = System.Convert.FromBase64String(b64);
                if (bytes.Length != vault.BlockSize || i >= vault.BlockHashes.Count || Sha(bytes) != vault.BlockHashes[i])
                {
                    damaged.Add(i);
                    continue;
                }
                shards[i] = bytes;
            }
            catch (FormatException) { damaged.Add(i); }
        }
        return shards;
    }

    private static byte[] Reassemble(PreservationVault vault, byte[]?[] shards)
    {
        var image = new byte[vault.ImageLength];
        for (int i = 0; i < vault.DataBlocks; i++)
        {
            long start = (long)i * vault.BlockSize;
            if (start >= image.Length) break;
            int len = (int)Math.Min(vault.BlockSize, image.Length - start);
            Array.Copy(shards[i]!, 0, image, start, len);
        }
        return image;
    }

    private static string Sha(byte[] data)
        => System.Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
