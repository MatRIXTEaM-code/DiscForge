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

/// <summary>One span of the original image: a file's content (by hash), a run of
/// structural bytes stored inline, or a run of zeros.</summary>
public sealed class RemasterRegion
{
    public long Offset { get; set; }
    public long Length { get; set; }
    /// <summary>"file", "raw" or "zero".</summary>
    public string Kind { get; set; } = "raw";
    public string? Sha256 { get; set; }   // file: content hash (resolved from the store)
    public string? Data { get; set; }     // raw: base64 of the inline structural bytes
}

/// <summary>
/// A recipe that describes an image as an ordered list of regions covering every
/// byte — so the original image can be regenerated exactly from the recipe plus a
/// content store keyed by file hash.
/// </summary>
public sealed class RemasterRecipe
{
    public string Schema { get; set; } = Remaster.SchemaId;
    public long TotalLength { get; set; }
    public string ImageSha256 { get; set; } = "";
    public List<RemasterRegion> Regions { get; set; } = new();

    public int FileRegions => Regions.Count(r => r.Kind == "file");
}

public sealed record RemasterVerifyResult(bool Match, string ExpectedSha, string ActualSha);

/// <summary>
/// Deterministic re-mastering — "rebuild-to-verify". Instead of storing a disc
/// image and merely hashing it, this decomposes the image into its file contents
/// (stored once, content-addressed, shareable across every disc that contains them)
/// plus a small structural recipe, and can <b>regenerate the byte-exact original</b>
/// — the same SHA-256 as the real disc. That makes preservation self-proving: if
/// the files and recipe don't rebuild bit-identically, something is wrong; and a
/// collection that shares files stores each unique file only once. Pure
/// reconstruction of data the owner already has — it defeats nothing.
///
/// The core is format-agnostic: given the byte ranges a container's files occupy,
/// it captures everything else (system area, descriptors, path tables, directory
/// records, padding) as inline structural bytes, with long zero runs compressed.
/// <see cref="FromIso"/> supplies those ranges for an ISO 9660 image.
/// </summary>
public static class Remaster
{
    public const string SchemaId = "discforge-remaster/1";
    private const int MinZeroRun = 16;   // only worth splitting zero runs this long out of a raw span

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Build a recipe and content store from an image and the byte ranges
    /// its files occupy (offset, length). Ranges must lie inside the image; any that
    /// overlap an already-accepted range are dropped (their bytes are captured as
    /// structural instead), so the result is always a complete, non-overlapping cover.</summary>
    public static (RemasterRecipe Recipe, IReadOnlyDictionary<string, byte[]> Store) Build(
        byte[] image, IReadOnlyList<(long Offset, long Length)> fileRegions)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(fileRegions);

        // Accept a clean, sorted, non-overlapping subset of the requested file ranges.
        var accepted = new List<(long Offset, long Length)>();
        long cursor = 0;
        foreach (var fr in fileRegions.Where(f => f.Length > 0)
                                      .OrderBy(f => f.Offset).ThenByDescending(f => f.Length))
        {
            if (fr.Offset < 0 || fr.Offset + fr.Length > image.Length) continue;   // out of bounds
            if (fr.Offset < cursor) continue;                                       // overlaps a prior range
            accepted.Add(fr);
            cursor = fr.Offset + fr.Length;
        }

        var recipe = new RemasterRecipe { TotalLength = image.Length };
        var store = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        long pos = 0;
        foreach (var fr in accepted)
        {
            if (fr.Offset > pos) EmitGap(image, pos, fr.Offset, recipe);
            var content = image.AsSpan((int)fr.Offset, (int)fr.Length).ToArray();
            string sha = Sha256Hex(content);
            store[sha] = content;   // dedup: identical content collapses to one entry
            recipe.Regions.Add(new RemasterRegion { Offset = fr.Offset, Length = fr.Length, Kind = "file", Sha256 = sha });
            pos = fr.Offset + fr.Length;
        }
        if (pos < image.Length) EmitGap(image, pos, image.Length, recipe);

