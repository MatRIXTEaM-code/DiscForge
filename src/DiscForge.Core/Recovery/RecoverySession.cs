// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Recovery;

/// <summary>
/// The one-stop recovery assessment behind <c>dforge recover</c> — the "point it at a damaged image
/// and be told what's wrong, what's salvageable, and what to do next" workflow that recovery GUIs
/// are loved for, distilled into a pure, testable core. The CLI gathers the evidence (format
/// identification, filesystem cross-check, the unreadable-sector map, blank-region scan); this class
/// turns that evidence into a four-tier verdict, concrete next-step advice, and a self-contained
/// HTML report. Pure: no I/O here, so every grading rule is unit-tested.
/// </summary>
public static class RecoverySession
{
    public enum Grade
    {
        /// <summary>No damage found; filesystems (where present) read fully and agree.</summary>
        Intact,
        /// <summary>Damage or gaps exist, but a filesystem still reads — files can be pulled out.</summary>
        Recoverable,
        /// <summary>Damage present and no filesystem view survives — only sector-level salvage remains.</summary>
        Damaged,
        /// <summary>Nothing identified and nothing readable.</summary>
        Unreadable,
    }

    public sealed record ZeroRegion(long Offset, long Length);

    public sealed record Findings
    {
        public required string Image { get; init; }
        public required long SizeBytes { get; init; }
        /// <summary>Identified container format, or null when unrecognized.</summary>
        public string? Format { get; init; }
        /// <summary>Filesystem views that opened: e.g. "ISO9660 (512 files)". Empty = none readable.</summary>
        public IReadOnlyList<string> FilesystemViews { get; init; } = Array.Empty<string>();
        /// <summary>Cross-check verdict when ≥1 view exists: "Agree", "Divergent", "Incomplete", or null.</summary>
        public string? FilesystemVerdict { get; init; }
        /// <summary>Genuinely-damaged unreadable sectors from the .badsectors sidecar (null = no sidecar).</summary>
        public int? DamagedSectors { get; init; }
        /// <summary>Harmless track-boundary holes from the sidecar.</summary>
        public int? BoundarySectors { get; init; }
        /// <summary>Runs of ≥64 KiB of zero bytes — holes, blank regions, or padded gaps.</summary>
        public IReadOnlyList<ZeroRegion> ZeroRegions { get; init; } = Array.Empty<ZeroRegion>();
        /// <summary>Whole-image Shannon entropy in bits/byte, if sampled.</summary>
        public double? EntropyBitsPerByte { get; init; }
    }

    // ---- grading (pure rules) ----------------------------------------------

    public static Grade Assess(Findings f)
    {
        ArgumentNullException.ThrowIfNull(f);
        bool hasFs = f.FilesystemViews.Count > 0;
        bool damage = (f.DamagedSectors ?? 0) > 0
                      || string.Equals(f.FilesystemVerdict, "Incomplete", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(f.FilesystemVerdict, "Divergent", StringComparison.OrdinalIgnoreCase);

        if (!hasFs && f.Format is null) return Grade.Unreadable;
        if (damage) return hasFs ? Grade.Recoverable : Grade.Damaged;

        // Large zero regions in an otherwise-clean image are suspicious holes, not proven damage:
        // still Recoverable-grade attention if they dominate, Intact otherwise.
        long zeroBytes = f.ZeroRegions.Sum(z => z.Length);
        if (hasFs && f.SizeBytes > 0 && zeroBytes > f.SizeBytes / 2) return Grade.Recoverable;
        return Grade.Intact;
    }

    public static IReadOnlyList<string> Advise(Findings f, Grade grade)
    {
        ArgumentNullException.ThrowIfNull(f);
        var advice = new List<string>();
        switch (grade)
        {
            case Grade.Intact:
                advice.Add("No damage found. Hash and catalogue it (dforge hashgen / preserve) and you're done.");
                break;
            case Grade.Recoverable:
                advice.Add("A filesystem still reads — extract the files first (dforge fs-recover), then worry about the sectors.");
                if ((f.DamagedSectors ?? 0) > 0)
                    advice.Add($"{f.DamagedSectors:N0} sector(s) are genuinely unreadable: if the disc is still available, re-read just those with dforge raw-dump --consensus, then merge with dforge dump-merge.");
                if (string.Equals(f.FilesystemVerdict, "Divergent", StringComparison.OrdinalIgnoreCase))
                    advice.Add("The filesystem views DISAGREE — one view exposes files the other hides. Inspect with dforge fs-verify --json before trusting either.");
                break;
            case Grade.Damaged:
                advice.Add("No filesystem survives. Work sector-level: dforge salvage-plan for a re-read strategy, dforge fs-orphans to carve directory remnants.");
                break;
            case Grade.Unreadable:
                advice.Add("Nothing recognizable. Confirm the file is a disc image at all (dforge identify), and check it isn't truncated to zero or still downloading.");
                break;
        }
        if (f.ZeroRegions.Count > 0 && grade != Grade.Unreadable)
        {
            long zb = f.ZeroRegions.Sum(z => z.Length);
            string capped = f.ZeroRegions.Count >= 4096
                ? " (region list capped at 4,096 — total blank space may be understated)" : "";
            advice.Add($"{f.ZeroRegions.Count:N0} blank region(s) totalling {zb:N0} bytes{capped} — zero-filled holes hash like real data, so a checksum match alone proves nothing there.");
        }
        return advice;
    }

    // ---- report -------------------------------------------------------------

    public static string Summary(Findings f, Grade grade, IReadOnlyList<string> advice)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{f.Image}: {grade.ToString().ToUpperInvariant()}  ({f.SizeBytes:N0} bytes" +
                      (f.Format is null ? ", unrecognized format)" : $", {f.Format})"));
        foreach (var v in f.FilesystemViews) sb.AppendLine($"  fs: {v}");
        if (f.FilesystemVerdict is not null) sb.AppendLine($"  fs cross-check: {f.FilesystemVerdict}");
        if (f.DamagedSectors is int d)
            sb.AppendLine($"  unreadable sectors: {d:N0} damage, {f.BoundarySectors ?? 0:N0} boundary");
        if (f.ZeroRegions.Count > 0)
            sb.AppendLine($"  blank regions: {f.ZeroRegions.Count:N0} (largest {f.ZeroRegions.Max(z => z.Length):N0} bytes)");
        if (f.EntropyBitsPerByte is double e) sb.AppendLine($"  entropy: {e:F3} bits/byte");
        foreach (var a in advice) sb.AppendLine($"  → {a}");
        return sb.ToString();
    }

