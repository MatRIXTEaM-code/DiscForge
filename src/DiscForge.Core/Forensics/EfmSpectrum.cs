// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Forensics;

/// <summary>The run-length spectrum and DC balance of an EFM channel stream — the encoding-domain
/// determinants of how much margin the physical signal will have.</summary>
public sealed record EfmSpectrumReport
{
    /// <summary>Count of each pit/land run length, indexed by T (3..11 legal); index 0..2 and 12+ hold
    /// out-of-constraint runs when present.</summary>
    public required IReadOnlyList<int> RunHistogram { get; init; }
    public required int TotalRuns { get; init; }
    public required int ChannelBits { get; init; }

    /// <summary>Runs outside the legal 3T..11T window — should be zero for a conforming stream.</summary>
    public required int ConstraintViolations { get; init; }
    public bool ConstraintOk => ConstraintViolations == 0;

    /// <summary>Total channel time spent at pit level and at land level, in T units.</summary>
    public required long PitTimeT { get; init; }
    public required long LandTimeT { get; init; }

    /// <summary>Pit/land duty asymmetry, (pit − land)/(pit + land). Zero is perfect DC balance; the EFM
    /// merging bits exist to drive it there, so a large magnitude is a stressed stream. This is the
    /// encoding-domain analogue of the analog HF-signal asymmetry (β) — it does not measure the RF eye,
    /// it measures the balance the RF eye will inherit.</summary>
    public double DutyAsymmetry => PitTimeT + LandTimeT == 0
        ? 0 : (double)(PitTimeT - LandTimeT) / (PitTimeT + LandTimeT);

    /// <summary>Peak absolute Digital Sum Value — how far DC balance wandered; the servo's headroom.</summary>
    public required int MaxAbsDsv { get; init; }
    public required int EndDsv { get; init; }
    public required double MeanRunT { get; init; }

    /// <summary>Fraction of runs that are the shortest (3T) — the highest-frequency content, the hardest
    /// for the optics to resolve. A high share means a demanding stream.</summary>
    public double I3Fraction => TotalRuns == 0 ? 0 : RunHistogram[3] / (double)TotalRuns;
    /// <summary>Fraction of runs that are the longest legal length (11T) — the lowest-frequency content,
    /// where DC balance and servo tracking are stressed.</summary>
    public double I11Fraction => TotalRuns == 0 ? 0 : RunHistogram[11] / (double)TotalRuns;

    /// <summary>Normalised Shannon entropy of the run-length distribution over the 3T..11T bins (0..1). A
    /// healthy stream spreads energy across the spectrum (high); one dominated by a single run length is
    /// spectrally poor (low).</summary>
    public required double SpectralEntropy { get; init; }

    /// <summary>A coarse letter grade from the derivable structural metrics. Heuristic, not an analog
    /// signal-quality measurement.</summary>
    public string Grade()
    {
        if (!ConstraintOk) return "F";                       // illegal runs — not a readable stream
        double asym = Math.Abs(DutyAsymmetry);
        double dsvRatio = ChannelBits == 0 ? 0 : MaxAbsDsv / (double)ChannelBits;
        if (asym < 0.02 && SpectralEntropy > 0.9 && dsvRatio < 0.02) return "A";
        if (asym < 0.05 && SpectralEntropy > 0.8) return "B";
        if (asym < 0.12) return "C";
        return "D";
    }

    public string Summary() => TotalRuns == 0
        ? "No EFM runs to analyse."
        : $"EFM spectrum: {TotalRuns:N0} runs, grade {Grade()} — " +
          $"duty asymmetry {DutyAsymmetry:0.0000}, I3 {I3Fraction:P1}, I11 {I11Fraction:P1}, " +
          $"entropy {SpectralEntropy:0.00}, peak |DSV| {MaxAbsDsv}" +
          (ConstraintOk ? "." : $", {ConstraintViolations} illegal run(s)!");
}

