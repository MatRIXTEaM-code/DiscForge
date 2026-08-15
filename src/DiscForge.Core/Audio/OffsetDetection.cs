// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Audio;

/// <summary>
/// Drive read-offset detection against AccurateRip: compute a track's AccurateRip
/// v1 checksum at every trial sample offset in a range and find the shift at which
/// the rip matches the database. Because the database holds checksums of
/// offset-CORRECTED rips submitted by thousands of drives, the trial offset that
/// matches IS the drive's combined read offset — measured, not looked up.
///
/// The sweep uses the sliding-window identity of the AR v1 sum
/// (W(o+1) = W(o) − S(o) + N·v[o+N], with S the plain sample sum), so a
/// ±600-sample sweep over a full track costs one pass plus O(1) per offset
/// instead of a full recompute each time. The identity is exercised against the
/// reference implementation (<see cref="AccurateRip.Compute"/>) in the tests —
/// the fast path must agree with the slow path everywhere, or it doesn't ship.
///
/// The sweep track must be a MIDDLE track (neither first nor last of the disc):
/// those two carry AccurateRip's guard-band special cases, which the sliding
/// window does not model. Callers with 1–2 track discs use
/// <see cref="BruteSweepV1"/>, which recomputes honestly per offset.
/// </summary>
public static class OffsetDetection
{
    /// <summary>One database hit found during a sweep.</summary>
    public sealed record Candidate
    {
        /// <summary>The drive's combined read offset in samples, AccurateRip sign
        /// convention (positive = the window shifts toward later samples).</summary>
        public required int OffsetSamples { get; init; }
        public required uint CrcV1 { get; init; }
        /// <summary>Confidence of the database pressing that matched.</summary>
        public required int Confidence { get; init; }
    }

    /// <summary>
    /// AR v1 checksums of a middle track at every offset in [−max..+max].
    /// <paramref name="pcmWindow"/> holds <c>max</c> frames of margin, the track,
    /// then <c>max</c> frames of margin (margins come from the neighbouring
    /// tracks' audio — the disc is one continuous spiral, which is exactly why an
    /// offset moves samples across track boundaries). Index i of the result is
    /// offset i − max.
    /// </summary>
    public static uint[] SweepV1(ReadOnlySpan<byte> pcmWindow, int trackFrames, int maxOffsetSamples)
    {
        if (trackFrames <= 0) throw new ArgumentOutOfRangeException(nameof(trackFrames));
        if (maxOffsetSamples < 0) throw new ArgumentOutOfRangeException(nameof(maxOffsetSamples));
        int totalFrames = trackFrames + 2 * maxOffsetSamples;
        if (pcmWindow.Length < (long)totalFrames * 4)
            throw new ArgumentException(
                $"Window needs {totalFrames} frames ({(long)totalFrames * 4} bytes); got {pcmWindow.Length}.");

        // Decode frames once: one 32-bit little-endian value per stereo sample.
        var v = new uint[totalFrames];
        for (int i = 0; i < totalFrames; i++)
            v[i] = (uint)(pcmWindow[i * 4]
                          | (pcmWindow[i * 4 + 1] << 8)
                          | (pcmWindow[i * 4 + 2] << 16)
                          | (pcmWindow[i * 4 + 3] << 24));

        // Seed sums at the leftmost window (offset −max).
        uint w = 0, s = 0;
        for (int i = 0; i < trackFrames; i++)
        {
            w += (uint)(i + 1) * v[i];
            s += v[i];
        }

        int positions = 2 * maxOffsetSamples + 1;
        var crcs = new uint[positions];
        crcs[0] = w;
        for (int o = 1; o < positions; o++)
        {
            // Slide right one frame: drop v[o−1], take v[o−1+N].
            uint incoming = v[o - 1 + trackFrames];
            w = w - s + (uint)trackFrames * incoming;
            s = s - v[o - 1] + incoming;
            crcs[o] = w;
        }
        return crcs;
    }

    /// <summary>
    /// The honest slow path for first/last tracks (guard bands apply): recompute
    /// the checksum per offset via the reference implementation. The window layout
    /// matches <see cref="SweepV1"/>; missing margin (before track 1 / after the
    /// lead-out) must be zero-filled by the caller — and reported as such.
    /// </summary>
    public static uint[] BruteSweepV1(ReadOnlySpan<byte> pcmWindow, int trackFrames, int maxOffsetSamples,
                                      bool isFirstTrack, bool isLastTrack)
    {
        int positions = 2 * maxOffsetSamples + 1;
        var crcs = new uint[positions];
        for (int o = 0; o < positions; o++)
            crcs[o] = AccurateRip.Compute(
                pcmWindow.Slice(o * 4, trackFrames * 4), isFirstTrack, isLastTrack).V1;
        return crcs;
    }

    /// <summary>
    /// Match a sweep against the database entries for one track. Returns every
    /// offset whose checksum equals some pressing's checksum, best confidence
    /// first — normally a single unambiguous hit at the drive's real offset.
    /// A zero checksum never matches (silence matches everything and proves nothing).
    /// </summary>
    public static IReadOnlyList<Candidate> Match(
        IReadOnlyList<uint> sweep, int maxOffsetSamples,
        IReadOnlyList<AccurateRip.DbEntry> database, int trackIndex)
    {
        var hits = new List<Candidate>();
        for (int i = 0; i < sweep.Count; i++)
        {
            uint crc = sweep[i];
            if (crc == 0) continue;
            int best = -1;
            foreach (var e in database)
                if (trackIndex < e.TrackChecksums.Count && e.TrackChecksums[trackIndex] == crc && e.Confidence > best)
                    best = e.Confidence;
            if (best >= 0)
                hits.Add(new Candidate { OffsetSamples = i - maxOffsetSamples, CrcV1 = crc, Confidence = best });
        }
        return hits.OrderByDescending(h => h.Confidence).ThenBy(h => Math.Abs(h.OffsetSamples)).ToList();
    }
}
