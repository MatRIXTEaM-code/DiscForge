// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;
using DiscForge.Core.Util;

namespace DiscForge.Core.Forensics;

/// <summary>One Data-Position measurement: the read speed observed at a given LBA (any positive unit —
/// an X-factor or KB/s; the analysis is scale-invariant).</summary>
public sealed record DpmSample(int Lba, double Speed);

/// <summary>A region reading markedly slower than its neighbourhood — a physical ring or a damaged band.</summary>
public sealed record DpmAnomaly(int StartLba, int EndLba, int SampleCount, double MinSpeed, double LocalBaseline, double DepthFraction)
{
    /// <summary>LBA extent the anomaly covers (End − Start + 1); depends on sampling density.</summary>
    public int Span => EndLba - StartLba + 1;
    public override string ToString() =>
        $"LBA {StartLba}–{EndLba} ({SampleCount} samples): {DepthFraction:P0} below baseline (min {MinSpeed:0.00} vs {LocalBaseline:0.00})";
}

/// <summary>What the timing profile looks like.</summary>
public enum DpmVerdict : byte
{
    /// <summary>A smooth profile — no positional anomaly.</summary>
    Clean,
    /// <summary>One or a few sharp, narrow slowdowns — the signature of a deliberate physical ring
    /// (SecuROM/StarForce-style) written at a fixed radius.</summary>
    RingLike,
    /// <summary>Many or broad irregular slowdowns — reads more like surface damage than a ring.</summary>
    DamageLike,
}

/// <summary>The read-only DPM verdict for a disc's read-timing profile.</summary>
public sealed record DpmReport
{
    public required int Samples { get; init; }
    public required double MinSpeed { get; init; }
    public required double MaxSpeed { get; init; }
    public required double MeanSpeed { get; init; }
    public required IReadOnlyList<DpmAnomaly> Anomalies { get; init; }
    public required DpmVerdict Verdict { get; init; }

    /// <summary>A scale-invariant fingerprint of the speed-profile SHAPE (CRC-16 over the normalised,
    /// down-sampled curve). Two dumps of the same physical disc fingerprint identically even from drives
    /// of different speed, so a copy pressed from a different master is distinguishable and a database can
    /// match the pressing.</summary>
    public required ushort Fingerprint { get; init; }

    public string Summary()
    {
        if (Samples == 0) return "No DPM samples to assess.";
        string v = Verdict switch
        {
            DpmVerdict.RingLike => "ring-like slowdown (deliberate physical layout — preserve verbatim)",
            DpmVerdict.DamageLike => "broad/irregular slowdowns (reads more like surface damage)",
            _ => "smooth profile (no positional anomaly)",
        };
        return $"DPM over {Samples:N0} samples: shape {Fingerprint:X4}, {Anomalies.Count} anomaly(ies) — {v}.";
    }
}

/// <summary>
/// dpm — Data Position Measurement. A drive reads a spinning disc, so the time each sector takes to come
/// under the laser traces the disc's physical layout; plotting read speed against LBA reveals rings and
/// bands that the logical image cannot show. Ring-based copy protections (SecuROM, StarForce) exploit
/// exactly this — they write data at a fixed radius so a genuine disc shows a sharp, repeatable slowdown
/// there that a naive burn cannot reproduce. This ingests a per-position read-speed scan (as a preservation
/// dumper records it), fits a local baseline, flags the regions that read markedly slower, and decides
/// whether the shape is a deliberate ring, broad damage, or a clean profile. It also emits a
/// scale-invariant fingerprint of the profile shape, so two dumps of the one disc match and a copy off a
/// different master is told apart.
///
/// This measures and fingerprints the physical layout for preservation and verification — it detects and
/// records a ring so a faithful copy can be checked against the original; it circumvents nothing. Purely
/// a read-time measurement: it needs the dumper's timing scan and cannot be conjured from a finished
/// image, which carries no timing. Read-only.
/// </summary>
public static class Dpm
{
    /// <summary>Baseline window (samples) — wider than any expected ring, so a ring never hides itself in
    /// its own baseline.</summary>
    public const int DefaultWindow = 65;
    /// <summary>A sample is "slow" when it reads this fraction below its local baseline.</summary>
    public const double DefaultDipFraction = 0.25;
    /// <summary>Anomalies no wider than this many samples (and few in number) read as a deliberate ring.</summary>
    public const int RingMaxSpan = 12;
    /// <summary>Down-sampled profile length for the shape fingerprint.</summary>
    public const int FingerprintBuckets = 64;

    public static DpmReport Analyze(IReadOnlyList<DpmSample> samples,
                                    int window = DefaultWindow, double dipFraction = DefaultDipFraction)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            return new DpmReport
            {
                Samples = 0, MinSpeed = 0, MaxSpeed = 0, MeanSpeed = 0,
                Anomalies = Array.Empty<DpmAnomaly>(), Verdict = DpmVerdict.Clean, Fingerprint = 0,
            };

        var ordered = samples.OrderBy(s => s.Lba).ToList();
        int n = ordered.Count;
        var speed = ordered.Select(s => s.Speed).ToArray();

        double min = speed[0], max = speed[0], sum = 0;
        foreach (var v in speed) { min = Math.Min(min, v); max = Math.Max(max, v); sum += v; }