/// <summary>
/// EFM spectrum — the physical-quality read one layer below the bytes. It encodes data through the EFM
/// channel code and measures the shape of the resulting pit/land stream: the run-length spectrum (how
/// often each 3T..11T length occurs), the pit/land duty asymmetry (the DC balance the RF eye inherits),
/// the DSV excursion (the servo's headroom), and the spectral entropy (whether energy is spread across
/// the run lengths or piled onto one). Short (3T) runs are the hardest content to resolve and long (11T)
/// runs stress DC balance, so their fractions bound how demanding the stream is on the drive.
///
/// It works in the ENCODING domain: from ideal channel bits it derives the structural properties that
/// determine readability — it does not, and cannot, measure true analog jitter or the β asymmetry of a
/// real RF eye, which need the recovered HF signal. It is the substrate those measurements ride on,
/// modelled faithfully. Read-only; it changes nothing.
/// </summary>
public static class EfmSpectrum
{
    public const int MinT = 3, MaxT = 11;

    /// <summary>Analyse the EFM channel stream produced by <paramref name="data"/>.</summary>
    public static EfmSpectrumReport Analyze(ReadOnlySpan<byte> data)
        => AnalyzeChannel(Efm.Encode(data));

    /// <summary>Analyse an already-encoded channel-bit stream (true = transition, NRZI).</summary>
    public static EfmSpectrumReport AnalyzeChannel(ReadOnlySpan<bool> bits)
    {
        var hist = new int[13];                              // indices 0..12; 3..11 legal
        int level = +1, dsv = 0, maxAbs = 0, run = 0;
        int violations = 0, totalRuns = 0;
        long pitT = 0, landT = 0, runSum = 0;
        bool seen = false;

        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                if (seen)
                {
                    int t = run + 1;                         // `run` zeros between transitions = (run+1)T
                    int completedLevel = level;             // the run was held at the pre-toggle level
                    if (t >= MinT && t <= MaxT) hist[t]++;
                    else { violations++; hist[Math.Clamp(t, 0, 12)]++; }
                    if (completedLevel > 0) pitT += t; else landT += t;
                    runSum += t; totalRuns++;
                }
                level = -level;
                seen = true;
                run = 0;
            }
            else run++;
            dsv += level;
            maxAbs = Math.Max(maxAbs, Math.Abs(dsv));
        }

        double entropy = 0;
        if (totalRuns > 0)
        {
            for (int t = MinT; t <= MaxT; t++)
            {
                if (hist[t] == 0) continue;
                double p = hist[t] / (double)totalRuns;
                entropy -= p * Math.Log2(p);
            }
            entropy /= Math.Log2(MaxT - MinT + 1);           // normalise to [0,1] over 9 bins
        }

        return new EfmSpectrumReport
        {
            RunHistogram = hist,
            TotalRuns = totalRuns,
            ChannelBits = bits.Length,
            ConstraintViolations = violations,
            PitTimeT = pitT,
            LandTimeT = landT,
            MaxAbsDsv = maxAbs,
            EndDsv = dsv,
            MeanRunT = totalRuns == 0 ? 0 : (double)runSum / totalRuns,
            SpectralEntropy = entropy,
        };
    }

    public static string Render(EfmSpectrumReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        if (r.TotalRuns == 0) return sb.ToString().TrimEnd();

        int peak = 0;
        for (int t = MinT; t <= MaxT; t++) peak = Math.Max(peak, r.RunHistogram[t]);
        for (int t = MinT; t <= MaxT; t++)
        {
            int n = r.RunHistogram[t];
            int bar = peak == 0 ? 0 : (int)Math.Round(40.0 * n / peak);
            sb.AppendLine($"  {t,2}T {new string('#', bar).PadRight(40)} {n,8:N0}  {(double)n / r.TotalRuns:P1}");
        }
        return sb.ToString().TrimEnd();
    }
}
