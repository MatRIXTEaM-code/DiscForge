// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Audio;
using Xunit;

namespace DiscForge.Core.Tests;

public class AudioDynamicsTests
{
    private const int Fs = 44100;

    private static short[] Sine(double hz, double amp, int frames, int channels = 1)
    {
        var pcm = new short[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(amp * 32767 * Math.Sin(2 * Math.PI * hz * i / Fs));
            for (int c = 0; c < channels; c++) pcm[i * channels + c] = v;
        }
        return pcm;
    }

    [Fact]
    public void A_full_scale_sine_peaks_near_zero_dbfs_with_a_three_db_crest()
    {
        var r = AudioDynamics.Analyze(Sine(1000, 1.0, Fs), 1, Fs);
        Assert.InRange(r.PeakDb, -0.2, 0.05);        // ~0 dBFS
        Assert.InRange(r.RmsDb, -3.5, -2.5);         // sine RMS ≈ −3 dBFS
        Assert.InRange(r.CrestDb, 2.5, 3.5);         // crest ≈ 3 dB
    }

    [Fact]
    public void Peaky_material_scores_higher_dr_than_a_brickwalled_signal()
    {
        // Brickwalled: a full-scale sine — peak ≈ loud-RMS, so DR is near zero.
        var flat = AudioDynamics.Analyze(Sine(1000, 1.0, 20 * Fs), 1, Fs);

        // Dynamic: a quiet 0.25-amplitude bed with occasional full-scale transients — high crest, so the
        // block peak sits far above the loud-block RMS. That is exactly what the DR meter rewards.
        int frames = 20 * Fs;
        var pcm = new short[frames];
        for (int i = 0; i < frames; i++)
            pcm[i] = (short)(0.25 * 32767 * Math.Sin(2 * Math.PI * 1000 * i / Fs));
        for (int i = 0; i < frames; i += 5000) pcm[i] = 32767;   // sparse full-scale transients
        var dyn = AudioDynamics.Analyze(pcm, 1, Fs);

        Assert.True(flat.DynamicRange <= 2, $"brickwalled DR should be low, got {flat.DynamicRange}");
        Assert.True(dyn.DynamicRange >= 8, $"peaky DR should be high, got {dyn.DynamicRange}");
        Assert.True(dyn.DynamicRange > flat.DynamicRange);
    }

    [Fact]
    public void Clipping_runs_are_detected()
    {
        var pcm = Sine(1000, 0.5, 1000);
        // Force a flat-topped clipped run.
        for (int i = 100; i < 120; i++) pcm[i] = 32767;
        var r = AudioDynamics.Analyze(pcm, 1, Fs);
        Assert.True(r.ClippedSamples >= 20);
        Assert.True(r.ClipRuns >= 1);
        Assert.True(r.LongestClipRun >= 20);
        Assert.True(r.LikelyClipped);
    }

    [Fact]
    public void A_clean_signal_is_not_flagged_as_clipped()
    {
        var r = AudioDynamics.Analyze(Sine(1000, 0.7, 5000), 1, Fs);
        Assert.Equal(0, r.ClipRuns);
        Assert.False(r.LikelyClipped);
    }

    [Fact]
    public void Silence_is_handled_without_infinities()
    {
        var r = AudioDynamics.Analyze(new short[Fs], 1, Fs);
        Assert.True(double.IsFinite(r.PeakDb));
        Assert.True(double.IsFinite(r.RmsDb));
        Assert.Equal(-144.0, r.PeakDb);
    }

    [Fact]
    public void Stereo_is_analysed_per_channel()
    {
        var r = AudioDynamics.Analyze(Sine(1000, 1.0, 4 * Fs, channels: 2), 2, Fs);
        Assert.Equal(2, r.Channels);
        Assert.Equal(4L * Fs, r.Frames);
    }
}
