// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Audio;
using Xunit;

namespace DiscForge.Core.Tests;

public class DeEmphasisTests
{
    private const int Fs = 44100;

    [Fact]
    public void Is_flat_at_dc()
    {
        var f = new DeEmphasis(Fs);
        Assert.Equal(0.0, f.ResponseDb(0), 3);   // no attenuation at DC
    }

    [Fact]
    public void Approaches_the_minus_ten_db_shelf_at_high_frequency()
    {
        var f = new DeEmphasis(Fs);
        // The high-frequency shelf is 20·log10(T2/T1) = 20·log10(15/50) ≈ −10.458 dB.
        double shelf = 20 * Math.Log10(DeEmphasis.T2 / DeEmphasis.T1);
        Assert.Equal(shelf, f.ResponseDb(Fs / 2.0), 1);   // at Nyquist
    }

    [Fact]
    public void Response_decreases_monotonically_with_frequency()
    {
        var f = new DeEmphasis(Fs);
        double prev = f.ResponseDb(1);
        for (double hz = 100; hz <= Fs / 2.0; hz += 250)
        {
            double db = f.ResponseDb(hz);
            Assert.True(db <= prev + 1e-9, $"response rose at {hz} Hz");
            prev = db;
        }
    }

    [Fact]
    public void Digital_response_tracks_the_analog_target_in_the_audible_band()
    {
        var f = new DeEmphasis(Fs);
        // Bilinear warping is small well below Nyquist; the filter must match the analog curve closely.
        foreach (double hz in new[] { 100.0, 500.0, 1000.0, 3000.0, 6000.0 })
        {
            double digital = f.ResponseDb(hz);
            double analog = DeEmphasis.AnalogResponseDb(hz);
            Assert.True(Math.Abs(digital - analog) < 0.7,
                $"at {hz} Hz digital {digital:0.00} vs analog {analog:0.00}");
        }
    }

    [Fact]
    public void A_low_frequency_tone_passes_nearly_unchanged()
    {
        var f = new DeEmphasis(Fs);
        var x = Tone(120.0, 20000);
        var y = f.ProcessChannel(x);
        double ratioDb = 20 * Math.Log10(Rms(y, 2000) / Rms(x, 2000));
        Assert.True(Math.Abs(ratioDb) < 0.3, $"low tone changed by {ratioDb:0.00} dB");
    }

    [Fact]
    public void A_high_frequency_tone_is_attenuated_by_the_predicted_amount()
    {
        var f = new DeEmphasis(Fs);
        const double hz = 12000.0;
        var x = Tone(hz, 20000);
        var y = f.ProcessChannel(x);
        double measuredDb = 20 * Math.Log10(Rms(y, 4000) / Rms(x, 4000));   // skip the transient
        double predicted = f.ResponseDb(hz);
        Assert.True(Math.Abs(measuredDb - predicted) < 0.3,
            $"measured {measuredDb:0.00} dB vs predicted {predicted:0.00} dB");
    }

    [Fact]
    public void Interleaved_stereo_filters_each_channel_independently()
    {
        var f = new DeEmphasis(Fs);
        // Left = high tone (should attenuate), right = silence (stays silent).
        int frames = 8000;
        var pcm = new short[frames * 2];
        for (int i = 0; i < frames; i++)
            pcm[i * 2] = (short)(10000 * Math.Sin(2 * Math.PI * 12000 * i / Fs));
        f.ProcessInterleaved(pcm, 2);

        double leftRms = 0; int rightNonZero = 0;
        for (int i = 2000; i < frames; i++)
        {
            leftRms += pcm[i * 2] * (double)pcm[i * 2];
            if (pcm[i * 2 + 1] != 0) rightNonZero++;
        }
        Assert.Equal(0, rightNonZero);                         // silent channel stays silent
        Assert.True(Math.Sqrt(leftRms / (frames - 2000)) < 10000 / Math.Sqrt(2));  // left attenuated
    }

    [Fact]
    public void A_different_sample_rate_still_matches_the_analog_target()
    {
        var f = new DeEmphasis(48000);
        Assert.Equal(0.0, f.ResponseDb(0), 3);
        Assert.True(Math.Abs(f.ResponseDb(1000) - DeEmphasis.AnalogResponseDb(1000)) < 0.7);
    }

    // ---- helpers ------------------------------------------------------------

    private static double[] Tone(double hz, int n)
    {
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = 10000 * Math.Sin(2 * Math.PI * hz * i / Fs);
        return x;
    }

    private static double Rms(double[] v, int skip)
    {
        double s = 0; int c = 0;
        for (int i = skip; i < v.Length; i++) { s += v[i] * v[i]; c++; }
        return Math.Sqrt(s / c);
    }
}
