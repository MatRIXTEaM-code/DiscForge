// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Files;

/// <summary>One file in a disc's content index: its path, size, and content hash.</summary>
public readonly record struct IndexedFile(string Path, long Size, string Sha);

/// <summary>The files of one disc image, indexed by path with a content hash each.</summary>
public sealed record ContentIndex
{
    public required string Filesystem { get; init; }
    public required string? VolumeId { get; init; }
    public required IReadOnlyList<IndexedFile> Files { get; init; }
    public string? Error { get; init; }
}

/// <summary>A file that exists in both discs but whose content changed.</summary>
public sealed record ChangedFile(string Path, long SizeA, long SizeB, string ShaA, string ShaB);

/// <summary>A file that moved or was renamed — same bytes, different path.</summary>
public sealed record MovedFile(string PathA, string PathB, long Size);

/// <summary>The result of diffing two disc images at the file level.</summary>
public sealed record DiscDiffResult
{
    public required IReadOnlyList<IndexedFile> Added { get; init; }     // in B, not A
    public required IReadOnlyList<IndexedFile> Removed { get; init; }   // in A, not B
    public required IReadOnlyList<ChangedFile> Changed { get; init; }   // same path, different content
    public required IReadOnlyList<MovedFile> Moved { get; init; }       // same content, different path
    public required int Unchanged { get; init; }
    public string? Error { get; init; }

    public bool Identical => Error is null && Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0 && Moved.Count == 0;

    public string Summary()
    {
        if (Error is not null) return $"disc-diff: {Error}";
        var sb = new StringBuilder();
        sb.AppendLine(Identical
            ? "IDENTICAL — both discs contain the same files with the same bytes."
            : $"DIFFERENT — {Added.Count} added, {Removed.Count} removed, {Changed.Count} changed, {Moved.Count} moved/renamed, {Unchanged} unchanged.");
        foreach (var f in Added) sb.AppendLine($"  + {f.Path} ({f.Size:N0} bytes)");
        foreach (var f in Removed) sb.AppendLine($"  - {f.Path} ({f.Size:N0} bytes)");
        foreach (var c in Changed)
            sb.AppendLine($"  ~ {c.Path} ({c.SizeA:N0} -> {c.SizeB:N0} bytes)");
        foreach (var m in Moved)
            sb.AppendLine($"  » {m.PathA} -> {m.PathB} ({m.Size:N0} bytes, same content)");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// disc-diff — compare two disc images at the FILE level and report what changed: files added, removed,
/// changed in content, or moved/renamed (same bytes, new path). It reads each image through its filesystem
/// and compares by content hash, so it answers "what actually differs between these two discs?" — two
/// pressings of a title, a patched versus original disc, or two revisions — which byte-level and Redump
/// reconciliation checks do not. Read-only; it compares and reports, and changes nothing.
/// </summary>
public static class DiscDiff
{
    public static DiscDiffResult Compare(string imageA, string imageB)
    {
        var a = ImageBrowser.BuildContentIndex(imageA);
        if (a.Error is not null) return Fail($"could not read '{Path.GetFileName(imageA)}': {a.Error}");
        var b = ImageBrowser.BuildContentIndex(imageB);
        if (b.Error is not null) return Fail($"could not read '{Path.GetFileName(imageB)}': {b.Error}");
        return Compare(a, b);
    }

    public static DiscDiffResult Compare(ContentIndex a, ContentIndex b)
    {
        static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

        var aByPath = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in a.Files) aByPath[Norm(f.Path)] = f;
        var bByPath = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in b.Files) bByPath[Norm(f.Path)] = f;

        var added = new List<IndexedFile>();
        var removed = new List<IndexedFile>();
        var changed = new List<ChangedFile>();
        int unchanged = 0;

        foreach (var (path, fb) in bByPath)
        {
            if (!aByPath.TryGetValue(path, out var fa)) added.Add(fb);
            else if (fa.Sha == fb.Sha && fa.Size == fb.Size) unchanged++;
            else changed.Add(new ChangedFile(fb.Path, fa.Size, fb.Size, fa.Sha, fb.Sha));
        }
        foreach (var (path, fa) in aByPath)
            if (!bByPath.ContainsKey(path)) removed.Add(fa);

        // Reclassify add+remove pairs that share content as moves/renames.
        var moved = new List<MovedFile>();
        var removedByContent = removed.GroupBy(f => f.Size + ":" + f.Sha)
            .ToDictionary(g => g.Key, g => new Queue<IndexedFile>(g));
        var stillAdded = new List<IndexedFile>();
        foreach (var f in added)
        {
            var key = f.Size + ":" + f.Sha;
            if (removedByContent.TryGetValue(key, out var q) && q.Count > 0)
            {
                var from = q.Dequeue();
                moved.Add(new MovedFile(from.Path, f.Path, f.Size));
            }
            else stillAdded.Add(f);
        }
        var stillRemoved = removed.Where(f => moved.All(m => m.PathA != f.Path)).ToList();

        return new DiscDiffResult
        {
            Added = Order(stillAdded), Removed = Order(stillRemoved),
            Changed = changed.OrderBy(c => c.Path, StringComparer.Ordinal).ToList(),
            Moved = moved.OrderBy(m => m.PathA, StringComparer.Ordinal).ToList(),
            Unchanged = unchanged,
        };
    }

    private static IReadOnlyList<IndexedFile> Order(List<IndexedFile> f) =>
        f.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();

    private static DiscDiffResult Fail(string message) => new()
    {
        Added = Array.Empty<IndexedFile>(), Removed = Array.Empty<IndexedFile>(),
        Changed = Array.Empty<ChangedFile>(), Moved = Array.Empty<MovedFile>(),
        Unchanged = 0, Error = message,
    };
}
