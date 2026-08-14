// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>The optical medium a quality scan belongs to — it decides which error metrics are meaningful.</summary>
public enum DiscFamily : byte { Unknown, Cd, Dvd, BluRay }

/// <summary>The drive-quality tool a scan export came from. Recognised so metadata (drive, media id,
/// speed) can be lifted from that tool's preamble; parsing of the numeric table is tool-agnostic.</summary>
public enum ScanTool : byte { Unknown, OptiDriveControl, NeroDiscSpeed, KProbe, DvdInfoPro, Generic }

/// <summary>One measured interval along a disc's surface. Every metric is optional because each tool and
/// each medium reports a different subset: CDs give C1/C2 (and sometimes an uncorrectable/CU count),
/// DVDs give PIE/PIF/POF, Blu-rays give LDC/BIS. <see cref="Position"/> is the x-axis value as the scan
/// gave it (disc MB, sector/LBA, or elapsed seconds); when absent it is the row index.</summary>
public sealed record ScanRow
{
    public required double Position { get; init; }
    // CD (CIRC): C1 is the first-layer block error rate (BLER); C2 the second layer; Cu uncorrectable.
    public int? C1 { get; init; }
    public int? C2 { get; init; }
    public int? Cu { get; init; }
    // DVD (RS-PC): PIE inner-parity errors, PIF inner-parity failures, POF outer-parity failures.
    public int? Pie { get; init; }
    public int? Pif { get; init; }
    public int? Pof { get; init; }
    // Blu-ray (LDC/BIS ECC): LDC long-distance-code errors, BIS burst-indicator-subcode errors.
    public int? Ldc { get; init; }
    public int? Bis { get; init; }
    public double? Jitter { get; init; }

    /// <summary>Map this interval onto DiscForge's medium-agnostic (C1, C2, uncorrectable) triple that
    /// <c>disc-print</c> and <c>disc-rot</c> consume. The analogy is by correction tier, not by name:
    /// a DVD's PIE and a CD's C1 are both the first-tier correctable error rate, PIF and C2 the serious
    /// second tier, and POF and CU both mean data the drive could not correct. BD LDC/BIS map the same
    /// way (LDC→first tier, BIS→second). This is why an imported DVD or BD scan flows straight through
    /// the existing rot/fingerprint machinery.</summary>
    public ScanSample ToSample(DiscFamily family) => family switch
    {
        DiscFamily.Dvd => new ScanSample(Pie ?? 0, Pif ?? 0, Pof ?? 0),
        DiscFamily.BluRay => new ScanSample(Ldc ?? 0, Bis ?? 0, Cu ?? 0),
        _ => new ScanSample(C1 ?? 0, C2 ?? 0, Cu ?? 0),   // CD / Unknown
    };
}

/// <summary>A foreign drive-quality scan normalised into DiscForge's model: the tool and medium it came
/// from, whatever provenance the export carried (drive, media id, book type, write speed, date), and the
/// per-interval error rows. Summary maxima/averages are computed for the medium's own metrics, and a
/// spec verdict is offered against the standard drive-quality guidelines.</summary>
public sealed record QualityScan
{
    public required ScanTool Tool { get; init; }
    public required DiscFamily Family { get; init; }
    public required IReadOnlyList<ScanRow> Rows { get; init; }

    public string? DiscId { get; init; }
    public string? Drive { get; init; }
    public string? MediaId { get; init; }
    public string? BookType { get; init; }
    public string? WriteSpeed { get; init; }
    public DateTimeOffset? ScannedAt { get; init; }
    /// <summary>The x-axis unit the scan used: "mb", "sector", "second", or "interval" when unlabelled.</summary>
    public required string PositionUnit { get; init; }
    /// <summary>A note when the parse had to assume something (e.g. a headerless file's medium).</summary>
    public string? Assumption { get; init; }

    public int Count => Rows.Count;

