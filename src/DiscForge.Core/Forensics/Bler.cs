// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>The outcome of a C1 codeword — RS(32,28), which corrects up to 2 erroneous symbols.</summary>
public enum C1Outcome : byte { Ok, E11, E21, E31 }

/// <summary>The outcome of a C2 codeword — RS(28,24), 2 errors or up to 4 flagged erasures.</summary>
public enum C2Outcome : byte { Ok, E12, E22, E32 }

/// <summary>One second of a C1/C2 error scan: the six standard error-class counts a quality scan reports.
/// BLER (the C1 block-error rate) is E11+E21+E31 — every C1 frame that carried at least one bad symbol.</summary>
public sealed record BlerSample(int Second, int E11, int E21, int E31, int E12, int E22, int E32)
{
    /// <summary>C1 Block Error Rate for this second: C1 frames with any erroneous symbol.</summary>
    public int Bler => E11 + E21 + E31;
    /// <summary>C2-stage corrected/uncorrected events this second.</summary>
    public int C2Events => E12 + E22 + E32;
    /// <summary>Uncorrectable errors this second (C2 failures) — must be zero for a conformant disc.</summary>
    public bool HasUncorrectable => E32 > 0;
}

/// <summary>The archival verdict over a whole C1/C2 scan.</summary>
public sealed record BlerReport
{
    public required IReadOnlyList<BlerSample> Samples { get; init; }
    public int Seconds => Samples.Count;

    public required double AvgBler { get; init; }
    public required int MaxBler { get; init; }
    /// <summary>95th-percentile BLER — a robust "typical worst" that ignores a lone spike.</summary>
    public required int Bler95 { get; init; }

    public required long TotalE11 { get; init; }
    public required long TotalE21 { get; init; }
    public required long TotalE31 { get; init; }
    public required long TotalE12 { get; init; }
    public required long TotalE22 { get; init; }
    public required long TotalE32 { get; init; }

    /// <summary>Longest run of consecutive seconds with any C1 error — a burst of surface damage.</summary>
    public required int MaxBurstSeconds { get; init; }

    /// <summary>The Red Book / IEC 60908 gate: peak BLER within the 220/s limit AND no uncorrectable
    /// (E32) errors anywhere. These two are the hard pass/fail; the grade refines the "pass" band.</summary>
    public bool RedBookPass => MaxBler <= Bler.RedBookMaxBler && TotalE32 == 0;

    /// <summary>An archival letter grade. Heuristic bands over the peak BLER and the severe-error totals,
    /// consistent with common quality-scan practice; the hard spec line is <see cref="RedBookPass"/>.</summary>
    public string Grade()
    {
        if (!RedBookPass) return "F";
        if (MaxBler < 20 && TotalE22 == 0) return "A";
        if (MaxBler < 50) return "B";
        if (MaxBler < 100) return "C";
        return "D";
    }

    public string Summary()
    {
        if (Seconds == 0) return "No C1/C2 samples to assess.";
        string verdict = RedBookPass ? $"within spec (grade {Grade()})" : "OUT OF SPEC";
        return $"BLER over {Seconds}s: avg {AvgBler:0.0}, max {MaxBler}, 95th {Bler95} " +
               $"(Red Book limit {Bler.RedBookMaxBler}); E22 {TotalE22:N0}, E32 {TotalE32:N0}; " +
               $"longest burst {MaxBurstSeconds}s — {verdict}.";
    }
}

