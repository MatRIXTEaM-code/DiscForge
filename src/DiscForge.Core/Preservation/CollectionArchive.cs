// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscForge.Core.Preservation;

/// <summary>One disc in a collection archive: its name and the recipe that rebuilds it byte-exact
/// from the shared content store.</summary>
public sealed class CollectionDisc
{
    public string Name { get; set; } = "";
    public RemasterRecipe Recipe { get; set; } = new();
}

/// <summary>A whole library reduced to the set of unique file blobs plus a per-disc rebuild recipe.
/// Every disc is regenerable byte-for-byte; shared files are stored exactly once.</summary>
public sealed class CollectionArchive
{
    public string Schema { get; set; } = CollectionArchiver.SchemaId;
    public List<CollectionDisc> Discs { get; set; } = new();
    /// <summary>The shared content-addressed store: SHA-256 (hex) → base64 file content.</summary>
    public Dictionary<string, string> Store { get; set; } = new();
    /// <summary>Names of inputs that could not be read as ISO images and were left out.</summary>
    public List<string> Skipped { get; set; } = new();
}

public sealed record CollectionStats
{
    public required int Discs { get; init; }
    public required long NaiveBytes { get; init; }        // storing every image whole
    public required long ArchiveBytes { get; init; }      // unique blobs + structural recipes
    public required int UniqueBlobs { get; init; }
    public required long TotalFileRefs { get; init; }     // file regions across all discs
    public double DedupRatio => ArchiveBytes == 0 ? 1 : NaiveBytes / (double)ArchiveBytes;
    public long SavedBytes => Math.Max(0, NaiveBytes - ArchiveBytes);
}

/// <summary>An edge in the relationship graph: two discs that share a lot of content.</summary>
public sealed record CollectionEdge(string A, string B, int SharedBlobs, double SharedFraction);

public sealed record CollectionReport
{
    public required CollectionStats Stats { get; init; }
    public required IReadOnlyList<CollectionEdge> Graph { get; init; }

    public string Summary()
        => $"{Stats.Discs} disc(s) → {Stats.UniqueBlobs:N0} unique file blob(s); " +
           $"{Stats.NaiveBytes:N0} B → {Stats.ArchiveBytes:N0} B ({Stats.DedupRatio:0.##}× smaller, " +
           $"{Stats.SavedBytes:N0} B saved). {Graph.Count} related-disc link(s).";
}

/// <summary>
/// Collection-scale self-deduplicating archive — point it at a shelf of related discs (every revision,
/// region and sampler) and it collapses the whole library to the set of genuinely-unique file blobs
/// plus a tiny per-disc rebuild recipe, while every disc stays reconstructable byte-for-byte. Files
/// shared across discs — the engine, the shared assets, an unchanged executable — are stored exactly
/// once no matter how many discs contain them, so a collection heavy with variants can shrink several-
/// fold and get <i>more</i> verifiable, not less (each disc still rebuilds to its exact original SHA).
/// It also draws the relationship graph: which discs share how much, surfacing the variant families.
/// Pure reconstruction of data the owner already has — it defeats nothing.
/// </summary>
public static class CollectionArchiver
{
    public const string SchemaId = "discforge-collection/1";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Build the archive from a set of named ISO images. Unreadable inputs are skipped and
    /// recorded in <see cref="CollectionArchive.Skipped"/>.</summary>
    public static CollectionArchive Build(IReadOnlyList<(string Name, byte[] Iso)> discs)
    {
        ArgumentNullException.ThrowIfNull(discs);
        var archive = new CollectionArchive();

        foreach (var (name, iso) in discs)
        {
            RemasterRecipe recipe;
            IReadOnlyDictionary<string, byte[]> store;
            try
            {
                (recipe, store) = Remaster.FromIso(iso);
            }
            catch
            {
                archive.Skipped.Add(name);
                continue;
            }

            archive.Discs.Add(new CollectionDisc { Name = name, Recipe = recipe });
            foreach (var (sha, content) in store)
                archive.Store.TryAdd(sha, System.Convert.ToBase64String(content));   // dedup: store each blob once
        }

        return archive;
    }

