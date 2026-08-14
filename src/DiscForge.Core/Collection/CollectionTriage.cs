// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Cue;
using DiscForge.Core.Dat;
using DiscForge.Core.Files;
using DiscForge.Core.Preservation;
using DiscForge.Core.Redump;

namespace DiscForge.Core.Collection;

/// <summary>What a dump in a collection needs, worst first. The order is the triage priority.</summary>
public enum TriageStatus
{
    /// <summary>Genuine unreadable sectors — the dump is incomplete and must be re-read.</summary>
    Incomplete,
    /// <summary>Matches nothing yet, but the cause is a fixable track split — re-cut it.</summary>
    NeedsRecut,
    /// <summary>Present in the DAT-less sense but unverifiable, or matched nothing — needs a look.</summary>
    NeedsAttention,
    /// <summary>Content-identical to another dump in the collection.</summary>
    Duplicate,
    /// <summary>Byte-for-byte a catalogued Redump dump.</summary>
    Verified,
}

/// <summary>One dump's triage verdict.</summary>
public sealed record TriageEntry
{
    public required string Name { get; init; }
    public required string RelPath { get; init; }
    public required TriageStatus Status { get; init; }
    public string? Game { get; init; }
    public required string Detail { get; init; }
    public string? Action { get; init; }
    public string? ContentHash { get; init; }
}

public sealed record TriageReport
{
    public required string Folder { get; init; }
    public required IReadOnlyList<TriageEntry> Entries { get; init; }

    public int Count(TriageStatus s) => Entries.Count(e => e.Status == s);
    public int Total => Entries.Count;

    public string Summary()
    {
        int action = Count(TriageStatus.Incomplete) + Count(TriageStatus.NeedsRecut) + Count(TriageStatus.NeedsAttention);
        return $"{Total} dump(s): {Count(TriageStatus.Verified)} verified, {Count(TriageStatus.Duplicate)} duplicate, " +
               $"{action} need action ({Count(TriageStatus.Incomplete)} incomplete, {Count(TriageStatus.NeedsRecut)} to re-cut, " +
               $"{Count(TriageStatus.NeedsAttention)} to check).";
    }
}

/// <summary>
/// collection-triage — a librarian's view of a whole folder of dumps, where every other tool works one disc at a
/// time. It walks a collection and, for each dump, folds together the checks DiscForge already makes — Redump
/// match, the unreadable-sector map, the mismatch diagnosis, content de-duplication — into a single ranked
/// worklist: what is preserved, what is a duplicate, and what needs action (a re-read for genuine damage, a
/// re-cut for a wrong split, or a look for anything unidentified). It reads and reports; it changes nothing.
/// </summary>
public static class CollectionTriage
{
    public static TriageReport Scan(string folder, DatFile? dat = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"'{folder}' is not a folder.");

        var entries = new List<TriageEntry>();
        var cues = Directory.EnumerateFiles(folder, "*.cue", SearchOption.AllDirectories).OrderBy(p => p).ToList();
        var cueDirs = new HashSet<string>(cues.Select(c => Path.GetDirectoryName(Path.GetFullPath(c))!), StringComparer.OrdinalIgnoreCase);

        foreach (var cue in cues)
            entries.Add(EvaluateCue(cue, folder, dat));

        // Standalone images not part of a bin/cue set (skip .bin — those belong to a cue).
        foreach (var img in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                     .Where(p => IsStandaloneImage(p)).OrderBy(p => p))
        {
            // A .bin sitting beside a cue is a track file, not a standalone dump.
            if (Path.GetExtension(img).Equals(".bin", StringComparison.OrdinalIgnoreCase)) continue;
            entries.Add(EvaluateImage(img, folder, dat));
        }

