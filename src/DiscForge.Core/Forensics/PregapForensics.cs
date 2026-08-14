// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>What was found in one gap (a pregap or inter-track gap).</summary>
public sealed record GapFinding(int Track, string Kind, int Sectors, long NonSilentSamples, bool ContainsAudio)
{
    public override string ToString() =>
        $"{Kind} (track {Track}, {Sectors} sector(s)): " +
        (ContainsAudio ? $"CONTAINS AUDIO — {NonSilentSamples:N0} non-silent sample(s)" : "silent");
}

/// <summary>What a disc's gaps hold.</summary>
public sealed record PregapReport
{
    public required IReadOnlyList<GapFinding> Gaps { get; init; }
    public bool HasHiddenAudio => Gaps.Any(g => g.ContainsAudio);

    public string Summary()
    {
        var hits = Gaps.Where(g => g.ContainsAudio).ToList();
        if (hits.Count == 0) return "All gaps are silent — no hidden-track audio.";
        bool htoa = hits.Any(g => g.Kind == "track-1-pregap");
        return $"{hits.Count} gap(s) contain audio" + (htoa ? " — including a hidden track before track 1 (HTOA)." : ".");
    }
}

/// <summary>
/// Pregap &amp; hidden-track forensics — surface the audio some CDs tuck into the gaps ordinary rippers
/// throw away. The classic case is a hidden track before track 1 (HTOA): real music living in track 1's
/// 150-sector pregap, at a negative time index, that a normal rip starts after and never captures. This
/// reads the raw audio of each gap and decides whether it is genuine silence or carries sound — treating
/// the samples as 16-bit PCM and measuring how much rises above the noise floor — so an HTOA or a
/// between-tracks Easter egg is preserved rather than dropped. Detection and preservation only.
/// </summary>
public static class PregapForensics
{
    // A sample this far from zero counts as real signal, not dither/noise floor.
    private const int SilenceFloor = 64;
    // A gap is "audio" once this fraction of its samples clear the floor.
    private const double AudioFraction = 0.001;

    /// <summary>Analyse one gap's raw audio (2352 bytes/sector, 16-bit stereo PCM).</summary>
    public static GapFinding AnalyzeGap(int track, string kind, byte[] audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        int sectors = audio.Length / 2352;
        long nonSilent = 0;
        long samples = audio.Length / 2;   // 16-bit samples (both channels)
        for (int i = 0; i + 1 < audio.Length; i += 2)
        {
            short s = (short)(audio[i] | (audio[i + 1] << 8));
            if (Math.Abs((int)s) > SilenceFloor) nonSilent++;
        }
        bool containsAudio = samples > 0 && nonSilent > samples * AudioFraction;
        return new GapFinding(track, kind, sectors, nonSilent, containsAudio);
    }

    public static PregapReport Analyze(IReadOnlyList<(int Track, string Kind, byte[] Audio)> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        return new PregapReport
        {
            Gaps = gaps.Select(g => AnalyzeGap(g.Track, g.Kind, g.Audio)).ToList(),
        };
    }

    public static string Render(PregapReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var g in r.Gaps) sb.AppendLine($"  {g}");
        return sb.ToString().TrimEnd();
    }
}
