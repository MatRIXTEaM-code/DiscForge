// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Audio;

/// <summary>One detected track: its audio span, and where the silent gap that precedes it began.</summary>
public sealed record AudioSpan(int StartFrame, int EndFrame, int PregapStartFrame)
{
    /// <summary>Length in per-channel frames (End is exclusive).</summary>
    public int Frames => EndFrame - StartFrame;
}

/// <summary>A run of silence between (or around) tracks.</summary>
public sealed record SilenceRegion(int StartFrame, int EndFrame)
{
    public int Frames => EndFrame - StartFrame;
}

/// <summary>The result of splitting a gapless rip into tracks.</summary>
public sealed record SplitResult
{
    public required IReadOnlyList<AudioSpan> Tracks { get; init; }
    public required IReadOnlyList<SilenceRegion> Gaps { get; init; }
    public required int SampleRate { get; init; }
    public required int TotalFrames { get; init; }

    public string Summary()
        => $"{Tracks.Count} track(s) split on {Gaps.Count} silent gap(s) " +
           $"across {TimeSpan.FromSeconds((double)TotalFrames / Math.Max(1, SampleRate)):mm\\:ss}.";
}

/// <summary>
/// silence-split — recover track boundaries from a gapless album rip (a needle-drop, a single-file CD
/// image, a live set) by finding the silent gaps between songs. It frames the audio, measures each
/// window's peak level, marks the windows that sit below a level threshold, and treats a run of silence
/// longer than a minimum as a track boundary — while short intra-song pauses stay inside their track.
/// Leading and trailing silence is trimmed. It reports each track's audio span and the gap before it, and
/// can emit a cue sheet whose INDEX 00 marks the pregap (the gap) and INDEX 01 the audio onset, snapped to
/// CD sector (1/75 s) boundaries. Analysis only — it locates boundaries and describes them; it does not
/// cut or rewrite the audio.
/// </summary>
public static class SilenceSplitter
{
    public sealed record Options
    {
        /// <summary>A window quieter than this (dBFS) counts as silence.</summary>
        public double ThresholdDb { get; init; } = -50.0;
        /// <summary>A silent run at least this long is a track boundary.</summary>
        public double MinSilenceSeconds { get; init; } = 1.5;
        /// <summary>Audio segments shorter than this are folded into the neighbour, not treated as tracks.</summary>
        public double MinTrackSeconds { get; init; } = 0.5;
        /// <summary>Analysis window length.</summary>
        public double WindowMs { get; init; } = 20.0;
    }

    /// <summary>Detect track boundaries in interleaved 16-bit PCM.</summary>
    public static SplitResult Analyze(ReadOnlySpan<short> pcm, int channels, int sampleRate, Options? options = null)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var opt = options ?? new Options();

        int totalFrames = pcm.Length / channels;
        int win = Math.Max(1, (int)(sampleRate * opt.WindowMs / 1000.0));
        int windows = (totalFrames + win - 1) / win;
        double threshLinear = 32767.0 * Math.Pow(10, opt.ThresholdDb / 20.0);

        // Per-window peak → silence flags.
        var silent = new bool[windows];
        for (int w = 0; w < windows; w++)
        {
            int f0 = w * win, f1 = Math.Min(totalFrames, f0 + win);
            int peak = 0;
            for (int f = f0; f < f1; f++)
                for (int ch = 0; ch < channels; ch++)
                {
                    int a = Math.Abs((int)pcm[f * channels + ch]);
                    if (a > peak) peak = a;
                }
            silent[w] = peak < threshLinear;
        }

        // Silence runs in window units.
        int minSilenceWin = Math.Max(1, (int)Math.Ceiling(opt.MinSilenceSeconds * sampleRate / win));
        var gaps = new List<SilenceRegion>();
        int run = -1;
        for (int w = 0; w <= windows; w++)
        {
            bool s = w < windows && silent[w];
            if (s && run < 0) run = w;
            else if (!s && run >= 0)
            {
                if (w - run >= minSilenceWin)
                    gaps.Add(new SilenceRegion(run * win, Math.Min(totalFrames, w * win)));
                run = -1;
            }
        }

        // Audio segments = the regions between qualifying gaps, trimmed to their non-silent extent.
        var boundaries = new List<int> { 0 };
        foreach (var g in gaps) { boundaries.Add(g.StartFrame); boundaries.Add(g.EndFrame); }
        boundaries.Add(totalFrames);

        int minTrackFrames = (int)(opt.MinTrackSeconds * sampleRate);
        var tracks = new List<AudioSpan>();
        for (int i = 0; i < boundaries.Count - 1; i += 2)
        {
            int segStart = boundaries[i], segEnd = boundaries[i + 1];
            var (onset, offset) = TrimToAudio(pcm, channels, segStart, segEnd, win, threshLinear);
            if (offset <= onset) continue;                       // all silence
            if (offset - onset < minTrackFrames && tracks.Count > 0)
            {
                // Too short to be its own track: extend the previous track over it.
                var prev = tracks[^1];
                tracks[^1] = prev with { EndFrame = offset };
                continue;
            }
            int pregapStart = i == 0 ? onset : boundaries[i - 1];  // the gap that precedes this track
            tracks.Add(new AudioSpan(onset, offset, pregapStart));
        }

        return new SplitResult
        {
            Tracks = tracks, Gaps = gaps, SampleRate = sampleRate, TotalFrames = totalFrames,
        };
    }

    /// <summary>Emit a cue sheet for the split. INDEX 00 marks each track's pregap (the preceding gap) and
    /// INDEX 01 its audio onset, snapped to CD sectors (1/75 s).</summary>
    public static string ToCue(SplitResult result, string fileName)
    {
        ArgumentNullException.ThrowIfNull(result);
        int framesPerSector = Math.Max(1, result.SampleRate / 75);
        var sb = new StringBuilder();
        sb.AppendLine($"FILE \"{fileName}\" WAVE");
        for (int i = 0; i < result.Tracks.Count; i++)
        {
            var t = result.Tracks[i];
            sb.AppendLine($"  TRACK {i + 1:D2} AUDIO");
            int index01 = t.StartFrame / framesPerSector;
            int index00 = t.PregapStartFrame / framesPerSector;
            if (i > 0 && index00 < index01)
                sb.AppendLine($"    INDEX 00 {Msf(index00)}");
            sb.AppendLine($"    INDEX 01 {Msf(index01)}");
        }
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    private static (int onset, int offset) TrimToAudio(ReadOnlySpan<short> pcm, int channels,
        int segStart, int segEnd, int win, double threshLinear)
    {
        int onset = segEnd, offset = segStart;
        for (int f = segStart; f < segEnd; f++)
        {
            int peak = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                int a = Math.Abs((int)pcm[f * channels + ch]);
                if (a > peak) peak = a;
            }
            if (peak >= threshLinear)
            {
                if (f < onset) onset = f;
                offset = f + 1;
            }
        }
        return (onset, offset);
    }

    private static string Msf(int sector)
    {
        int f = sector % 75, s = (sector / 75) % 60, m = sector / 75 / 60;
        return $"{m:D2}:{s:D2}:{f:D2}".ToString(CultureInfo.InvariantCulture);
    }
}
