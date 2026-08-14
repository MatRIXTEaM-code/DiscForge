// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>How a region of unreadable/failing sectors is shaped — which tells you what
/// to do with it. Physical shapes (a scratch, surface rot) are damage you try to recover;
/// a deliberate periodic layout is almost certainly a copy-protection pattern you preserve
/// rather than "repair".</summary>
public enum ErrorPatternKind : byte
{
    /// <summary>No failing sectors.</summary>
    None = 0,
    /// <summary>A near-solid contiguous burst — the signature of a radial scratch. Physical; recover.</summary>
    Scratch = 1,
    /// <summary>Sectors failing irregularly and scattered — surface rot / pinholes. Physical; recover.</summary>
    SurfaceRot = 2,
    /// <summary>Failing sectors placed at regular intervals — a periodic layout that stochastic
    /// damage does not produce, so it points to a deliberate (copy-protection) pattern. Preserve it.</summary>
    DeliberatePattern = 3,
    /// <summary>Both a physical shape and a deliberate one are present.</summary>
    Mixed = 4,
}

/// <summary>One coherent region of failing sectors, classified by shape.</summary>
public sealed record ErrorLesion(int Start, int End, int BadSectors, ErrorPatternKind Kind, double Confidence, string Rationale)
{
    /// <summary>Inclusive sector span the lesion covers (End − Start + 1).</summary>
    public int Span => End - Start + 1;

    public override string ToString() =>
        $"[{Start}..{End}] {BadSectors} bad · {Kind} ({Confidence:P0}) — {Rationale}";
}

/// <summary>The shape-analysis verdict for a disc's failing sectors, with a recommendation
/// that respects the clean-room boundary: recover physical damage, preserve deliberate patterns.</summary>
public sealed record ErrorPatternReport
{
    public required int TotalSectors { get; init; }
    public required int BadSectors { get; init; }
    public required ErrorPatternKind Verdict { get; init; }
    public required IReadOnlyList<ErrorLesion> Lesions { get; init; }

    /// <summary>The longest contiguous run of failing sectors — a big number means a scratch/burst.</summary>
    public required int LongestRun { get; init; }

    public required string Recommendation { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>The failing sectors look deliberately placed (protection), not damaged.</summary>
    public bool LooksDeliberate => Verdict is ErrorPatternKind.DeliberatePattern or ErrorPatternKind.Mixed;

    public string Summary()
    {
        if (BadSectors == 0) return "No failing sectors — nothing to classify.";
        string verdict = Verdict switch
        {
            ErrorPatternKind.Scratch => "physical damage (scratch/burst) — recover it",
            ErrorPatternKind.SurfaceRot => "physical damage (surface rot) — recover it",
            ErrorPatternKind.DeliberatePattern => "a deliberate periodic pattern (likely copy protection) — preserve it",
            ErrorPatternKind.Mixed => "a mix of physical damage and a deliberate pattern — recover the damage, preserve the pattern",
            _ => "unclassified",
        };
        return $"{BadSectors:N0} failing sector(s) across {Lesions.Count} region(s): {verdict}.";
    }
}

/// <summary>
/// Error-pattern forensics — classify a disc's unreadable/failing sectors by the <i>shape</i>
/// of the pattern they form, so you know whether to fight them or keep them. A radial scratch
/// takes out a solid burst of adjacent sectors; surface rot scatters pinholes irregularly; a
/// copy-protection scheme places its bad sectors at regular, repeating intervals — a periodicity
/// that random physical damage does not produce. This categorises and recommends; it decodes and
/// defeats nothing. Preserving a protection pattern faithfully is preservation, not circumvention.
/// </summary>
public static class ErrorPatternForensics
{
    // Sectors within this distance of each other belong to the same lesion.
    private const int LinkGap = 128;
    // A contiguous run this long (or a lesion this dense) reads as a scratch/burst.
    private const int ScratchMinRun = 3;
    private const double SolidDensity = 0.85;
    // Periodicity: at least this share of gaps must land on the dominant period.
    private const double RegularityThreshold = 0.70;
    private const int MinPeriodicSectors = 5;

    /// <summary>Classify from a per-sector "is this sector failing?" flag array.</summary>
    public static ErrorPatternReport Classify(IReadOnlyList<bool> bad)
    {
        ArgumentNullException.ThrowIfNull(bad);
        var lbas = new List<int>();
        for (int i = 0; i < bad.Count; i++) if (bad[i]) lbas.Add(i);
        return Classify(lbas, bad.Count);
    }

    /// <summary>Classify from a per-sector health array: Damaged and Unrecovered sectors are "failing".</summary>
    public static ErrorPatternReport Classify(IReadOnlyList<SectorHealth> health)
    {
        ArgumentNullException.ThrowIfNull(health);
        var lbas = new List<int>();
        for (int i = 0; i < health.Count; i++)
            if (health[i] is SectorHealth.Damaged or SectorHealth.Unrecovered) lbas.Add(i);
        return Classify(lbas, health.Count);
    }

