// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>Errors counted in one scan interval (conventionally one second of playback):
/// C1 (correctable at the first ECC layer — this is BLER), C2 (passed to the second layer,
/// serious), and CU (uncorrectable — actual data loss).</summary>
public readonly record struct ScanSample(int C1, int C2, int Cu);

/// <summary>A disc's error-scan health, ordered by severity.</summary>
public enum DiscHealthGrade : byte
{
    Pristine = 0,
    Good = 1,
    Fair = 2,
    Poor = 3,
    Failing = 4,
}

/// <summary>How soon a disc needs to be dumped, from its rot trend.</summary>
public enum RotUrgency : byte
{
    None = 0,     // stable and healthy
    Watch = 1,    // degrading slowly; check again later
    Soon = 2,     // dump within the year
    Urgent = 3,   // dump within ~3 months
    Critical = 4, // dump now — already failing or about to
}

/// <summary>One error scan of one disc at one point in time. Build it from raw per-interval
/// samples with <see cref="FromSamples"/>, which computes the summary statistics.</summary>
public sealed record ErrorScan
{
    public required string DiscId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? Drive { get; init; }

    public required int SampleCount { get; init; }
    /// <summary>Peak C1 per interval — the classic BLER figure (Red Book max average is 220).</summary>
    public required int MaxC1 { get; init; }
    public required double AvgC1 { get; init; }
    public required long TotalC1 { get; init; }
    public required int MaxC2 { get; init; }
    public required long TotalC2 { get; init; }
    public required long TotalCu { get; init; }

    /// <summary>Red Book's maximum acceptable average block-error rate.</summary>
    public const int RedBookMaxBler = 220;

    public bool HasUncorrectable => TotalCu > 0;
    public bool HasC2 => TotalC2 > 0;

    /// <summary>A single quality grade from this scan, using the standard error thresholds.</summary>
    public DiscHealthGrade Grade
    {
        get
        {
            if (HasUncorrectable) return DiscHealthGrade.Failing;
            if (HasC2) return MaxC2 > 50 || TotalC2 > 500 ? DiscHealthGrade.Failing : DiscHealthGrade.Poor;
            if (MaxC1 > RedBookMaxBler) return DiscHealthGrade.Failing;
            if (MaxC1 > 100) return DiscHealthGrade.Poor;
            if (MaxC1 > 50) return DiscHealthGrade.Fair;
            if (MaxC1 > 20 || AvgC1 >= 5) return DiscHealthGrade.Good;
            return DiscHealthGrade.Pristine;
        }
    }

    public static ErrorScan FromSamples(string discId, DateTimeOffset timestamp,
                                        IReadOnlyList<ScanSample> samples, string? drive = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(discId);
        ArgumentNullException.ThrowIfNull(samples);

        int maxC1 = 0, maxC2 = 0;
        long totC1 = 0, totC2 = 0, totCu = 0;
        foreach (var s in samples)
        {
            if (s.C1 > maxC1) maxC1 = s.C1;
            if (s.C2 > maxC2) maxC2 = s.C2;
            totC1 += s.C1;
            totC2 += s.C2;
            totCu += s.Cu;
        }
        return new ErrorScan
        {
            DiscId = discId,
            Timestamp = timestamp,
            Drive = drive,
            SampleCount = samples.Count,
            MaxC1 = maxC1,
            AvgC1 = samples.Count == 0 ? 0 : totC1 / (double)samples.Count,
            TotalC1 = totC1,
            MaxC2 = maxC2,
            TotalC2 = totC2,
            TotalCu = totCu,
        };
    }
}

/// <summary>The rot forecast for one disc, from its scan history.</summary>
public sealed record RotForecast
{
    public required string DiscId { get; init; }
    public required DiscHealthGrade CurrentGrade { get; init; }
    public required int ScanCount { get; init; }
    /// <summary>Trend of peak BLER, in C1/year (positive = worsening). Zero when unknown.</summary>
    public required double BlerPerYear { get; init; }
    /// <summary>Trend of C2 errors, per year. Zero when unknown.</summary>
    public required double C2PerYear { get; init; }
    /// <summary>Projected days until the disc crosses into failure, or null if not trending there
    /// (stable/improving, only one scan, or already failing).</summary>
    public required double? DaysToCritical { get; init; }
    public required bool AlreadyCritical { get; init; }
    public required RotUrgency Urgency { get; init; }
    public required string Assessment { get; init; }
}