/// <summary>
/// bler — the surface-quality report a plant or archivist quotes for a disc's condition, built on the
/// CD's two-stage CIRC error correction. A CD reads through C1 (RS(32,28), correcting up to two bad
/// symbols per frame) and then, after de-interleave, C2 (RS(28,24), two errors or up to four flagged
/// erasures); the standard health metrics are the tallies of what each stage had to do. BLER is the C1
/// block-error rate — every C1 frame with at least one bad symbol, of the 7,350 per second — and the Red
/// Book ceiling is 220/s. E22 is a C2 frame that needed its full two-error correction (a warning), and
/// E32 is a C2 failure: an uncorrectable error the player can only conceal, which must never occur on a
/// conformant disc. This ingests a per-second C1/C2 scan, tallies the six classes, finds the worst burst,
/// and returns the Red Book pass/fail plus an archival grade.
///
/// Honesty about the domain: BLER is a READ-TIME metric the drive's optics report during a quality scan;
/// it cannot be recovered from an already-ripped image, because by the time a file exists the drive has
/// already corrected the errors away. This analyses the scan a drive produces (and its
/// <see cref="ClassifyC1"/>/<see cref="ClassifyC2"/> use the true RS correction capacities), it does not
/// invent errors from a corrected dump. Read-only; it judges and reports, and changes nothing.
/// </summary>
public static class Bler
{
    public const int C1Total = 32, C1Data = 28;
    public const int C2Total = 28, C2Data = 24;
    /// <summary>RS(32,28) corrects (32−28)/2 = 2 errors at C1.</summary>
    public const int C1Correct = (C1Total - C1Data) / 2;
    /// <summary>RS(28,24) corrects (28−24)/2 = 2 errors at C2.</summary>
    public const int C2Correct = (C2Total - C2Data) / 2;
    /// <summary>C2 corrects up to 4 erasures when C1 flags their positions.</summary>
    public const int C2ErasureCapacity = C2Total - C2Data;
    /// <summary>C1 frames per second: 75 sectors × 98 frames.</summary>
    public const int FramesPerSecond = 75 * 98;
    /// <summary>The Red Book maximum acceptable peak BLER, per second.</summary>
    public const int RedBookMaxBler = 220;

    /// <summary>Classify a C1 codeword by how many symbols were erroneous. RS(32,28) corrects up to 2;
    /// 3+ is a C1 failure (E31), whose symbols are flagged as erasures for C2.</summary>
    public static C1Outcome ClassifyC1(int erroneousSymbols) => erroneousSymbols switch
    {
        <= 0 => C1Outcome.Ok,
        1 => C1Outcome.E11,
        2 => C1Outcome.E21,
        _ => C1Outcome.E31,
    };

    /// <summary>Classify a C2 codeword. Without erasure flags RS(28,24) corrects 2 errors; with C1's
    /// erasure flags it corrects up to 4 known-position symbols. Beyond that is E32 — uncorrectable.</summary>
    public static C2Outcome ClassifyC2(int erroneousSymbols, bool erasureFlagged = false)
    {
        int capacity = erasureFlagged ? C2ErasureCapacity : C2Correct;
        if (erroneousSymbols <= 0) return C2Outcome.Ok;
        if (erroneousSymbols > capacity) return C2Outcome.E32;
        return erroneousSymbols == 1 ? C2Outcome.E12 : C2Outcome.E22;
    }

    /// <summary>Aggregate a per-second scan into the archival verdict.</summary>
    public static BlerReport Analyze(IReadOnlyList<BlerSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            return new BlerReport
            {
                Samples = samples, AvgBler = 0, MaxBler = 0, Bler95 = 0,
                TotalE11 = 0, TotalE21 = 0, TotalE31 = 0, TotalE12 = 0, TotalE22 = 0, TotalE32 = 0,
                MaxBurstSeconds = 0,
            };

        long sum = 0, e11 = 0, e21 = 0, e31 = 0, e12 = 0, e22 = 0, e32 = 0;
        int max = 0, burst = 0, maxBurst = 0;
        var blers = new int[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            int b = s.Bler;
            blers[i] = b;
            sum += b; max = Math.Max(max, b);
            e11 += s.E11; e21 += s.E21; e31 += s.E31;
            e12 += s.E12; e22 += s.E22; e32 += s.E32;
            if (b > 0) { burst++; maxBurst = Math.Max(maxBurst, burst); } else burst = 0;
        }

        Array.Sort(blers);
        int idx = (int)Math.Ceiling(0.95 * blers.Length) - 1;
        int p95 = blers[Math.Clamp(idx, 0, blers.Length - 1)];

        return new BlerReport
        {
            Samples = samples,
            AvgBler = sum / (double)samples.Count,
            MaxBler = max,
            Bler95 = p95,
            TotalE11 = e11, TotalE21 = e21, TotalE31 = e31,
            TotalE12 = e12, TotalE22 = e22, TotalE32 = e32,
            MaxBurstSeconds = maxBurst,
        };
    }