    /// <summary>Classify from an explicit list of failing sector LBAs and the disc's sector count.</summary>
    public static ErrorPatternReport FromBadSectors(IEnumerable<int> badLbas, int totalSectors)
    {
        ArgumentNullException.ThrowIfNull(badLbas);
        var lbas = badLbas.Where(x => x >= 0).Distinct().ToList();
        return Classify(lbas, totalSectors);
    }

    // ---- core ---------------------------------------------------------------

    private static ErrorPatternReport Classify(List<int> lbas, int totalSectors)
    {
        lbas.Sort();
        var notes = new List<string>();

        if (lbas.Count == 0)
            return new ErrorPatternReport
            {
                TotalSectors = totalSectors,
                BadSectors = 0,
                Verdict = ErrorPatternKind.None,
                Lesions = Array.Empty<ErrorLesion>(),
                LongestRun = 0,
                Recommendation = "No failing sectors — the read is clean.",
                Notes = notes,
            };

        int longestRun = LongestContiguousRun(lbas);

        // Segment into lesions, keeping each cluster's LBAs so we can re-test scattered ones.
        var clusters = Cluster(lbas, LinkGap);
        var scored = clusters.Select(c => (lbas: c, cls: ClassifyLesion(c))).ToList();

        // A protection comb can be spaced wider than LinkGap, so its sectors land in separate
        // singleton/rot lesions. Re-test everything classified as scattered rot for a global
        // periodicity those separate lesions would hide.
        var scatter = scored.Where(s => s.cls.kind == ErrorPatternKind.SurfaceRot)
                            .SelectMany(s => s.lbas).OrderBy(x => x).ToArray();
        if (scatter.Length >= MinPeriodicSectors && IsPeriodic(scatter, out int gPeriod, out double gReg))
        {
            scored.RemoveAll(s => s.cls.kind == ErrorPatternKind.SurfaceRot);
            scored.Add((scatter, (ErrorPatternKind.DeliberatePattern, gReg,
                $"{scatter.Length} failing sectors form a comb across the disc, one roughly every " +
                $"{gPeriod} sectors ({gReg:P0} of the gaps) — a regular spacing physical damage does not produce.")));
            notes.Add("A wide-pitch periodic comb was detected across otherwise-scattered failures.");
        }

        var lesions = scored
            .OrderBy(s => s.lbas[0])
            .Select(s => new ErrorLesion(s.lbas[0], s.lbas[^1], s.lbas.Length, s.cls.kind, s.cls.conf, s.cls.why))
            .ToList();

        // Aggregate: physical (scratch + rot) vs deliberate, by how many sectors each covers.
        int physical = lesions.Where(l => l.Kind is ErrorPatternKind.Scratch or ErrorPatternKind.SurfaceRot).Sum(l => l.BadSectors);
        int deliberate = lesions.Where(l => l.Kind == ErrorPatternKind.DeliberatePattern).Sum(l => l.BadSectors);
        int scratch = lesions.Where(l => l.Kind == ErrorPatternKind.Scratch).Sum(l => l.BadSectors);
        int rot = lesions.Where(l => l.Kind == ErrorPatternKind.SurfaceRot).Sum(l => l.BadSectors);

        ErrorPatternKind verdict;
        int total = physical + deliberate;
        bool bothMeaningful = physical > 0 && deliberate > 0
                              && physical >= total * 0.05 && deliberate >= total * 0.05;
        if (bothMeaningful) verdict = ErrorPatternKind.Mixed;
        else if (deliberate > physical) verdict = ErrorPatternKind.DeliberatePattern;
        else verdict = scratch >= rot ? ErrorPatternKind.Scratch : ErrorPatternKind.SurfaceRot;

        if (longestRun >= 500 && verdict is ErrorPatternKind.Scratch)
            notes.Add($"The longest burst is {longestRun:N0} sectors. Most bursts this long are scratches, but a " +
                      "few protection schemes press a wide contiguous bad-sector band — if a recovery pass cannot " +
                      "improve it, preserve it as-is rather than discarding it.");

        return new ErrorPatternReport
        {
            TotalSectors = totalSectors,
            BadSectors = lbas.Count,
            Verdict = verdict,
            Lesions = lesions,
            LongestRun = longestRun,
            Recommendation = Recommend(verdict),
            Notes = notes,
        };
    }