/// <summary>
/// Disc-rot triage / actuarial prediction — track a disc's C1/C2 error scans over time and predict
/// which discs are dying, so the failing ones get dumped first. Optical rot shows up as a rising
/// block-error rate (C1/BLER) long before data is actually lost, and as C2 errors once the first
/// correction layer starts to be overwhelmed. Given a history of scans this fits the trend of those
/// error rates, projects when the disc will cross into failure (Red Book's BLER limit, or the onset
/// of C2/uncorrectable errors), and ranks a whole collection by urgency — an actuarial "dump-order"
/// for a shelf of aging discs. Reads scan results and forecasts; it never alters a disc.
/// </summary>
public static class DiscRotTriage
{
    private const double DaysPerYear = 365.25;
    /// <summary>Peak per-interval C2 at which the grade is Failing — the C2 projection target.</summary>
    private const int FailingMaxC2 = 50;

    /// <summary>Forecast one disc's fate from its scan history (any order; sorted internally).</summary>
    public static RotForecast Forecast(IReadOnlyList<ErrorScan> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0)
            throw new ArgumentException("Need at least one scan to forecast.", nameof(history));

        var scans = history.OrderBy(s => s.Timestamp).ToList();
        var current = scans[^1];
        string discId = current.DiscId;
        var grade = current.Grade;

        bool alreadyCritical = current.HasUncorrectable || grade == DiscHealthGrade.Failing;

        double blerSlope = 0, c2Slope = 0;
        double? daysToCritical = null;

        if (scans.Count >= 2)
        {
            blerSlope = SlopePerYear(scans, s => s.MaxC1);
            c2Slope = SlopePerYear(scans, s => s.MaxC2);

            if (!alreadyCritical)
            {
                // Time until peak BLER reaches the Red Book ceiling at the current rate.
                if (blerSlope > 0 && current.MaxC1 < ErrorScan.RedBookMaxBler)
                {
                    double years = (ErrorScan.RedBookMaxBler - current.MaxC1) / blerSlope;
                    daysToCritical = years * DaysPerYear;
                }
                // C2 is the more serious signal: project peak C2 reaching the failing level (50).
                // When C2 is emerging this usually arrives before the BLER ceiling — take the nearer.
                if (c2Slope > 0 && current.MaxC2 < FailingMaxC2)
                {
                    double years = (FailingMaxC2 - current.MaxC2) / c2Slope;
                    double days = years * DaysPerYear;
                    daysToCritical = daysToCritical is { } d ? Math.Min(d, days) : days;
                }
            }
        }

        var urgency = Classify(grade, alreadyCritical, daysToCritical);
        // Any C2 in the latest scan means the first correction layer is already being overrun —
        // that disc warrants dumping soon even if the linear projection looks distant.
        if (current.HasC2 && !alreadyCritical && urgency < RotUrgency.Soon)
            urgency = RotUrgency.Soon;
        string assessment = Describe(current, grade, scans.Count, blerSlope, c2Slope, daysToCritical, alreadyCritical);

