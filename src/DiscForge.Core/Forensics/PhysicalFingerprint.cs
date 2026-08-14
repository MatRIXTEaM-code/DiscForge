// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>A physical-copy signature: the positional pattern of a single disc's read errors,
/// quantised into bands across the surface. Manufacturing defects and handling marks put errors
/// at radii unique to <i>this</i> physical disc, and those stay put read to read.</summary>
public sealed record PhysicalFingerprint
{
    public required string DiscId { get; init; }
    public required int Bands { get; init; }
    /// <summary>Per-band error intensity, 0–255 (log-scaled).</summary>
    public required byte[] Profile { get; init; }
    /// <summary>Bands carrying a significant defect — the disc's "constellation".</summary>
    public required IReadOnlyList<int> DefectBands { get; init; }
    /// <summary>How much unique defect information this fingerprint carries, 0–1. A near-clean disc
    /// scores low: there is nothing distinctive to fingerprint, so a match cannot be claimed.</summary>
    public required double Distinctiveness { get; init; }
    public required long TotalErrors { get; init; }

    /// <summary>A short hex id of the profile — a label, not an identity claim.</summary>
    public string ShortId => System.Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Profile))[..12].ToLowerInvariant();
}

/// <summary>How two physical fingerprints relate.</summary>
public sealed record PhysicalMatch
{
    public required double Similarity { get; init; }
    public required double LocationOverlap { get; init; }
    /// <summary>Both fingerprints carry enough defect information to decide.</summary>
    public required bool Distinctive { get; init; }
    public required bool SamePhysicalCopy { get; init; }
    public required string Assessment { get; init; }
}

/// <summary>
/// Physical-copy fingerprinting — identify the individual disc, not just the title. The disc genome
/// tells you <i>which game</i>; this tells you <i>which physical copy</i>, because every pressing and
/// every scuffed hand-me-down carries read errors at radii unique to it — the optical equivalent of
/// ballistics. The fingerprint is the positional pattern of C1/C2 errors quantised into bands; two
/// scans of the <i>same</i> disc share that constellation (defects don't move, and rot only adds to
/// it), while a different copy of the same title has its errors elsewhere. Crucially it reports
/// distinctiveness: a near-flawless disc has no signature to match, and the tool says so rather than
/// guessing. Pure measurement — it reads error positions and compares, and changes nothing.
/// </summary>
public static class PhysicalFingerprinter
{
    private const int DefaultBands = 64;
    // A band counts as a defect when its intensity and its raw error count both clear these floors.
    private const int DefectIntensityCutoff = 24;   // of 255
    private const long DefectRawFloor = 4;
    private const double DistinctiveEnough = 0.5;
    private const double SameCopyThreshold = 0.75;

    /// <summary>Compute a fingerprint from a positional error scan (samples ordered across the surface).</summary>
    public static PhysicalFingerprint Compute(string discId, IReadOnlyList<ScanSample> positionalScan, int bands = DefaultBands)
    {
        ArgumentException.ThrowIfNullOrEmpty(discId);
        ArgumentNullException.ThrowIfNull(positionalScan);
        if (bands < 1) bands = 1;

        int n = positionalScan.Count;
        var raw = new long[bands];
        long total = 0;

        if (n > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var s = positionalScan[i];
                // C2 and CU weigh more — they mark worse, more copy-specific damage.
                long weight = s.C1 + 4L * s.C2 + 16L * s.Cu;
                int band = (int)((long)i * bands / n);
                if (band >= bands) band = bands - 1;
                raw[band] += weight;
                total += weight;
            }
        }

        long max = 0;
        foreach (var r in raw) if (r > max) max = r;

        var profile = new byte[bands];
        var defectBands = new List<int>();
        double logMax = Math.Log(1 + max);
        for (int b = 0; b < bands; b++)
        {
            byte v = max == 0 ? (byte)0 : (byte)Math.Round(255 * Math.Log(1 + raw[b]) / logMax);
            profile[b] = v;
            if (v >= DefectIntensityCutoff && raw[b] >= DefectRawFloor) defectBands.Add(b);
        }

        // Distinctiveness saturates as more bands carry defects; a couple of specks is weak evidence.
        double distinctiveness = total < DefectRawFloor ? 0 : 1 - Math.Exp(-defectBands.Count / 4.0);

        return new PhysicalFingerprint
        {
            DiscId = discId,
            Bands = bands,
            Profile = profile,
            DefectBands = defectBands,
            Distinctiveness = distinctiveness,
            TotalErrors = total,
        };
    }

    /// <summary>Compare two fingerprints. Only claims "same physical copy" when both are distinctive.</summary>
    public static PhysicalMatch Compare(PhysicalFingerprint a, PhysicalFingerprint b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Bands != b.Bands)
            throw new ArgumentException("Fingerprints must have the same band count to compare.");

        double pearson = Math.Max(0, Pearson(a.Profile, b.Profile));
        // Overlap coefficient (not Jaccard) so a copy that has since grown extra rot still matches:
        // the older defect set stays a subset of the newer one.
        double overlap = OverlapCoefficient(a.DefectBands, b.DefectBands);
        double similarity = 0.6 * pearson + 0.4 * overlap;

        bool distinctive = a.Distinctiveness >= DistinctiveEnough && b.Distinctiveness >= DistinctiveEnough;
        bool same = distinctive && similarity >= SameCopyThreshold;

        string assessment = !distinctive
            ? "One or both discs are too clean to fingerprint — not enough defect structure to identify an individual copy."
            : same
                ? $"Same physical copy (defect constellation matches {similarity:P0}; overlap {overlap:P0})."
                : $"Different physical copies (defect patterns disagree — similarity {similarity:P0}).";

        return new PhysicalMatch
        {
            Similarity = similarity,
            LocationOverlap = overlap,
            Distinctive = distinctive,
            SamePhysicalCopy = same,
            Assessment = assessment,
        };
    }

    // ---- internals ----------------------------------------------------------

    private static double Pearson(byte[] x, byte[] y)
    {
        int n = x.Length;
        double mx = 0, my = 0;
        for (int i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
        mx /= n; my /= n;
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        if (sxx <= 1e-9 || syy <= 1e-9) return 0;   // a flat profile carries no correlation
        return sxy / Math.Sqrt(sxx * syy);
    }

    private static double OverlapCoefficient(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var setB = new HashSet<int>(b);
        int inter = a.Count(x => setB.Contains(x));
        return inter / (double)Math.Min(a.Count, b.Count);
    }

    public static string Render(PhysicalFingerprint f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{f.DiscId}: physical fingerprint {f.ShortId} — {f.DefectBands.Count} defect band(s) of {f.Bands}, " +
                      $"{f.TotalErrors:N0} weighted errors, distinctiveness {f.Distinctiveness:P0}.");
        if (f.Distinctiveness < DistinctiveEnough)
            sb.AppendLine("  (Too clean to serve as a reliable individual-copy fingerprint.)");
        return sb.ToString().TrimEnd();
    }
}
