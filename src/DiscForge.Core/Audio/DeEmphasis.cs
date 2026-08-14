// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Audio;

/// <summary>
/// CD audio de-emphasis. Some CDs — many from the 1980s — were mastered with pre-emphasis: the high
/// frequencies were boosted before recording to push hiss below the noise floor, with the expectation
/// that the player would apply the complementary cut on playback. The pre-emphasis flag lives in the Q
/// sub-channel control bits (and a cue sheet's PRE flag); a flat digital rip of such a disc sounds bright
/// and harsh until de-emphasis is applied. The standard curve is a first-order shelving response with a
/// 50 µs pole and a 15 µs zero — flat at DC, sloping down to a −10 dB shelf (the 15/50 ratio) at high
/// frequency.
///
/// This implements the exact analog de-emphasis transfer function H(s) = (1 + s·T2)/(1 + s·T1), with
/// T1 = 50 µs and T2 = 15 µs, discretised by the bilinear transform into a first-order IIR filter. Because
/// it is derived from the transfer function rather than hard-coded coefficients, its response can be —
/// and is — checked against the analog target it must match. It restores the intended flat response of a
/// pre-emphasised rip; it changes nothing about a normal track (apply it only when the disc flags
/// pre-emphasis).
/// </summary>
public sealed class DeEmphasis
{
    /// <summary>Pre-emphasis pole time constant: 50 microseconds.</summary>
    public const double T1 = 50e-6;
    /// <summary>Pre-emphasis zero time constant: 15 microseconds.</summary>
    public const double T2 = 15e-6;

    public int SampleRate { get; }

    // First-order IIR: y[n] = b0·x[n] + b1·x[n-1] − a1·y[n-1].
    private readonly double _b0, _b1, _a1;

    public double B0 => _b0;
    public double B1 => _b1;
    public double A1 => _a1;

    public DeEmphasis(int sampleRate = 44100)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;

        // Bilinear transform of H(s) = (1 + s·T2)/(1 + s·T1) with s = k·(1 − z⁻¹)/(1 + z⁻¹), k = 2·fs.
        double k = 2.0 * sampleRate;
        double b0 = 1 + k * T2, b1 = 1 - k * T2;
        double a0 = 1 + k * T1, a1 = 1 - k * T1;
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _a1 = a1 / a0;
    }

    /// <summary>The magnitude response (dB) of this digital filter at <paramref name="freqHz"/>.</summary>
    public double ResponseDb(double freqHz)
    {
        double w = 2 * Math.PI * freqHz / SampleRate;
        double c = Math.Cos(w), s = Math.Sin(w);
        double numRe = _b0 + _b1 * c, numIm = -_b1 * s;
        double denRe = 1 + _a1 * c, denIm = -_a1 * s;
        double mag = Math.Sqrt((numRe * numRe + numIm * numIm) / (denRe * denRe + denIm * denIm));
        return 20 * Math.Log10(mag);
    }

    /// <summary>The ideal analog de-emphasis magnitude (dB) at <paramref name="freqHz"/> — the target this
    /// filter is meant to reproduce: |(1 + jωT2)/(1 + jωT1)|.</summary>
    public static double AnalogResponseDb(double freqHz)
    {
        double w = 2 * Math.PI * freqHz;
        double num = Math.Sqrt(1 + w * T2 * w * T2);
        double den = Math.Sqrt(1 + w * T1 * w * T1);
        return 20 * Math.Log10(num / den);
    }

    /// <summary>Filter one channel's samples (a fresh filter state per call).</summary>
    public double[] ProcessChannel(ReadOnlySpan<double> x)
    {
        var y = new double[x.Length];
        double x1 = 0, y1 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = _b0 * x[i] + _b1 * x1 - _a1 * y1;
            y[i] = v;
            x1 = x[i];
            y1 = v;
        }
        return y;
    }

    /// <summary>De-emphasise interleaved 16-bit PCM in place, filtering each channel independently.</summary>
    public void ProcessInterleaved(short[] pcm, int channels)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        var x1 = new double[channels];
        var y1 = new double[channels];
        int frames = pcm.Length / channels;
        for (int f = 0; f < frames; f++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int idx = f * channels + ch;
                double x = pcm[idx];
                double v = _b0 * x + _b1 * x1[ch] - _a1 * y1[ch];
                x1[ch] = x;
                y1[ch] = v;
                pcm[idx] = (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
            }
        }
    }
}
