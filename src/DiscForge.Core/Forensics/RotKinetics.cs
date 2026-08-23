// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>One longitudinal measurement of a disc's error rate at a point in time.</summary>
public readonly record struct RotSample(DateTimeOffset Time, double ErrorRate);

/// <summary>The optional storage environment a forecast is conditioned on.</summary>
public readonly record struct StorageEnvironment(double TempC, double RelHumidityPct);

/// <summary>A physics-grounded rot forecast: a first-order (exponential) degradation fit to a disc's error
/// history, projected to the point it crosses a failure threshold, optionally accelerated/decelerated for
/// a storage environment via an Arrhenius/Eyring factor.</summary>
public sealed record RotKineticsResult
{
    /// <summary>Relative growth rate of the error metric, per year (the k in error ∝ e^{k·t}).</summary>
    public required double GrowthPerYear { get; init; }
    /// <summary>Fitted error rate at the first scan.</summary>
    public required double InitialRate { get; init; }
    /// <summary>Goodness of fit of ln(error) vs time (0–1).</summary>
    public required double RSquared { get; init; }
    public required int SampleCount { get; init; }
    public required double Threshold { get; init; }

    /// <summary>Years from the first scan until the fit crosses the threshold, or null if it isn't trending
    /// there (stable/improving), already past it, or too few points.</summary>
    public required double? YearsToThreshold { get; init; }
    public required DateTimeOffset? ThresholdDate { get; init; }
    /// <summary>An uncertainty band on the threshold date, from the slope's standard error.</summary>
    public (DateTimeOffset Early, DateTimeOffset Late)? Band { get; init; }

    /// <summary>The environmental acceleration applied (1.0 = none). &gt;1 means the given storage is harsher
    /// than the reference and shortens life; &lt;1 means kinder.</summary>
    public double EnvAccelFactor { get; init; } = 1.0;
    public required bool AlreadyFailing { get; init; }
    public required string Assessment { get; init; }
}

/// <summary>
/// rot-kinetics — model optical-disc decay as a first-order chemical process rather than a straight line.
/// Dye/reflective-layer degradation compounds, so the block-error rate grows roughly exponentially long
/// before data is lost; fitting ln(error) against time recovers a growth constant and projects when the
/// disc crosses a failure threshold, with a confidence band. Given a storage temperature/humidity it scales
/// the forecast by an Arrhenius/Eyring acceleration factor (using a published activation-energy estimate
/// for optical media) to translate shelf-life between environments. Modelling and prioritisation only.
/// </summary>
public static class RotKinetics
{
    /// <summary>Red Book's peak block-error-rate ceiling — the default "unrecoverable soon" threshold.</summary>
    public const double DefaultThreshold = 220;

    /// <summary>Projections beyond this horizon are reported as "no crossing": a
    /// near-zero fitted slope extrapolates to absurd dates (and once overflowed
    /// DateTimeOffset), and a multi-century forecast from a few scans is noise
    /// dressed as precision, not a finding.</summary>
    public const double MaxProjectionYears = 500;

    /// <summary>Boltzmann constant in eV/K.</summary>
    private const double KB = 8.617333e-5;
    /// <summary>A representative activation energy for optical-media decay (eV); literature places dye/reflective
    /// degradation around 0.7–1.1 eV — 0.9 is a mid estimate used as the default.</summary>
    public const double DefaultActivationEnergyEv = 0.9;
    /// <summary>Reference storage the shelf-life is quoted at: 25 °C, 50 % RH.</summary>
    private static readonly StorageEnvironment Reference = new(25, 50);
    /// <summary>Humidity acceleration coefficient (per %RH) in the Eyring RH term — modest, heuristic.</summary>
    private const double HumidityCoeff = 0.015;

    private const double DaysPerYear = 365.25;