        return new RotForecast
        {
            DiscId = discId,
            CurrentGrade = grade,
            ScanCount = scans.Count,
            BlerPerYear = blerSlope,
            C2PerYear = c2Slope,
            DaysToCritical = daysToCritical,
            AlreadyCritical = alreadyCritical,
            Urgency = urgency,
            Assessment = assessment,
        };
    }

    /// <summary>Forecast a whole collection and return it in recommended dump order — most urgent
    /// first, then soonest-to-fail, then by disc id.</summary>
    public static IReadOnlyList<RotForecast> Prioritize(IEnumerable<IReadOnlyList<ErrorScan>> discs)
    {
        ArgumentNullException.ThrowIfNull(discs);
        return discs
            .Where(h => h is { Count: > 0 })
            .Select(Forecast)
            .OrderByDescending(f => f.Urgency)
            .ThenBy(f => f.DaysToCritical ?? double.PositiveInfinity)
            .ThenBy(f => f.DiscId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Render(IReadOnlyList<RotForecast> forecasts)
    {
        ArgumentNullException.ThrowIfNull(forecasts);
        var sb = new StringBuilder();
        int dump = forecasts.Count(f => f.Urgency >= RotUrgency.Soon);
        sb.AppendLine($"{forecasts.Count} disc(s) triaged — {dump} need dumping soon. Recommended dump order:");
        int i = 0;
        foreach (var f in forecasts)
        {
            string eta = f.AlreadyCritical ? "now"
                : f.DaysToCritical is { } d ? $"~{d / DaysPerYear:0.#} yr" : "stable";
            sb.AppendLine($"  {++i}. {f.DiscId} — {f.Urgency} · {f.CurrentGrade} · to-critical {eta}");
            sb.AppendLine($"       {f.Assessment}");
        }
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    // Ordinary-least-squares slope of metric vs time, expressed per year.
    private static double SlopePerYear(List<ErrorScan> scans, Func<ErrorScan, double> metric)
    {
        var t0 = scans[0].Timestamp;
        double meanX = 0, meanY = 0;
        var xs = new double[scans.Count];
        var ys = new double[scans.Count];
        for (int i = 0; i < scans.Count; i++)
        {
            xs[i] = (scans[i].Timestamp - t0).TotalDays / DaysPerYear;   // years since first scan
            ys[i] = metric(scans[i]);
            meanX += xs[i];
            meanY += ys[i];
        }
        meanX /= scans.Count;
        meanY /= scans.Count;

        double num = 0, den = 0;
        for (int i = 0; i < scans.Count; i++)
        {
            double dx = xs[i] - meanX;
            num += dx * (ys[i] - meanY);
            den += dx * dx;
        }
        return den <= 1e-9 ? 0 : num / den;
    }

    private static RotUrgency Classify(DiscHealthGrade grade, bool alreadyCritical, double? daysToCritical)
    {
        if (alreadyCritical) return RotUrgency.Critical;
        if (daysToCritical is { } d)
        {
            if (d <= 90) return RotUrgency.Urgent;
            if (d <= 365) return RotUrgency.Soon;
            if (d <= 5 * 365) return RotUrgency.Watch;
        }
        // No worsening trend: urgency follows the current grade.
        return grade switch
        {
            DiscHealthGrade.Poor => RotUrgency.Soon,
            DiscHealthGrade.Fair => RotUrgency.Watch,
            _ => RotUrgency.None,
        };
    }

    private static string Describe(ErrorScan current, DiscHealthGrade grade, int scanCount,
                                   double blerSlope, double c2Slope, double? daysToCritical, bool alreadyCritical)
    {
        if (alreadyCritical)
            return current.HasUncorrectable
                ? $"Uncorrectable (CU) errors present — data is already being lost. Dump immediately while it still reads."
                : $"Grade {grade} with sustained C2 errors — the disc is failing. Dump immediately.";

        if (scanCount < 2)
            return $"Single scan: grade {grade} (peak BLER {current.MaxC1}). No trend yet — re-scan later to establish a rot rate.";

        string trend = blerSlope > 0.5
            ? $"peak BLER rising ~{blerSlope:0.#}/yr"
            : blerSlope < -0.5 ? $"peak BLER falling ~{-blerSlope:0.#}/yr" : "peak BLER stable";
        string c2 = c2Slope > 0.5 ? $", C2 emerging ~{c2Slope:0.#}/yr" : "";

        if (daysToCritical is { } d)
            return $"Grade {grade}; {trend}{c2}. At this rate it crosses into failure in ~{d / DaysPerYear:0.#} years.";
        return $"Grade {grade}; {trend}{c2}. No worsening trend — stable for now.";
    }
}
