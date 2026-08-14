// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Audio;

/// <summary>The loudness / dynamics measurements of an audio track.</summary>
public sealed record AudioDynamicsReport
{
    public required int Channels { get; init; }
    public required int SampleRate { get; init; }
    public required long Frames { get; init; }

    /// <summary>Sample peak, in dBFS (0 = full scale).</summary>
    public required double PeakDb { get; init; }
    /// <summary>RMS level, in dBFS.</summary>
    public required double RmsDb { get; init; }
    /// <summary>Crest factor (peak − RMS), in dB — a first-order dynamics indicator.</summary>
    public double CrestDb => PeakDb - RmsDb;

    /// <summary>The DR value (the TT/Pleasurize "DR meter" algorithm) — higher is more dynamic; low
    /// single digits are the "loudness war" signature.</summary>
    public required int DynamicRange { get; init; }

    /// <summary>Samples sitting at (or above) the clip threshold.</summary>
    public required long ClippedSamples { get; init; }
    /// <summary>Runs of consecutive clipped samples — a flat-topped waveform is clipping, not just a loud peak.</summary>
    public required int ClipRuns { get; init; }
    public required int LongestClipRun { get; init; }

    public bool LikelyClipped => ClipRuns > 0 && LongestClipRun >= 3;

    public string Summary()
        => $"DR{DynamicRange}, peak {PeakDb:0.0} dBFS, RMS {RmsDb:0.0} dBFS, crest {CrestDb:0.0} dB" +
           (ClippedSamples > 0
               ? $"; {ClippedSamples:N0} clipped sample(s) in {ClipRuns:N0} run(s) (longest {LongestClipRun})"
               : "; no clipping") +
           (LikelyClipped ? " — likely clipped." : ".");
}

/// <summary>
/// audio-dynamics — the loudness and dynamics read of an audio track. It measures the sample peak and RMS
/// (in dBFS) and their difference (crest factor), computes the DR value with the established TT/Pleasurize
/// meter algorithm (three-second blocks, the second-highest block peak over the RMS of the loudest 20% of
/// blocks — higher means more dynamic, low single digits are the "loudness war" fingerprint), and detects
/// clipping as runs of consecutive full-scale samples (a flat-topped waveform), not merely loud peaks.
/// It reveals how heavily a transfer was compressed or clamped — a quality signal a bit-perfect hash can
/// never show. Analysis only; it measures and reports, it changes no audio.
/// </summary>
public static class AudioDynamics
{
    /// <summary>Samples with magnitude at or above this are treated as clipped.</summary>
    public const int ClipThreshold = 32767;
    private const double FullScale = 32768.0;
    private const double DrBlockSeconds = 3.0;

    public static AudioDynamicsReport Analyze(ReadOnlySpan<short> pcm, int channels, int sampleRate)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        long frames = pcm.Length / channels;

        // ---- peak, RMS, clipping (whole signal) ----------------------------
        int peak = 0;
        double sumSquares = 0;
        long clipped = 0;
        int clipRuns = 0, longestRun = 0, curRun = 0;
        for (int i = 0; i < pcm.Length; i++)
        {
            int a = Math.Abs((int)pcm[i]);
            if (a > peak) peak = a;
            sumSquares += (double)pcm[i] * pcm[i];
            if (a >= ClipThreshold)
            {
                clipped++;
                if (curRun == 0) clipRuns++;
                curRun++;
                if (curRun > longestRun) longestRun = curRun;
            }
            else curRun = 0;
        }

        double peakDb = ToDb(peak / FullScale);
        double rms = pcm.Length > 0 ? Math.Sqrt(sumSquares / pcm.Length) : 0;
        double rmsDb = ToDb(rms / FullScale);

        int dr = ComputeDr(pcm, channels, sampleRate);

        return new AudioDynamicsReport
        {
            Channels = channels, SampleRate = sampleRate, Frames = frames,
            PeakDb = peakDb, RmsDb = rmsDb, DynamicRange = dr,
            ClippedSamples = clipped, ClipRuns = clipRuns, LongestClipRun = longestRun,
        };
    }

    // ---- DR meter -----------------------------------------------------------

    private static int ComputeDr(ReadOnlySpan<short> pcm, int channels, int sampleRate)
    {
        long frames = pcm.Length / channels;
        int blockFrames = Math.Max(1, (int)(DrBlockSeconds * sampleRate));
        int numBlocks = (int)((frames + blockFrames - 1) / blockFrames);
        if (numBlocks == 0) return 0;

        double drSum = 0;
        int counted = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            var rmsBlocks = new double[numBlocks];
            var peakBlocks = new double[numBlocks];
            for (int b = 0; b < numBlocks; b++)
            {
                long start = (long)b * blockFrames;
                long end = Math.Min(frames, start + blockFrames);
                double ss = 0, pk = 0;
                long n = 0;
                for (long f = start; f < end; f++)
                {
                    double x = pcm[(int)(f * channels + ch)] / FullScale;
                    ss += x * x;
                    double ax = Math.Abs(x);
                    if (ax > pk) pk = ax;
                    n++;
                }
                rmsBlocks[b] = n > 0 ? Math.Sqrt(2.0 * ss / n) : 0;   // the DR-meter RMS (×2 convention)
                peakBlocks[b] = pk;
            }

            Array.Sort(peakBlocks);
            Array.Sort(rmsBlocks);
            // Second-highest block peak (falls back to the highest for a single block).
            double p2 = numBlocks >= 2 ? peakBlocks[numBlocks - 2] : peakBlocks[numBlocks - 1];

            // RMS of the loudest 20% of blocks.
            int n20 = Math.Max(1, (int)Math.Round(0.2 * numBlocks));
            double ssTop = 0;
            for (int k = 0; k < n20; k++)
            {
                double r = rmsBlocks[numBlocks - 1 - k];
                ssTop += r * r;
            }
            double rmsTop = Math.Sqrt(ssTop / n20);

            if (rmsTop > 0 && p2 > 0)
            {
                drSum += 20.0 * Math.Log10(p2 / rmsTop);
                counted++;
            }
        }

        if (counted == 0) return 0;
        return (int)Math.Round(drSum / counted);
    }

    public static string Render(AudioDynamicsReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        sb.AppendLine($"  {r.Channels}ch / {r.SampleRate} Hz, {r.Frames:N0} frames " +
                      $"({TimeSpan.FromSeconds((double)r.Frames / r.SampleRate):mm\\:ss})");
        return sb.ToString().TrimEnd();
    }

    private static double ToDb(double linear) => linear <= 0 ? -144.0 : Math.Max(-144.0, 20.0 * Math.Log10(linear));
}