    private static (ErrorPatternKind kind, double conf, string why) ClassifyLesion(int[] lbas)
    {
        int n = lbas.Length;
        int start = lbas[0], end = lbas[^1], span = end - start + 1;

        if (n == 1)
            return (ErrorPatternKind.SurfaceRot, 0.45,
                "a single isolated failing sector — most consistent with a surface defect / pinhole.");

        var gaps = new int[n - 1];
        int ones = 0;
        for (int i = 1; i < n; i++)
        {
            gaps[i - 1] = lbas[i] - lbas[i - 1];
            if (gaps[i - 1] == 1) ones++;
        }
        double onesFrac = ones / (double)gaps.Length;
        double density = n / (double)span;

        // Near-solid contiguous burst → scratch.
        if (span >= ScratchMinRun && (onesFrac >= 0.90 || density >= SolidDensity))
        {
            double conf = Math.Min(0.98, 0.6 + 0.4 * density);
            return (ErrorPatternKind.Scratch, conf,
                $"{n} failing sectors form a near-solid burst of {span} (density {density:P0}) — the signature of a radial scratch.");
        }

        // Regular spacing → deliberate pattern.
        if (n >= MinPeriodicSectors && IsPeriodic(lbas, out int period, out double reg))
            return (ErrorPatternKind.DeliberatePattern, Math.Min(0.98, reg),
                $"{n} failing sectors spaced regularly, roughly one every {period} sectors ({reg:P0} of the gaps) — " +
                "a periodic layout typical of a deliberate protection pattern, not stochastic damage.");

        // Otherwise: irregular scatter → rot.
        return (ErrorPatternKind.SurfaceRot, 0.55,
            $"{n} failing sectors scattered irregularly across {span} sectors — consistent with surface rot rather than a scratch or a pattern.");
    }

    // ---- geometry helpers ---------------------------------------------------

    // Group sorted LBAs into lesions, breaking whenever the gap exceeds linkGap.
    private static List<int[]> Cluster(List<int> sorted, int linkGap)
    {
        var clusters = new List<int[]>();
        int i = 0;
        while (i < sorted.Count)
        {
            int j = i + 1;
            while (j < sorted.Count && sorted[j] - sorted[j - 1] <= linkGap) j++;
            clusters.Add(sorted.GetRange(i, j - i).ToArray());
            i = j;
        }
        return clusters;
    }

    // The dominant inter-sector gap and the share of gaps that land on it (within 10%).
    // True when that share is regular enough and the period is more than a solid run.
    private static bool IsPeriodic(int[] sortedLbas, out int period, out double regularity)
    {
        period = 0;
        regularity = 0;
        if (sortedLbas.Length < MinPeriodicSectors) return false;

        var hist = new Dictionary<int, int>();
        for (int i = 1; i < sortedLbas.Length; i++)
        {
            int g = sortedLbas[i] - sortedLbas[i - 1];
            hist[g] = hist.GetValueOrDefault(g) + 1;
        }
        int gapCount = sortedLbas.Length - 1;

        int mode = 0, modeHits = 0;
        foreach (var (g, c) in hist)
            if (c > modeHits || (c == modeHits && g < mode)) { mode = g; modeHits = c; }

        if (mode <= 1) return false;   // a solid run, not a spaced pattern

        int tol = Math.Max(1, mode / 10);
        int within = 0;
        for (int i = 1; i < sortedLbas.Length; i++)
        {
            int g = sortedLbas[i] - sortedLbas[i - 1];
            if (Math.Abs(g - mode) <= tol) within++;
        }
        period = mode;
        regularity = within / (double)gapCount;
        return regularity >= RegularityThreshold;
    }

    private static int LongestContiguousRun(List<int> sorted)
    {
        int best = 1, run = 1;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == sorted[i - 1] + 1) run++;
            else run = 1;
            if (run > best) best = run;
        }
        return best;
    }

    private static string Recommend(ErrorPatternKind verdict) => verdict switch
    {
        ErrorPatternKind.Scratch =>
            "Physical damage. Re-read the affected region (ideally on a second drive), then recover with " +
            "reconstruct / dump-merge / ecc-repair. Clean the disc surface before re-reading.",
        ErrorPatternKind.SurfaceRot =>
            "Physical damage (surface rot). Re-read with retries and recover with reconstruct / dump-merge / " +
            "ecc-repair; scattered rot often yields to a few extra passes. Dump soon — rot spreads.",
        ErrorPatternKind.DeliberatePattern =>
            "This regular pattern is almost certainly part of the disc's design (copy protection), not damage. " +
            "Preserve it exactly — do not 'repair' these sectors — and record the pattern in the dump metadata.",
        ErrorPatternKind.Mixed =>
            "Recover the physical damage (reconstruct / dump-merge / ecc-repair) but preserve the regular " +
            "pattern as-is — repairing a protection pattern would corrupt a faithful dump.",
        _ => "No failing sectors — the read is clean.",
    };

    // ---- report rendering (convenience for the CLI) -------------------------

    public static string Render(ErrorPatternReport r, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{title}: {r.Summary()}");
        sb.AppendLine($"  failing sectors : {r.BadSectors:N0} of {r.TotalSectors:N0}  ·  longest burst {r.LongestRun:N0}");
        foreach (var l in r.Lesions)
            sb.AppendLine($"  {l}");
        sb.AppendLine($"  recommendation  : {r.Recommendation}");
        foreach (var note in r.Notes)
            sb.AppendLine($"  note: {note}");
        return sb.ToString().TrimEnd();
    }
}
