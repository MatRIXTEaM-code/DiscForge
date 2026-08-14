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

/// <summary>One file in a watch snapshot: its size, last-write time and SHA-256.</summary>
public sealed class WatchEntry
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public long MTimeTicks { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>A point-in-time integrity snapshot of a collection.</summary>
public sealed class WatchSnapshot
{
    public string Schema { get; set; } = LibraryWatch.SchemaId;
    public string? CreatedUtc { get; set; }
    public List<WatchEntry> Entries { get; set; } = new();
}

/// <summary>How a file changed between two snapshots.</summary>
public enum DriftKind
{
    Added,
    Removed,
    /// <summary>Content changed AND the file's timestamp moved — an intentional edit or replacement.</summary>
    Modified,
    /// <summary>Content changed but the file's timestamp did NOT move — the signature of silent
    /// storage corruption (bit rot): the bytes changed without anything writing to the file.</summary>
    SuspectedRot,
}

public sealed record DriftItem(string Path, DriftKind Kind, string Detail);

/// <summary>The result of comparing an old snapshot to a new scan.</summary>
public sealed class WatchReport
{
    public int Unchanged { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Modified { get; set; }
    public int SuspectedRot { get; set; }
    public List<DriftItem> Changes { get; } = new();

    /// <summary>Silent corruption was found — the alarm this whole feature exists to raise.</summary>
    public bool RotDetected => SuspectedRot > 0;
    public bool AnyChange => Added + Removed + Modified + SuspectedRot > 0;

    public string Summary() =>
        $"{Unchanged:N0} unchanged, {Added:N0} added, {Removed:N0} removed, {Modified:N0} modified, " +
        $"{SuspectedRot:N0} SUSPECTED ROT.";
}

/// <summary>
/// Watches a collection for silent corruption over time — the preservation problem
/// nothing else in this space addresses. A dump can be perfect the day it's made and
/// quietly rot years later when a byte flips on a failing drive or SSD; you don't
/// find out until you try to use it. This snapshots every file's hash, and on the
/// next run tells you not just <i>what changed</i> but <i>whether the change looks
/// like rot</i> — content that changed while the file's own timestamp never moved,
/// which is exactly what block-level corruption looks like and what an intentional
/// edit never does. Pure integrity monitoring; it defeats nothing.
/// </summary>
public static class LibraryWatch
{
    public const string SchemaId = "discforge-watch/1";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Compare a previous snapshot to a current one. Pure and deterministic.</summary>
    public static WatchReport Compare(WatchSnapshot previous, WatchSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var prev = previous.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var cur = current.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var report = new WatchReport();

        foreach (var c in current.Entries)
        {
            if (!prev.TryGetValue(c.Path, out var p))
            {
                report.Added++;
                report.Changes.Add(new DriftItem(c.Path, DriftKind.Added, "new file"));
                continue;
            }
            if (string.Equals(p.Sha256, c.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                report.Unchanged++;
                continue;
            }
            // Content differs. If the timestamp never moved, the write didn't come
            // from a normal edit — that's the fingerprint of silent corruption.
            if (p.MTimeTicks == c.MTimeTicks)
            {
                report.SuspectedRot++;
                report.Changes.Add(new DriftItem(c.Path, DriftKind.SuspectedRot,
                    "content changed but the file was never modified — likely bit rot"));
            }
            else
            {
                report.Modified++;
                report.Changes.Add(new DriftItem(c.Path, DriftKind.Modified, "content and timestamp changed"));
            }
        }

        foreach (var p in previous.Entries)
            if (!cur.ContainsKey(p.Path))
            {
                report.Removed++;
                report.Changes.Add(new DriftItem(p.Path, DriftKind.Removed, "file gone"));
            }

        return report;
    }

    /// <summary>Scan a directory tree into a snapshot (SHA-256 of every file).
    /// <paramref name="excludeNames"/> file names are skipped (e.g. the state file itself).</summary>
    public static WatchSnapshot ScanDirectory(string baseDir, string? createdUtc, IReadOnlySet<string>? excludeNames = null)
    {
        ArgumentNullException.ThrowIfNull(baseDir);
        string full = Path.GetFullPath(baseDir);
        var snap = new WatchSnapshot { CreatedUtc = createdUtc };
        foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(full, file).Replace('\\', '/');
            if (rel.StartsWith(".git/", StringComparison.Ordinal)) continue;
            if (excludeNames is not null && excludeNames.Contains(Path.GetFileName(file))) continue;

            var fi = new FileInfo(file);
            snap.Entries.Add(new WatchEntry
            {
                Path = rel,
                Length = fi.Length,
                MTimeTicks = fi.LastWriteTimeUtc.Ticks,
                Sha256 = Sha256File(file),
            });
        }
        snap.Entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return snap;
    }

    public static string ToJson(WatchSnapshot s) => JsonSerializer.Serialize(s, Json);

    public static WatchSnapshot FromJson(string json)
        => JsonSerializer.Deserialize<WatchSnapshot>(json, Json)
           ?? throw new ArgumentException("Empty or invalid watch snapshot.");

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return System.Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