        MarkDuplicates(entries);
        // Rank by triage priority (enum order), then by name for stability.
        var ordered = entries.OrderBy(e => (int)e.Status).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new TriageReport { Folder = folder, Entries = ordered };
    }

    private static TriageEntry EvaluateCue(string cuePath, string root, DatFile? dat)
    {
        string name = Path.GetFileName(cuePath);
        string rel = Path.GetRelativePath(root, cuePath);
        string? contentHash = LargestBinHash(cuePath);

        // Genuine unreadable sectors trump everything — a holed dump can never be complete.
        var sidecar = BadSectorMap.SidecarPath(cuePath);
        if (File.Exists(sidecar))
        {
            try
            {
                var bad = BadSectorMap.Load(sidecar);
                if (bad.DamagePresent)
                    return new TriageEntry
                    {
                        Name = name, RelPath = rel, Status = TriageStatus.Incomplete, ContentHash = contentHash,
                        Detail = $"{bad.DamageCount} genuine unreadable sector(s) recorded — INCOMPLETE.",
                        Action = "Re-read the disc; a holed dump cannot match a Redump checksum.",
                    };
            }
            catch { /* a bad sidecar shouldn't sink the entry */ }
        }

        if (dat is null)
            return new TriageEntry
            {
                Name = name, RelPath = rel, Status = TriageStatus.NeedsAttention, ContentHash = contentHash,
                Detail = "No DAT supplied — cannot verify against Redump.",
                Action = "Re-run with --dat <Redump DAT> to verify.",
            };

        try
        {
            var diff = RedumpDiffer.Diff(cuePath, dat);
            if (diff.Match)
                return new TriageEntry
                {
                    Name = name, RelPath = rel, Status = TriageStatus.Verified, Game = diff.Game, ContentHash = contentHash,
                    Detail = $"Matches {diff.Game}.",
                };

            bool recut = diff.Recommendations.Any(r => r.Contains("redump-cue"));
            return new TriageEntry
            {
                Name = name, RelPath = rel, Status = recut ? TriageStatus.NeedsRecut : TriageStatus.NeedsAttention,
                Game = diff.Identified ? diff.Game : null, ContentHash = contentHash,
                Detail = diff.Summary(),
                Action = diff.Recommendations.FirstOrDefault(),
            };
        }
        catch (Exception ex)
        {
            return new TriageEntry
            {
                Name = name, RelPath = rel, Status = TriageStatus.NeedsAttention, ContentHash = contentHash,
                Detail = "Could not diff: " + ex.Message,
            };
        }
    }

    private static TriageEntry EvaluateImage(string imgPath, string root, DatFile? dat)
    {
        string name = Path.GetFileName(imgPath);
        string rel = Path.GetRelativePath(root, imgPath);
        string? hash = null;
        try { hash = ImageChecksums.ComputeFile(imgPath).Sha1.ToLowerInvariant(); } catch { }

        var sidecar = BadSectorMap.SidecarPath(imgPath);
        if (File.Exists(sidecar))
        {
            try
            {
                var bad = BadSectorMap.Load(sidecar);
                if (bad.DamagePresent)
                    return new TriageEntry
                    {
                        Name = name, RelPath = rel, Status = TriageStatus.Incomplete, ContentHash = hash,
                        Detail = $"{bad.DamageCount} genuine unreadable sector(s) — INCOMPLETE.",
                        Action = "Re-read the disc.",
                    };
            }
            catch { }
        }

        if (dat is not null && hash is not null)
        {
            try
            {
                var sums = ImageChecksums.ComputeFile(imgPath);
                var m = dat.Verify(sums.Length, sums.Crc32, sums.Sha1, sums.Md5);
                if (m.Verified)
                    return new TriageEntry
                    {
                        Name = name, RelPath = rel, Status = TriageStatus.Verified, Game = m.Rom?.Game, ContentHash = hash,
                        Detail = $"Matches {m.Rom?.Game}.",
                    };
            }
            catch { }
        }

        return new TriageEntry
        {
            Name = name, RelPath = rel, Status = TriageStatus.NeedsAttention, ContentHash = hash,
            Detail = dat is null ? "Standalone image — no DAT supplied to verify." : "No catalogued dump matches this image.",
            Action = dat is null ? "Re-run with --dat to verify." : "Confirm the disc / DAT region.",
        };
    }

    /// <summary>Second content-identical dump (and beyond) is downgraded to Duplicate, keeping the best-status one.</summary>
    private static void MarkDuplicates(List<TriageEntry> entries)
    {
        foreach (var group in entries.Where(e => e.ContentHash is not null)
                     .GroupBy(e => e.ContentHash).Where(g => g.Count() > 1))
        {
            // Keep the entry with the most-preserved status (highest enum = Verified); mark the rest duplicates.
            var keep = group.OrderByDescending(e => (int)e.Status).First();
            foreach (var e in group)
            {
                if (ReferenceEquals(e, keep)) continue;
                int i = entries.IndexOf(e);
                entries[i] = e with
                {
                    Status = TriageStatus.Duplicate,
                    Detail = $"Content-identical to {keep.Name}.",
                    Action = "Remove or archive the redundant copy.",
                };
            }
        }
    }

    private static bool IsStandaloneImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".iso" or ".cdi";
    }

    private static string? LargestBinHash(string cuePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cuePath))!;
            var sheet = CueSheet.Parse(File.ReadAllText(cuePath));
            var bins = sheet.Tracks.Select(t => t.File).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct()
                .Select(f => Path.Combine(dir, f)).Where(File.Exists).ToList();
            if (bins.Count == 0) return null;
            var largest = bins.OrderByDescending(b => new FileInfo(b).Length).First();
            return ImageChecksums.ComputeFile(largest).Sha1.ToLowerInvariant();
        }
        catch { return null; }
    }

    // ---- HTML dashboard ------------------------------------------------------

    /// <summary>Render the report as a self-contained HTML dashboard the collector can keep open.</summary>
    public static string RenderHtml(TriageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        (string label, string cls) Badge(TriageStatus s) => s switch
        {
            TriageStatus.Incomplete => ("INCOMPLETE", "s-inc"),
            TriageStatus.NeedsRecut => ("RE-CUT", "s-cut"),
            TriageStatus.NeedsAttention => ("CHECK", "s-att"),
            TriageStatus.Duplicate => ("DUPLICATE", "s-dup"),
            _ => ("VERIFIED", "s-ok"),
        };

        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>DiscForge — collection triage</title>
<style>
:root{color-scheme:light dark}
body{font:15px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;margin:0;background:#0f1115;color:#e7e9ee}
header{padding:24px 28px;border-bottom:1px solid #262a33}
h1{margin:0 0 4px;font-size:19px}
.sub{color:#9aa3b2;font-size:13px}
.cards{display:flex;gap:12px;flex-wrap:wrap;padding:20px 28px}
.card{background:#171a21;border:1px solid #262a33;border-radius:10px;padding:14px 18px;min-width:120px}
.card .n{font-size:26px;font-weight:650}
.card .l{font-size:12px;color:#9aa3b2;text-transform:uppercase;letter-spacing:.04em}
table{width:100%;border-collapse:collapse;font-size:14px}
th,td{text-align:left;padding:10px 14px;border-bottom:1px solid #20242c;vertical-align:top}
th{color:#9aa3b2;font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.04em}
tbody tr:hover{background:#161922}
.badge{display:inline-block;padding:2px 9px;border-radius:999px;font-size:11px;font-weight:700;letter-spacing:.03em}
.s-inc{background:#3a1620;color:#ff8ba0}.s-cut{background:#3a2c14;color:#ffca7a}
.s-att{background:#2a2140;color:#c0a8ff}.s-dup{background:#22262e;color:#9aa3b2}.s-ok{background:#12331f;color:#7ce6a1}
.game{color:#c7cede}.action{color:#9aa3b2;font-size:13px}
.wrap{padding:0 28px 40px}
</style></head><body>
<header><h1>DiscForge — collection triage</h1>
""");
        sb.Append($"<div class=\"sub\">{Esc(report.Folder)} · {Esc(report.Summary())}</div></header>\n");

        sb.Append("<div class=\"cards\">");
        void Card(string n, string l) => sb.Append($"<div class=\"card\"><div class=\"n\">{n}</div><div class=\"l\">{l}</div></div>");
        Card(report.Count(TriageStatus.Verified).ToString(), "verified");
        Card(report.Count(TriageStatus.Incomplete).ToString(), "incomplete");
        Card(report.Count(TriageStatus.NeedsRecut).ToString(), "to re-cut");
        Card(report.Count(TriageStatus.NeedsAttention).ToString(), "to check");
        Card(report.Count(TriageStatus.Duplicate).ToString(), "duplicate");
        sb.Append("</div>\n");

        sb.Append("<div class=\"wrap\"><table><thead><tr><th>Status</th><th>Dump</th><th>Identity</th><th>Detail</th><th>Recommended action</th></tr></thead><tbody>\n");
        foreach (var e in report.Entries)
        {
            var (label, cls) = Badge(e.Status);
            sb.Append("<tr>");
            sb.Append($"<td><span class=\"badge {cls}\">{label}</span></td>");
            sb.Append($"<td>{Esc(e.Name)}</td>");
            sb.Append($"<td class=\"game\">{Esc(e.Game ?? "—")}</td>");
            sb.Append($"<td>{Esc(e.Detail)}</td>");
            sb.Append($"<td class=\"action\">{Esc(e.Action ?? "—")}</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody></table></div></body></html>\n");
        return sb.ToString();
    }
}