    public static RotKineticsResult Fit(
        IReadOnlyList<RotSample> history,
        double threshold = DefaultThreshold,
        StorageEnvironment? storage = null,
        double activationEnergyEv = DefaultActivationEnergyEv)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count < 2)
            throw new ArgumentException("Need at least two scans to fit a rot rate.", nameof(history));

        var pts = history.OrderBy(s => s.Time).ToList();
        var t0 = pts[0].Time;

        // Linear regression of y = ln(max(error,1)) against x = years since first scan.
        int n = pts.Count;
        var xs = new double[n];
        var ys = new double[n];
        double mx = 0, my = 0;
        for (int i = 0; i < n; i++)
        {
            xs[i] = (pts[i].Time - t0).TotalDays / DaysPerYear;
            ys[i] = Math.Log(Math.Max(pts[i].ErrorRate, 1.0));
            mx += xs[i]; my += ys[i];
        }
        mx /= n; my /= n;

        double sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = xs[i] - mx, dy = ys[i] - my;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        double k = sxx <= 1e-12 ? 0 : sxy / sxx;      // growth per year
        double b = my - k * mx;                        // intercept (ln error at t0)
        double e0 = Math.Exp(b);
        double r2 = (sxx <= 1e-12 || syy <= 1e-12) ? 0 : (sxy * sxy) / (sxx * syy);

        // Standard error of the slope, for the confidence band.
        double slopeSe = 0;
        if (n > 2 && sxx > 1e-12)
        {
            double ssRes = syy - k * sxy;              // residual sum of squares
            double s2 = Math.Max(ssRes, 0) / (n - 2);
            slopeSe = Math.Sqrt(s2 / sxx);
        }

        double lastError = pts[^1].ErrorRate;
        bool alreadyFailing = lastError >= threshold;

        double env = storage is { } s ? AccelFactor(s, activationEnergyEv) : 1.0;

        double? yearsToThreshold = null;
        DateTimeOffset? date = null;
        (DateTimeOffset, DateTimeOffset)? band = null;

        if (!alreadyFailing && k > 1e-6 && threshold > e0)
        {
            // Years from the first scan until the fit reaches the threshold, at the disc's own (reference-
            // environment) rate; the remaining time from the last scan is then compressed by the storage
            // acceleration (harsher env → higher effective rate → sooner).
            double RemainingFromLast(double slope)
            {
                double xFail = (Math.Log(threshold) - b) / slope;   // years from t0 at this slope
                return (xFail - xs[^1]) / Math.Max(env, 1e-9);       // remaining from last scan, env-scaled
            }

            double yearsFromLast = RemainingFromLast(k);
            if (yearsFromLast > 0 && yearsFromLast <= MaxProjectionYears)
            {
                yearsToThreshold = yearsFromLast;
                date = pts[^1].Time.AddDays(yearsFromLast * DaysPerYear);
                if (slopeSe > 1e-9)
                {
                    double early = RemainingFromLast(k + slopeSe);   // steeper → sooner
                    double late = RemainingFromLast(Math.Max(k - slopeSe, 1e-6));  // shallower → later
                    if (early > 0 && late > 0 && late <= MaxProjectionYears)
                        band = (pts[^1].Time.AddDays(early * DaysPerYear), pts[^1].Time.AddDays(late * DaysPerYear));
                }
            }
        }

        string assessment = Describe(k, r2, e0, threshold, alreadyFailing, yearsToThreshold, env, n);

        return new RotKineticsResult
        {
            GrowthPerYear = k,
            InitialRate = e0,
            RSquared = r2,
            SampleCount = n,
            Threshold = threshold,
            YearsToThreshold = yearsToThreshold,
            ThresholdDate = date,
            Band = band,
            EnvAccelFactor = env,
            AlreadyFailing = alreadyFailing,
            Assessment = assessment,
        };
    }

    /// <summary>The Arrhenius (temperature) × Eyring (humidity) acceleration of the storage environment
    /// relative to the 25 °C / 50 %RH reference. &gt;1 means faster decay (shorter life).</summary>
    public static double AccelFactor(StorageEnvironment storage, double activationEnergyEv = DefaultActivationEnergyEv)
    {
        double tK = storage.TempC + 273.15;
        double tRefK = Reference.TempC + 273.15;
        double arrhenius = Math.Exp((activationEnergyEv / KB) * (1.0 / tRefK - 1.0 / tK));
        double eyringRh = Math.Exp(HumidityCoeff * (storage.RelHumidityPct - Reference.RelHumidityPct));
        return arrhenius * eyringRh;
    }

    private static string Describe(double k, double r2, double e0, double threshold,
                                   bool failing, double? years, double env, int n)
    {
        if (failing)
            return $"Already at/above the failure threshold ({threshold:0}) — dump immediately.";
        string envNote = Math.Abs(env - 1.0) < 0.02 ? ""
            : env > 1 ? $" (storage {env:0.0}× harsher than reference — brought forward)"
                      : $" (storage {1 / env:0.0}× kinder than reference — pushed back)";
        if (k <= 1e-6)
            return $"Error rate is stable or improving (fit k≈{k:0.###}/yr, R²={r2:0.00}) — no rot trend from {n} scans.";
        if (years is { } y)
            return $"First-order decay: error growing ~{(Math.Exp(k) - 1) * 100:0}%/yr (R²={r2:0.00}); " +
                   $"crosses {threshold:0} in ~{y:0.0} yr{envNote}.";
        return $"Growing ~{(Math.Exp(k) - 1) * 100:0}%/yr but already near/over threshold or projection unstable.";
    }

    public static string Render(RotKineticsResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder(r.Assessment);
        if (r.ThresholdDate is { } d)
        {
            sb.Append($"\n  Projected threshold-crossing: {d:yyyy-MM}");
            if (r.Band is { } band) sb.Append($"  (band {band.Early:yyyy-MM} … {band.Late:yyyy-MM})");
        }
        return sb.ToString();
    }
}