        // Local baseline via a moving median (robust to the very dips we are hunting).
        var baseline = new double[n];
        int half = Math.Max(1, window / 2);
        var scratch = new List<double>(window);
        for (int i = 0; i < n; i++)
        {
            scratch.Clear();
            for (int j = Math.Max(0, i - half); j <= Math.Min(n - 1, i + half); j++) scratch.Add(speed[j]);
            scratch.Sort();
            baseline[i] = scratch[scratch.Count / 2];
        }

        // Flag contiguous slow regions.
        var anomalies = new List<DpmAnomaly>();
        int runStart = -1;
        for (int i = 0; i <= n; i++)
        {
            bool slow = i < n && baseline[i] > 0 && speed[i] < baseline[i] * (1 - dipFraction);
            if (slow && runStart < 0) runStart = i;
            else if (!slow && runStart >= 0)
            {
                int a = runStart, b = i - 1;
                double localMin = double.MaxValue, localBase = 0;
                for (int k = a; k <= b; k++) { localMin = Math.Min(localMin, speed[k]); localBase = Math.Max(localBase, baseline[k]); }
                double depth = localBase > 0 ? 1 - localMin / localBase : 0;
                anomalies.Add(new DpmAnomaly(ordered[a].Lba, ordered[b].Lba, b - a + 1, localMin, localBase, depth));
                runStart = -1;
            }
        }

        var verdict = Classify(anomalies);
        ushort fp = Fingerprint(ordered, max);

        return new DpmReport
        {
            Samples = n, MinSpeed = min, MaxSpeed = max, MeanSpeed = sum / n,
            Anomalies = anomalies, Verdict = verdict, Fingerprint = fp,
        };
    }

    /// <summary>Parse a DPM scan. Columns by header (case-insensitive): lba/sector, and either
    /// speed/x/kbps, or time/timeus/us/micros (speed is then taken as the reciprocal of the time). A
    /// headerless numeric line is read as lba,speed. Blank lines and '#'/';' comments are ignored.</summary>
    public static IReadOnlyList<DpmSample> ParseCsv(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var samples = new List<DpmSample>();
        int lbaCol = 0, valCol = 1; bool isTime = false, headerSeen = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            var parts = line.Split(new[] { ',', '\t', ';' }, StringSplitOptions.TrimEntries);

            if (!headerSeen && parts.Any(p => !double.TryParse(p, NumberStyles.Any, CultureInfo.InvariantCulture, out _)))
            {
                for (int c = 0; c < parts.Length; c++)
                {
                    switch (parts[c].ToLowerInvariant())
                    {
                        case "lba" or "sector" or "block": lbaCol = c; break;
                        case "speed" or "x" or "kbps" or "kb/s": valCol = c; isTime = false; break;
                        case "time" or "timeus" or "us" or "micros" or "ms": valCol = c; isTime = true; break;
                    }
                }
                headerSeen = true;
                continue;
            }

            if (parts.Length < 2) continue;
            if (!int.TryParse(parts[lbaCol], NumberStyles.Any, CultureInfo.InvariantCulture, out int lba)) continue;
            if (!double.TryParse(parts[valCol], NumberStyles.Any, CultureInfo.InvariantCulture, out double val)) continue;
            double speed = isTime ? (val > 0 ? 1.0 / val : 0) : val;
            samples.Add(new DpmSample(lba, speed));
        }
        return samples;
    }

    public static string Render(DpmReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        if (r.Samples == 0) return sb.ToString().TrimEnd();
        sb.AppendLine($"  speed: min {r.MinSpeed:0.00}, mean {r.MeanSpeed:0.00}, max {r.MaxSpeed:0.00}");
        foreach (var a in r.Anomalies.Take(20)) sb.AppendLine($"  {a}");
        if (r.Anomalies.Count > 20) sb.AppendLine($"  … and {r.Anomalies.Count - 20} more");
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    private static DpmVerdict Classify(IReadOnlyList<DpmAnomaly> anomalies)
    {
        if (anomalies.Count == 0) return DpmVerdict.Clean;
        bool allNarrow = anomalies.All(a => a.SampleCount <= RingMaxSpan);
        if (anomalies.Count <= 3 && allNarrow) return DpmVerdict.RingLike;
        return DpmVerdict.DamageLike;
    }

    private static ushort Fingerprint(IReadOnlyList<DpmSample> ordered, double max)
    {
        if (max <= 0) return 0;
        int lo = ordered[0].Lba, hi = ordered[^1].Lba;
        long span = Math.Max(1, (long)hi - lo + 1);
        var bucketSum = new double[FingerprintBuckets];
        var bucketCnt = new int[FingerprintBuckets];
        foreach (var s in ordered)
        {
            int b = (int)Math.Clamp((s.Lba - lo) * (long)FingerprintBuckets / span, 0, FingerprintBuckets - 1);
            bucketSum[b] += s.Speed; bucketCnt[b]++;
        }
        var bytes = new byte[FingerprintBuckets];
        for (int b = 0; b < FingerprintBuckets; b++)
        {
            double avg = bucketCnt[b] > 0 ? bucketSum[b] / bucketCnt[b] : 0;
            bytes[b] = (byte)Math.Clamp((int)Math.Round(avg / max * 255.0), 0, 255);   // normalise → scale-invariant
        }
        return Crc16.ComputeInverted(bytes);
    }
}