    public static string BuildHtml(Findings f, Grade grade, IReadOnlyList<string> advice)
    {
        static string H(string s) => System.Net.WebUtility.HtmlEncode(s);
        string color = grade switch
        {
            Grade.Intact => "#54d18c",
            Grade.Recoverable => "#e8b64a",
            Grade.Damaged => "#e78a5a",
            _ => "#e05f5f",
        };
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Recovery report — ").Append(H(f.Image))
          .Append("</title><style>body{font:15px/1.5 -apple-system,Segoe UI,sans-serif;background:#12161b;color:#e7ecf2;margin:0;padding:34px}")
          .Append("h1{font-size:22px;margin:0 0 4px}.grade{display:inline-block;font-weight:700;padding:3px 14px;border-radius:20px;color:#12161b;background:")
          .Append(color).Append("}table{border-collapse:collapse;margin:16px 0}td{padding:5px 14px 5px 0;color:#9aa7b4}td+td{color:#e7ecf2}")
          .Append("ul{line-height:1.7}.z{font-size:13px;color:#9aa7b4}</style></head><body>");
        sb.Append("<h1>Recovery report — ").Append(H(f.Image)).Append("</h1>");
        sb.Append("<p><span class=\"grade\">").Append(grade.ToString().ToUpperInvariant()).Append("</span></p>");
        sb.Append("<table>");
        sb.Append("<tr><td>Size</td><td>").Append(f.SizeBytes.ToString("N0")).Append(" bytes</td></tr>");
        sb.Append("<tr><td>Format</td><td>").Append(H(f.Format ?? "unrecognized")).Append("</td></tr>");
        foreach (var v in f.FilesystemViews)
            sb.Append("<tr><td>Filesystem</td><td>").Append(H(v)).Append("</td></tr>");
        if (f.FilesystemVerdict is not null)
            sb.Append("<tr><td>Cross-check</td><td>").Append(H(f.FilesystemVerdict)).Append("</td></tr>");
        if (f.DamagedSectors is int dmg)
            sb.Append("<tr><td>Unreadable sectors</td><td>").Append(dmg.ToString("N0")).Append(" damage, ")
              .Append((f.BoundarySectors ?? 0).ToString("N0")).Append(" boundary</td></tr>");
        if (f.EntropyBitsPerByte is double ent)
            sb.Append("<tr><td>Entropy</td><td>").Append(ent.ToString("F3")).Append(" bits/byte</td></tr>");
        sb.Append("</table>");
        if (f.ZeroRegions.Count > 0)
        {
            sb.Append("<p class=\"z\">Blank regions (≥64 KiB of zeros):</p><ul class=\"z\">");
            foreach (var z in f.ZeroRegions.Take(50))
                sb.Append("<li>offset ").Append(z.Offset.ToString("N0")).Append(", ")
                  .Append(z.Length.ToString("N0")).Append(" bytes</li>");
            if (f.ZeroRegions.Count > 50)
                sb.Append("<li>… and ").Append((f.ZeroRegions.Count - 50).ToString("N0")).Append(" more</li>");
            sb.Append("</ul>");
        }
        sb.Append("<h2 style=\"font-size:17px\">What to do</h2><ul>");
        foreach (var a in advice) sb.Append("<li>").Append(H(a)).Append("</li>");
        sb.Append("</ul><p class=\"z\">Generated by dforge recover. Verdicts follow DiscForge's \"provably correct or declined\" rules: ")
          .Append("a zero-filled hole is reported, never silently hashed over.</p></body></html>");
        return sb.ToString();
    }

    // ---- evidence helpers (pure) -------------------------------------------

    /// <summary>Find runs of ≥<paramref name="minRun"/> zero bytes in a stream — holes and blank regions.</summary>
    public static IReadOnlyList<ZeroRegion> FindZeroRegions(Stream s, int minRun = 64 * 1024, int maxRegions = 4096)
    {
        ArgumentNullException.ThrowIfNull(s);
        var regions = new List<ZeroRegion>();
        var buf = new byte[256 * 1024];
        long pos = 0, runStart = -1, runLen = 0;
        s.Position = 0;
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                if (buf[i] == 0)
                {
                    if (runStart < 0) runStart = pos + i;
                    runLen++;
                }
                else if (runStart >= 0)
                {
                    if (runLen >= minRun && regions.Count < maxRegions)
                        regions.Add(new ZeroRegion(runStart, runLen));
                    runStart = -1; runLen = 0;
                }
            }
            pos += n;
        }
        if (runStart >= 0 && runLen >= minRun && regions.Count < maxRegions)
            regions.Add(new ZeroRegion(runStart, runLen));
        return regions;
    }
}