    // ---- CD ----
    public int MaxC1 => Rows.Max(r => r.C1 ?? 0);
    public int MaxC2 => Rows.Max(r => r.C2 ?? 0);
    // ---- DVD ----
    public int MaxPie => Rows.Max(r => r.Pie ?? 0);
    public double AvgPie => Rows.Count == 0 ? 0 : Rows.Average(r => (double)(r.Pie ?? 0));
    public int MaxPif => Rows.Max(r => r.Pif ?? 0);
    public long TotalPof => Rows.Sum(r => (long)(r.Pof ?? 0));
    // ---- BD ----
    public int MaxLdc => Rows.Max(r => r.Ldc ?? 0);
    public double AvgLdc => Rows.Count == 0 ? 0 : Rows.Average(r => (double)(r.Ldc ?? 0));
    public int MaxBis => Rows.Max(r => r.Bis ?? 0);
    /// <summary>Any interval that lost data outright — CD CU, or DVD POF (both map to the CU tier).</summary>
    public long TotalUncorrectable => Rows.Sum(r => (long)(r.Cu ?? 0) + (Family == DiscFamily.Dvd ? (r.Pof ?? 0) : 0));

    /// <summary>Convert to the (C1, C2, CU) samples that <c>disc-print</c> and <c>disc-rot</c> read.</summary>
    public IReadOnlyList<ScanSample> ToSamples() => Rows.Select(r => r.ToSample(Family)).ToList();

    /// <summary>True when the scan clears the standard drive-quality guideline for its medium.
    /// The hard line in every case is "no uncorrectable errors"; the correctable tiers use the
    /// commonly quoted ceilings (DVD PIE≤280 / PIF≤16, BD LDC avg≤13 / BIS≤15). CD keeps only the
    /// no-C2 hard line here — <c>bler</c> is the tool for a graded CD verdict.</summary>
    public bool Pass => Family switch
    {
        DiscFamily.Dvd => TotalPof == 0 && MaxPif <= 16 && MaxPie <= 280,
        DiscFamily.BluRay => AvgLdc <= 13 && MaxBis <= 15,
        DiscFamily.Cd => MaxC2 == 0 && Rows.Sum(r => (long)(r.Cu ?? 0)) == 0,
        _ => TotalUncorrectable == 0,
    };

    /// <summary>An archival letter grade for DVD and Blu-ray scans (CD defers to <c>bler</c>, so returns
    /// "-"). Heuristic bands over the standard thresholds, in the same spirit as the CD BLER grade: the
    /// hard "F" line is any uncorrectable error, or PIF&gt;16 / PIE&gt;280 (DVD), or LDC&nbsp;avg&gt;13 /
    /// BIS&nbsp;max&gt;15 (BD); within spec, the grade tightens on PIF/PIE (DVD) — the failure metric
    /// archivists watch first — or LDC/BIS (BD).</summary>
    public string Grade() => Family switch
    {
        DiscFamily.Dvd => !Pass ? "F"
            : MaxPif <= 2 && MaxPie <= 50 ? "A"
            : MaxPif <= 4 && MaxPie <= 100 ? "B"
            : MaxPif <= 4 && MaxPie <= 280 ? "C"
            : "D",
        DiscFamily.BluRay => !Pass ? "F" : AvgLdc <= 5 && MaxBis <= 5 ? "A" : "B",
        _ => "-",
    };

    public string Verdict()
    {
        if (Count == 0) return "no scan intervals parsed.";
        string body = Family switch
        {
            DiscFamily.Dvd =>
                $"PIE max {MaxPie} / avg {AvgPie:0.0} (≤280), PIF max {MaxPif} (≤4 tight / 16 hard), POF {TotalPof}",
            DiscFamily.BluRay =>
                $"LDC avg {AvgLdc:0.0} (≤13) / max {MaxLdc}, BIS max {MaxBis} (≤15)",
            DiscFamily.Cd =>
                $"C1 max {MaxC1}, C2 max {MaxC2} (Red Book: run `bler` for the graded verdict)",
            _ => $"{Count} intervals",
        };
        string tail = Family == DiscFamily.Cd
            ? (Pass ? "no C2 errors" : "C2/uncorrectable present — OUT OF SPEC")
            : Pass ? $"within guideline (grade {Grade()})" : $"OUT OF SPEC (grade {Grade()})";
        return $"{body} — {tail}.";
    }
}

