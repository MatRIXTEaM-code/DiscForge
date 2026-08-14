// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Audio;
using Xunit;

namespace DiscForge.Core.Tests;

public class SilenceSplitterTests
{
    private const int Fs = 44100;

    // Build interleaved stereo PCM from a script of (seconds, tone|silence) segments.
    private static short[] Build(params (double seconds, bool tone)[] segs)
    {
        var samples = new List<short>();
        int phase = 0;
        foreach (var (seconds, tone) in segs)
        {
            int frames = (int)(seconds * Fs);
            for (int i = 0; i < frames; i++)
            {
                short v = tone ? (short)(12000 * Math.Sin(2 * Math.PI * 1000 * phase++ / Fs)) : (short)0;
                samples.Add(v); samples.Add(v);   // stereo
            }
        }
        return samples.ToArray();
    }

    [Fact]
    public void Splits_three_tracks_on_two_long_gaps()
    {
        var pcm = Build((1.0, true), (2.0, false), (1.0, true), (2.0, false), (1.0, true));
        var r = SilenceSplitter.Analyze(pcm, 2, Fs);
        Assert.Equal(3, r.Tracks.Count);
        Assert.Equal(2, r.Gaps.Count);
    }

    [Fact]
    public void A_short_intra_song_pause_does_not_split()
    {
        // One song with a 0.5s pause in the middle — below the 1.5s gap threshold.
        var pcm = Build((2.0, true), (0.5, false), (2.0, true));
        var r = SilenceSplitter.Analyze(pcm, 2, Fs);
        Assert.Single(r.Tracks);
    }

    [Fact]
    public void Leading_and_trailing_silence_is_trimmed()
    {
        var pcm = Build((2.0, false), (2.0, true), (2.0, false));
        var r = SilenceSplitter.Analyze(pcm, 2, Fs);
        Assert.Single(r.Tracks);
        // Audio onset is around 2s in, not at frame 0.
        Assert.True(r.Tracks[0].StartFrame > (int)(1.5 * Fs));
        Assert.True(r.Tracks[0].EndFrame < (int)(4.5 * Fs));
    }

    [Fact]
    public void Continuous_audio_is_a_single_track()
    {
        var pcm = Build((5.0, true));
        var r = SilenceSplitter.Analyze(pcm, 2, Fs);
        Assert.Single(r.Tracks);
        Assert.Empty(r.Gaps);
    }

    [Fact]
    public void All_silence_yields_no_tracks()
    {
        var pcm = Build((3.0, false));
        var r = SilenceSplitter.Analyze(pcm, 2, Fs);
        Assert.Empty(r.Tracks);
    }

    [Fact]
    public void The_cue_sheet_has_a_track_per_span_with_pregaps()
    {
        var pcm = Build((1.0, true), (2.0, false), (1.0, true));
        var r = SilenceSplitter.Analyze(pcm, 2, Fs);
        string cue = SilenceSplitter.ToCue(r, "album.wav");

        Assert.Contains("TRACK 01 AUDIO", cue);
        Assert.Contains("TRACK 02 AUDIO", cue);
        Assert.Contains("INDEX 01", cue);
        Assert.Contains("INDEX 00", cue);   // the second track carries a pregap
    }

    [Fact]
    public void A_tighter_threshold_and_gap_can_split_quieter_pauses()
    {
        // A quiet -40 dBFS "pause" between two tones: default -50 dB won't call it silence,
        // but a stricter -30 dB threshold with a shorter min gap will.
        short quiet = (short)(32767 * Math.Pow(10, -40.0 / 20.0));   // ~-40 dBFS
        var samples = new List<short>();
        void Seg(double sec, Func<int, short> gen) { int n = (int)(sec * Fs); for (int i = 0; i < n; i++) { var v = gen(i); samples.Add(v); samples.Add(v); } }
        Seg(1.0, i => (short)(12000 * Math.Sin(2 * Math.PI * 1000 * i / Fs)));
        Seg(1.0, _ => quiet);
        Seg(1.0, i => (short)(12000 * Math.Sin(2 * Math.PI * 1000 * i / Fs)));
        var pcm = samples.ToArray();

        var loose = SilenceSplitter.Analyze(pcm, 2, Fs);
        Assert.Single(loose.Tracks);   // -40 dB pause is above the -50 dB floor → not silence

        var strict = SilenceSplitter.Analyze(pcm, 2, Fs,
            new SilenceSplitter.Options { ThresholdDb = -30, MinSilenceSeconds = 0.5 });
        Assert.Equal(2, strict.Tracks.Count);
    }
}