    /// <summary>Rebuild one disc byte-for-byte from the shared store.</summary>
    public static byte[] Reconstruct(CollectionArchive archive, string discName)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var disc = archive.Discs.FirstOrDefault(d => string.Equals(d.Name, discName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No disc named '{discName}' in the archive.");
        return Remaster.Rebuild(disc.Recipe, sha => Resolve(archive, sha));
    }

    /// <summary>Verify every disc rebuilds to its recorded image hash.</summary>
    public static bool VerifyAll(CollectionArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        foreach (var disc in archive.Discs)
        {
            var result = Remaster.Verify(disc.Recipe, sha => Resolve(archive, sha));
            if (!result.Match) return false;
        }
        return true;
    }

    /// <summary>Compute dedup statistics and the shared-content relationship graph.</summary>
    public static CollectionReport Analyze(CollectionArchive archive, double linkThreshold = 0.30)
    {
        ArgumentNullException.ThrowIfNull(archive);

        long naive = archive.Discs.Sum(d => d.Recipe.TotalLength);
        long uniqueBlobBytes = archive.Store.Values.Sum(b => (long)Base64Length(b));
        long structural = archive.Discs.Sum(d => d.Recipe.Regions
            .Where(r => r.Kind == "raw" && r.Data is not null)
            .Sum(r => (long)Base64Length(r.Data!)));
        long totalFileRefs = archive.Discs.Sum(d => (long)d.Recipe.FileRegions);

        var stats = new CollectionStats
        {
            Discs = archive.Discs.Count,
            NaiveBytes = naive,
            ArchiveBytes = uniqueBlobBytes + structural,
            UniqueBlobs = archive.Store.Count,
            TotalFileRefs = totalFileRefs,
        };

        // Per-disc set of file-content hashes, for the overlap graph.
        var sets = archive.Discs.ToDictionary(
            d => d.Name,
            d => new HashSet<string>(d.Recipe.Regions.Where(r => r.Kind == "file" && r.Sha256 is not null)
                                                     .Select(r => r.Sha256!)));

        var edges = new List<CollectionEdge>();
        for (int i = 0; i < archive.Discs.Count; i++)
            for (int j = i + 1; j < archive.Discs.Count; j++)
            {
                var a = archive.Discs[i].Name;
                var b = archive.Discs[j].Name;
                var sa = sets[a];
                var sb = sets[b];
                if (sa.Count == 0 || sb.Count == 0) continue;
                int shared = sa.Count(x => sb.Contains(x));
                double frac = shared / (double)Math.Min(sa.Count, sb.Count);
                if (frac >= linkThreshold) edges.Add(new CollectionEdge(a, b, shared, frac));
            }

        return new CollectionReport
        {
            Stats = stats,
            Graph = edges.OrderByDescending(e => e.SharedFraction).ToList(),
        };
    }

    public static string Render(CollectionReport report)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(report.Summary());
        foreach (var e in report.Graph)
            sb.AppendLine($"  {e.A} ↔ {e.B}: {e.SharedBlobs} shared file(s) ({e.SharedFraction:P0})");
        return sb.ToString().TrimEnd();
    }

    public static string ToJson(CollectionArchive archive) => JsonSerializer.Serialize(archive, Json);

    public static CollectionArchive FromJson(string json)
        => JsonSerializer.Deserialize<CollectionArchive>(json, Json)
           ?? throw new ArgumentException("Empty or invalid collection archive.");

    // ---- internals ----------------------------------------------------------

    private static byte[] Resolve(CollectionArchive archive, string sha)
        => archive.Store.TryGetValue(sha, out var b64)
            ? System.Convert.FromBase64String(b64)
            : throw new InvalidDataException($"Shared store is missing blob {sha}.");

    // Decoded byte length of a base64 string without allocating the bytes.
    private static int Base64Length(string b64)
    {
        int len = b64.Length;
        if (len == 0) return 0;
        int pad = 0;
        if (b64[len - 1] == '=') pad++;
        if (len > 1 && b64[len - 2] == '=') pad++;
        return len / 4 * 3 - pad;
    }
}