/// <summary>
/// scan-import — read a drive-quality scan exported by another tool (Opti Drive Control, Nero CD-DVD
/// Speed / DiscSpeed, KProbe, DVDInfoPro, or any delimited CSV/TSV of the same data) and normalise it
/// into DiscForge's own scan model, so a decade of community scans becomes something DiscForge can
/// analyse: the imported samples feed straight into <c>disc-rot</c> (rot trend / dump-order),
/// <c>disc-print</c> (physical-copy fingerprint) and, for CDs, <c>bler</c> (Red Book grade).
///
/// The parser is deliberately format-tolerant because these tools mostly save graphs as images and only
/// their *text* exports (and the tables people hand-post on forums) are machine-readable. It recognises
/// the numeric table by its column headers across the CD (C1/C2), DVD (PIE/PIF/POF) and BD (LDC/BIS)
/// vocabularies, lifts whatever provenance the preamble carries (drive, media id, book type, speed,
/// date), and falls back to a positional/headerless read when a medium hint is given. It classifies and
/// reports; it neither writes to a disc nor defeats anything.
/// </summary>
public static class QualityScanImport
{
    private enum Field { None, Position, C1, C2, Cu, Pie, Pif, Pof, Ldc, Bis, Jitter }

    private static readonly char[] Delimiters = { ',', '\t', ';' };

    /// <summary>Parse a scan export. <paramref name="hint"/> disambiguates a headerless file's medium;
    /// <paramref name="id"/> overrides the disc label (else the export's own title is used).</summary>
    public static QualityScan Parse(string text, DiscFamily hint = DiscFamily.Unknown, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r", "").Split('\n');

        var tool = ScanTool.Unknown;
        string? drive = null, mediaId = null, bookType = null, speed = null, title = null;
        DateTimeOffset? when = null;

        int headerIdx = -1;
        char delim = '\0';
        int[]? map = null;   // column index → Field

        // Pass 1: find the header row (first line whose cells classify to a metric), and along the way
        // scan every preceding line for tool signatures and key:value provenance.
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            tool = SniffTool(line, tool);
            SniffMeta(line, ref drive, ref mediaId, ref bookType, ref speed, ref title, ref when);

            var (d, cols) = SplitBest(line);
            if (cols.Length >= 2)
            {
                var m = new int[cols.Length];
                int metricCells = 0, knownCells = 0;
                for (int c = 0; c < cols.Length; c++)
                {
                    m[c] = (int)Classify(cols[c]);
                    if (m[c] != (int)Field.None) knownCells++;
                    if (m[c] is > (int)Field.Position and not (int)Field.Jitter) metricCells++;
                }
                // A header must name at least one error metric and be mostly recognised tokens.
                if (metricCells >= 1 && knownCells >= 2)
                {
                    headerIdx = i; delim = d; map = m; break;
                }
            }
        }

        var rows = new List<ScanRow>();
        var family = hint;
        string? assumption = null;

