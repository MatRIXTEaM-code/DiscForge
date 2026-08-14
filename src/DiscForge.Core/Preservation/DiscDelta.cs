// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Preservation;

/// <summary>The human-readable file-level difference between two disc images.</summary>
public sealed class DeltaDiff
{
    public List<string> Added { get; set; } = new();
    public List<string> Removed { get; set; } = new();
    public List<string> Changed { get; set; } = new();
    public int Unchanged { get; set; }

    [JsonIgnore] public int TotalChanges => Added.Count + Removed.Count + Changed.Count;
}

/// <summary>
/// A content-aware delta between two disc images: everything needed to rebuild the
/// <b>target</b> byte-exact from the <b>base</b>, carrying only the file contents the
/// base does not already have.
/// </summary>
public sealed class DiscDeltaPackage
{
    public string Schema { get; set; } = DiscDelta.SchemaId;
    /// <summary>SHA-256 of the base image this delta was made against — checked on apply.</summary>
    public string BaseImageSha256 { get; set; } = "";
    /// <summary>SHA-256 of the target image this delta reconstructs.</summary>
    public string TargetImageSha256 { get; set; } = "";
    /// <summary>The target's re-master recipe (structural bytes inline, files by hash).</summary>
    public RemasterRecipe Recipe { get; set; } = new();
    /// <summary>Only the file blobs the base lacks: SHA-256 → base64 content.</summary>
    public Dictionary<string, string> Store { get; set; } = new();
    /// <summary>Named file-level changes, for humans.</summary>
    public DeltaDiff Diff { get; set; } = new();

    [JsonIgnore] public long DeltaStoreBytes => Store.Values.Sum(v => (long)(v.Length * 3L / 4));
}

/// <summary>
/// Content-aware disc delta: diff two images (a game and its revision, two region
/// variants, a patched build) at the <i>file</i> level and emit only what actually
/// changed. Because it reuses the <see cref="Remaster"/> content-addressed cover, a
/// file present unchanged in both images is stored once — in the base — and merely
/// referenced by the delta; only new or modified files travel in the delta itself.
/// Applying the delta to the base regenerates the target <b>byte-for-byte</b> (same
/// SHA-256), so two related discs can be kept for the cost of one plus their
/// differences. Pure reconstruction of the owner's own data; it defeats nothing.
/// </summary>
public static class DiscDelta
{
    public const string SchemaId = "discforge-delta/1";
    private const int SectorSize = 2048;

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Build a delta that reconstructs <paramref name="targetImage"/> from
    /// <paramref name="baseImage"/>.</summary>
    public static DiscDeltaPackage Create(byte[] baseImage, byte[] targetImage)
    {
        ArgumentNullException.ThrowIfNull(baseImage);
        ArgumentNullException.ThrowIfNull(targetImage);

        var (_, baseStore) = Remaster.FromIso(baseImage);
        var (targetRecipe, targetStore) = Remaster.FromIso(targetImage);

        // The delta carries only the blobs the base does not already have.
        var delta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sha, content) in targetStore)
            if (!baseStore.ContainsKey(sha))
                delta[sha] = System.Convert.ToBase64String(content);

        return new DiscDeltaPackage
        {
            BaseImageSha256 = Sha256Hex(baseImage),
            TargetImageSha256 = targetRecipe.ImageSha256,
            Recipe = targetRecipe,
            Store = delta,
            Diff = DiffFiles(baseImage, targetImage),
        };
    }

    /// <summary>Reconstruct the target image from the base and the delta. Verifies the
    /// delta was made against this exact base, and that the rebuilt image hashes to the
    /// recorded target SHA-256.</summary>
    public static byte[] Apply(DiscDeltaPackage package, byte[] baseImage)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(baseImage);

        string baseSha = Sha256Hex(baseImage);
        if (!string.Equals(baseSha, package.BaseImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "This delta was made against a different base image " +
                $"(expected {package.BaseImageSha256[..12]}…, got {baseSha[..12]}…).");

        var (_, baseStore) = Remaster.FromIso(baseImage);
        var deltaStore = package.Store.ToDictionary(
            kv => kv.Key, kv => System.Convert.FromBase64String(kv.Value), StringComparer.OrdinalIgnoreCase);

        byte[] Resolve(string sha)
        {
            if (deltaStore.TryGetValue(sha, out var d)) return d;
            if (baseStore.TryGetValue(sha, out var b)) return b;
            throw new InvalidDataException(
                $"Delta cannot be applied: file {sha[..12]}… is neither in the delta nor in the base.");
        }

        var rebuilt = Remaster.Rebuild(package.Recipe, Resolve);
        string actual = Sha256Hex(rebuilt);
        if (!string.Equals(actual, package.TargetImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Rebuilt image hash {actual[..12]}… does not match the target {package.TargetImageSha256[..12]}….");
        return rebuilt;
    }

    public static string ToJson(DiscDeltaPackage p) => JsonSerializer.Serialize(p, Pretty);

    public static DiscDeltaPackage FromJson(string json)
        => JsonSerializer.Deserialize<DiscDeltaPackage>(json, Pretty)
           ?? throw new ArgumentException("Empty or invalid disc delta.");

    // ---- internals ----------------------------------------------------------

    /// <summary>File-level diff of two ISO images by path and content hash.</summary>
    private static DeltaDiff DiffFiles(byte[] baseImage, byte[] targetImage)
    {
        var baseFiles = FileHashes(baseImage);
        var targetFiles = FileHashes(targetImage);

        var diff = new DeltaDiff();
        foreach (var (path, sha) in targetFiles)
        {
            if (!baseFiles.TryGetValue(path, out var baseSha)) diff.Added.Add(path);
            else if (!string.Equals(baseSha, sha, StringComparison.OrdinalIgnoreCase)) diff.Changed.Add(path);
            else diff.Unchanged++;
        }
        foreach (var path in baseFiles.Keys)
            if (!targetFiles.ContainsKey(path)) diff.Removed.Add(path);

        diff.Added.Sort(StringComparer.OrdinalIgnoreCase);
        diff.Removed.Sort(StringComparer.OrdinalIgnoreCase);
        diff.Changed.Sort(StringComparer.OrdinalIgnoreCase);
        return diff;
    }

    private static Dictionary<string, string> FileHashes(byte[] image)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IsoDirectory dir;
        using (var ms = new MemoryStream(image, writable: false))
            dir = IsoReader.Read(ms);

        foreach (var f in dir.Files)
        {
            long start = (long)f.Extent * SectorSize;
            long len = f.Size;
            if (start < 0 || start + len > image.Length) continue;   // truncated — skip defensively
            map[f.Path] = Sha256Hex(image.AsSpan((int)start, (int)len));
        }
        return map;
    }

    private static string Sha256Hex(ReadOnlySpan<byte> data)
        => System.Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