    /// <summary>Parse a per-second C1/C2 scan. Recognised columns (by header, case-insensitive): second,
    /// e11, e21, e31, e12, e22, e32. A headerless numeric line is read as either the full
    /// second,e11,e21,e31,e12,e22,e32 (7 fields) or a minimal second,bler,e32 (3 fields, the aggregate
    /// C1 rate recorded as E11 since it dominates a healthy scan). Blank lines and '#'/';' comments are
    /// ignored.</summary>
    public static IReadOnlyList<BlerSample> ParseCsv(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var samples = new List<BlerSample>();
        int[]? map = null;   // header column → field index (0=second,1..6=e11..e32), -1 if absent

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            var parts = line.Split(new[] { ',', '\t', ';' }, StringSplitOptions.TrimEntries);

            // Header row: contains any non-numeric token we recognise.
            if (map == null && parts.Any(p => !double.TryParse(p, NumberStyles.Any, CultureInfo.InvariantCulture, out _)))
            {
                map = BuildHeaderMap(parts);
                continue;
            }

            int F(int field) => FieldValue(parts, map, field);
            if (map != null)
            {
                samples.Add(new BlerSample(F(0), F(1), F(2), F(3), F(4), F(5), F(6)));
            }
            else if (parts.Length >= 7)
            {
                samples.Add(new BlerSample(I(parts, 0), I(parts, 1), I(parts, 2), I(parts, 3),
                                           I(parts, 4), I(parts, 5), I(parts, 6)));
            }
            else if (parts.Length >= 2)
            {
                int sec = I(parts, 0), bler = I(parts, 1), e32 = parts.Length >= 3 ? I(parts, 2) : 0;
                samples.Add(new BlerSample(sec, bler, 0, 0, 0, 0, e32));   // aggregate → E11
            }
        }
        return samples;
    }

    public static string Render(BlerReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        if (r.Seconds == 0) return sb.ToString().TrimEnd();
        sb.AppendLine($"  C1: E11 {r.TotalE11:N0}, E21 {r.TotalE21:N0}, E31 {r.TotalE31:N0}");
        sb.AppendLine($"  C2: E12 {r.TotalE12:N0}, E22 {r.TotalE22:N0}, E32 {r.TotalE32:N0}");
        if (r.TotalE32 > 0)
            sb.AppendLine($"  ⚠ {r.TotalE32:N0} uncorrectable (E32) error(s) — the disc is not a faithful read.");
        else if (r.MaxBler > RedBookMaxBler)
            sb.AppendLine($"  ⚠ peak BLER {r.MaxBler} exceeds the Red Book {RedBookMaxBler}/s limit.");
        return sb.ToString().TrimEnd();
    }

    // ---- helpers ------------------------------------------------------------

    private static int[] BuildHeaderMap(string[] header)
    {
        var map = new[] { -1, -1, -1, -1, -1, -1, -1 };
        for (int c = 0; c < header.Length; c++)
        {
            switch (header[c].ToLowerInvariant())
            {
                case "second" or "sec" or "time" or "t": map[0] = c; break;
                case "e11": map[1] = c; break;
                case "e21": map[2] = c; break;
                case "e31": map[3] = c; break;
                case "e12": map[4] = c; break;
                case "e22": map[5] = c; break;
                case "e32" or "cu" or "uncorrectable": map[6] = c; break;
                case "bler": map[1] = c; break;   // aggregate C1 rate → E11 slot
            }
        }
        return map;
    }

    private static int FieldValue(string[] parts, int[]? map, int field)
    {
        if (map == null) return 0;
        int col = map[field];
        return col >= 0 && col < parts.Length ? I(parts, col) : 0;
    }

    private static int I(string[] parts, int i) =>
        i < parts.Length && int.TryParse(parts[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