        if (map != null)
        {
            for (int i = headerIdx + 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] is '#') continue;
                var cells = SplitOn(line, delim);
                var row = RowFromMapped(cells, map);
                if (row != null) rows.Add(row);
            }
            if (family == DiscFamily.Unknown) family = InferFamily(map);
        }
        else
        {
            // Headerless: read every numeric line as (position?, metric, metric…) under the hint medium.
            if (family == DiscFamily.Unknown) { family = DiscFamily.Cd; assumption = "no header and no medium hint — read as a CD C1/C2 scan"; }
            else assumption = "no header — columns read positionally for the given medium";
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';' or '/') continue;
                var (_, cells) = SplitBest(line);
                if (cells.Length == 0 || !cells.All(IsNumeric)) continue;
                var row = RowPositional(cells, family, rows.Count);
                if (row != null) rows.Add(row);
            }
        }

        // If a header was present but the medium is still unknown, infer from which columns had data.
        if (family == DiscFamily.Unknown) family = InferFamilyFromRows(rows);

        if (tool == ScanTool.Unknown && rows.Count > 0) tool = ScanTool.Generic;
        string unit = map != null && headerIdx >= 0 ? PositionUnit(SplitOn(lines[headerIdx].Trim(), delim), map) : "interval";

        return new QualityScan
        {
            Tool = tool,
            Family = family,
            Rows = rows,
            DiscId = id ?? title,
            Drive = drive,
            MediaId = mediaId,
            BookType = bookType,
            WriteSpeed = speed,
            ScannedAt = when,
            PositionUnit = unit,
            Assumption = assumption,
        };
    }

    public static string Render(QualityScan s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var sb = new StringBuilder();
        string fam = s.Family switch
        {
            DiscFamily.Cd => "CD", DiscFamily.Dvd => "DVD", DiscFamily.BluRay => "Blu-ray", _ => "unknown medium",
        };
        string src = s.Tool == ScanTool.Unknown ? "" : $" [{ToolName(s.Tool)}]";
        sb.AppendLine($"{fam} quality scan{src}: {s.Count} intervals — {s.Verdict()}");
        var prov = new List<string>();
        if (s.DiscId is { Length: > 0 }) prov.Add($"disc \"{s.DiscId}\"");
        if (s.Drive is { Length: > 0 }) prov.Add($"drive {s.Drive}");
        if (s.MediaId is { Length: > 0 }) prov.Add($"media {s.MediaId}");
        if (s.BookType is { Length: > 0 }) prov.Add($"book {s.BookType}");
        if (s.WriteSpeed is { Length: > 0 }) prov.Add($"speed {s.WriteSpeed}");
        if (s.ScannedAt is { } w) prov.Add($"scanned {w:yyyy-MM-dd}");
        if (prov.Count > 0) sb.AppendLine("  " + string.Join(" · ", prov));
        if (s.Assumption is { Length: > 0 }) sb.AppendLine($"  (assumption: {s.Assumption})");
        return sb.ToString().TrimEnd();
    }

    public static string ToolName(ScanTool t) => t switch
    {
        ScanTool.OptiDriveControl => "Opti Drive Control",
        ScanTool.NeroDiscSpeed => "Nero DiscSpeed",
        ScanTool.KProbe => "KProbe",
        ScanTool.DvdInfoPro => "DVDInfoPro",
        ScanTool.Generic => "generic",
        _ => "unknown",
    };

    // ---- provenance sniffing ------------------------------------------------

    private static ScanTool SniffTool(string line, ScanTool current)
    {
        if (current != ScanTool.Unknown) return current;
        string l = line.ToLowerInvariant();
        if (l.Contains("opti drive control") || l.Contains("optidrivecontrol")) return ScanTool.OptiDriveControl;
        if (l.Contains("dvdinfopro") || l.Contains("dvd info pro")) return ScanTool.DvdInfoPro;
        if (l.Contains("kprobe") || l.Contains("k-probe")) return ScanTool.KProbe;
        if (l.Contains("discspeed") || l.Contains("cd-dvd speed") || l.Contains("cd dvd speed") ||
            (l.Contains("nero") && l.Contains("speed"))) return ScanTool.NeroDiscSpeed;
        return ScanTool.Unknown;
    }

    private static void SniffMeta(string line, ref string? drive, ref string? mediaId, ref string? bookType,
                                  ref string? speed, ref string? title, ref DateTimeOffset? when)
    {
        int sep = line.IndexOf(':');
        if (sep < 0) sep = line.IndexOf('=');
        if (sep <= 0 || sep >= line.Length - 1) return;
        string key = line[..sep].Trim().ToLowerInvariant();
        string val = line[(sep + 1)..].Trim();
        if (val.Length == 0) return;

        switch (key)
        {
            case "drive" or "recorder" or "reader" or "device": drive ??= val; break;
            case "media id" or "media code" or "mid" or "manufacturer" or "manufacturer id" or "media": mediaId ??= val; break;
            case "book type" or "booktype" or "format type": bookType ??= val; break;
            case "write speed" or "scan speed" or "read speed" or "speed" or "burn speed": speed ??= val; break;
            case "title" or "label" or "disc" or "disc name" or "volume" or "volume label": title ??= val; break;
            case "date" or "scanned" or "scan date" or "created":
                if (when is null &&
                    DateTimeOffset.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var w))
                    when = w;
                break;
        }
    }

    // ---- tabular parsing ----------------------------------------------------

    // Split a line on whichever delimiter yields the most cells; fall back to whitespace runs.
    private static (char delim, string[] cells) SplitBest(string line)
    {
        char best = '\0'; int bestCount = 0;
        foreach (var d in Delimiters)
        {
            int n = line.Count(ch => ch == d);
            if (n > bestCount) { bestCount = n; best = d; }
        }
        if (best == '\0')
            return ('\0', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return (best, SplitOn(line, best));
    }

    private static string[] SplitOn(string line, char delim) => delim == '\0'
        ? line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : line.Split(delim).Select(s => s.Trim()).ToArray();

    private static ScanRow? RowFromMapped(string[] cells, int[] map)
    {
        double? pos = null;
        int? c1 = null, c2 = null, cu = null, pie = null, pif = null, pof = null, ldc = null, bis = null;
        double? jit = null;
        bool anyMetric = false;

        for (int c = 0; c < cells.Length && c < map.Length; c++)
        {
            var f = (Field)map[c];
            if (f == Field.None) continue;
            string cell = cells[c];
            if (f == Field.Position) { if (TryD(cell, out var p)) pos = p; continue; }
            if (f == Field.Jitter) { if (TryD(cell, out var j)) { jit = j; } continue; }
            if (!TryI(cell, out var v)) continue;
            anyMetric = true;
            switch (f)
            {
                case Field.C1: c1 = v; break;
                case Field.C2: c2 = v; break;
                case Field.Cu: cu = v; break;
                case Field.Pie: pie = v; break;
                case Field.Pif: pif = v; break;
                case Field.Pof: pof = v; break;
                case Field.Ldc: ldc = v; break;
                case Field.Bis: bis = v; break;
            }
        }
        if (!anyMetric && jit is null) return null;
        return new ScanRow
        {
            Position = pos ?? 0, C1 = c1, C2 = c2, Cu = cu,
            Pie = pie, Pif = pif, Pof = pof, Ldc = ldc, Bis = bis, Jitter = jit,
        };
    }

    // Headerless: first column is the position if there is more than one; the rest map by medium.
    private static ScanRow? RowPositional(string[] cells, DiscFamily family, int index)
    {
        var nums = new List<double>();
        foreach (var c in cells) if (TryD(c, out var v)) nums.Add(v);
        if (nums.Count == 0) return null;

        double pos; int off;
        if (nums.Count == 1) { pos = index; off = 0; }
        else { pos = nums[0]; off = 1; }
        int M(int k) => off + k < nums.Count ? (int)Math.Round(nums[off + k]) : 0;
        bool has(int k) => off + k < nums.Count;

        return family switch
        {
            DiscFamily.Dvd => new ScanRow { Position = pos, Pie = M(0), Pif = has(1) ? M(1) : null, Pof = has(2) ? M(2) : null },
            DiscFamily.BluRay => new ScanRow { Position = pos, Ldc = M(0), Bis = has(1) ? M(1) : null },
            _ => new ScanRow { Position = pos, C1 = M(0), C2 = has(1) ? M(1) : null, Cu = has(2) ? M(2) : null },
        };
    }

    private static Field Classify(string cell)
    {
        string s = new string(cell.Where(ch => !char.IsWhiteSpace(ch) && ch is not '_' and not '-' and not '(' and not ')').ToArray())
            .ToLowerInvariant();
        if (s.Length == 0) return Field.None;

        // Position / x-axis — tolerant of unit suffixes like "position(mb)" or "time(s)".
        if (s.StartsWith("pos") || s.Contains("position")) return Field.Position;
        if (s is "mb" or "mbyte" or "mbytes" or "lba" or "interval" or "x" or "offset" or "adr" ||
            s.Contains("sector") || s.Contains("address")) return Field.Position;
        if (s is "time" or "second" or "seconds" or "sec" || s.Contains("time") || s.StartsWith("elapsed"))
            return Field.Position;

        if (s.Contains("jitter")) return Field.Jitter;
        if (s.StartsWith("c1") || s == "bler") return Field.C1;
        if (s.StartsWith("c2")) return Field.C2;
        if (s.Contains("ldc")) return Field.Ldc;
        if (s.Contains("bis")) return Field.Bis;
        if (s.Contains("uncorrect") || s == "cu" || s.Contains("e32")) return Field.Cu;

        bool fail = s.Contains("fail");
        bool err = s.Contains("err");
        if (s.StartsWith("pif") || (s.StartsWith("pi") && fail)) return Field.Pif;
        if (s.StartsWith("pie") || s == "pi" || (s.StartsWith("pi") && err)) return Field.Pie;
        if (s.StartsWith("pof") || s == "po" || (s.StartsWith("po") && (fail || err))) return Field.Pof;
        return Field.None;
    }

    private static DiscFamily InferFamily(int[] map)
    {
        bool any(Field f) => Array.IndexOf(map, (int)f) >= 0;
        if (any(Field.Pie) || any(Field.Pif) || any(Field.Pof)) return DiscFamily.Dvd;
        if (any(Field.Ldc) || any(Field.Bis)) return DiscFamily.BluRay;
        if (any(Field.C1) || any(Field.C2)) return DiscFamily.Cd;
        return DiscFamily.Unknown;
    }

    private static DiscFamily InferFamilyFromRows(IReadOnlyList<ScanRow> rows)
    {
        if (rows.Any(r => r.Pie.HasValue || r.Pif.HasValue || r.Pof.HasValue)) return DiscFamily.Dvd;
        if (rows.Any(r => r.Ldc.HasValue || r.Bis.HasValue)) return DiscFamily.BluRay;
        if (rows.Any(r => r.C1.HasValue || r.C2.HasValue)) return DiscFamily.Cd;
        return DiscFamily.Unknown;
    }

    // Name the x-axis from the header cell that classified as the position column.
    private static string PositionUnit(string[] header, int[] map)
    {
        for (int c = 0; c < header.Length && c < map.Length; c++)
        {
            if ((Field)map[c] != Field.Position) continue;
            string s = header[c].ToLowerInvariant();
            if (s.Contains("mb")) return "mb";
            if (s.Contains("sector") || s.Contains("lba")) return "sector";
            if (s.Contains("time") || s.Contains("sec")) return "second";
            return "interval";
        }
        return "interval";
    }

    // ---- numeric helpers ----------------------------------------------------

    private static bool IsNumeric(string s) => TryD(s, out _);

    private static bool TryD(string s, out double v) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v);

    private static bool TryI(string s, out int v)
    {
        if (int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return true;
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) { v = (int)Math.Round(d); return true; }
        v = 0; return false;
    }
}