        recipe.ImageSha256 = Sha256Hex(image);
        return (recipe, store);
    }

    /// <summary>Regenerate the image from a recipe and a file resolver (hash → content).</summary>
    public static byte[] Rebuild(RemasterRecipe recipe, Func<string, byte[]> resolveFile)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(resolveFile);
        if (recipe.TotalLength > int.MaxValue)
            throw new NotSupportedException("Images larger than 2 GiB are not supported by the in-memory rebuild.");

        var outp = new byte[recipe.TotalLength];
        foreach (var r in recipe.Regions)
        {
            switch (r.Kind)
            {
                case "zero":
                    break;   // already zero
                case "raw":
                    var raw = System.Convert.FromBase64String(r.Data ?? "");
                    if (raw.Length != r.Length) throw new InvalidDataException($"Raw region at {r.Offset} is the wrong length.");
                    raw.CopyTo(outp, (int)r.Offset);
                    break;
                case "file":
                    var content = resolveFile(r.Sha256 ?? "")
                        ?? throw new InvalidDataException($"Missing content for file region {r.Sha256} at {r.Offset}.");
                    if (content.Length != r.Length)
                        throw new InvalidDataException($"Content for {r.Sha256} is {content.Length} bytes; region expects {r.Length}.");
                    content.CopyTo(outp, (int)r.Offset);
                    break;
                default:
                    throw new InvalidDataException($"Unknown region kind '{r.Kind}'.");
            }
        }
        return outp;
    }

    /// <summary>Rebuild the image and confirm it hashes to the recipe's recorded
    /// image hash — the proof that the parts truly reconstruct the original.</summary>
    public static RemasterVerifyResult Verify(RemasterRecipe recipe, Func<string, byte[]> resolveFile)
    {
        var rebuilt = Rebuild(recipe, resolveFile);
        string actual = Sha256Hex(rebuilt);
        return new RemasterVerifyResult(
            string.Equals(actual, recipe.ImageSha256, StringComparison.OrdinalIgnoreCase),
            recipe.ImageSha256, actual);
    }

    /// <summary>Build a recipe + store from an ISO 9660 image, using the directory
    /// tree to locate each file's bytes.</summary>
    public static (RemasterRecipe Recipe, IReadOnlyDictionary<string, byte[]> Store) FromIso(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        IsoDirectory dir;
        using (var ms = new MemoryStream(image, writable: false))
            dir = IsoReader.Read(ms);

        var regions = dir.Files
            .Where(f => f.Size > 0)
            .Select(f => ((long)f.Extent * IsoReader.SectorSize, (long)f.Size))
            .ToList();
        return Build(image, regions);
    }

    public static string ToJson(RemasterRecipe r) => JsonSerializer.Serialize(r, Json);

    public static RemasterRecipe FromJson(string json)
        => JsonSerializer.Deserialize<RemasterRecipe>(json, Json)
           ?? throw new ArgumentException("Empty or invalid remaster recipe.");

    // ---- internals ----------------------------------------------------------

    // Emit a [start, end) span of non-file bytes as raw/zero regions, compressing
    // long zero runs.
    private static void EmitGap(byte[] image, long start, long end, RemasterRecipe recipe)
    {
        long i = start;
        long rawStart = -1;
        while (i < end)
        {
            if (image[i] == 0)
            {
                long z = i;
                while (z < end && image[z] == 0) z++;
                long runLen = z - i;
                if (runLen >= MinZeroRun)
                {
                    if (rawStart >= 0) { EmitRaw(image, rawStart, i, recipe); rawStart = -1; }
                    recipe.Regions.Add(new RemasterRegion { Offset = i, Length = runLen, Kind = "zero" });
                    i = z;
                    continue;
                }
                if (rawStart < 0) rawStart = i;   // short zero run folds into the raw span
                i = z;
            }
            else
            {
                if (rawStart < 0) rawStart = i;
                i++;
            }
        }
        if (rawStart >= 0) EmitRaw(image, rawStart, end, recipe);
    }

    private static void EmitRaw(byte[] image, long start, long end, RemasterRecipe recipe)
    {
        var span = image.AsSpan((int)start, (int)(end - start));
        recipe.Regions.Add(new RemasterRegion
        {
            Offset = start,
            Length = end - start,
            Kind = "raw",
            Data = System.Convert.ToBase64String(span),
        });
    }

    private static string Sha256Hex(ReadOnlySpan<byte> data)
        => System.Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
