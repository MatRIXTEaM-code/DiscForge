// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Audio;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// AccurateRip offset detection. The claim that carries everything: the O(1)
/// sliding-window sweep must agree with the reference checksum at EVERY offset —
/// the fast path is only trusted because the slow path vouches for it. On top of
/// that, detection must find a planted drive offset exactly, refuse to match
/// digital silence, and prefer the higher-confidence pressing.
/// </summary>
public class OffsetDetectionTests
{
    /// <summary>Deterministic pseudo-random PCM, seeded — no RNG in tests.</summary>
    private static byte[] Pcm(int frames, uint seed)
    {
        var b = new byte[frames * 4];
        uint x = seed | 1;
        for (int i = 0; i < b.Length; i++)
        {
            x = x * 1664525u + 1013904223u;              // classic LCG — reproducible everywhere
            b[i] = (byte)(x >> 24);
        }
        return b;
    }

    [Fact]
    public void SlidingSweep_AgreesWithTheReferenceImplementation_AtEveryOffset()
    {
        const int trackFrames = 5000, max = 37;
        var window = Pcm(trackFrames + 2 * max, seed: 99);

        var fast = OffsetDetection.SweepV1(window, trackFrames, max);

        Assert.Equal(2 * max + 1, fast.Length);
        for (int o = 0; o <= 2 * max; o++)
        {
            var slice = window.AsSpan(o * 4, trackFrames * 4);
            uint reference = AccurateRip.Compute(slice, isFirstTrack: false, isLastTrack: false).V1;
            Assert.Equal(reference, fast[o]);
        }
    }

    [Fact]
    public void Detection_FindsAPlantedPlextorOffset_Exactly()
    {
        const int trackFrames = 20000, max = 100, planted = +30;
        var window = Pcm(trackFrames + 2 * max, seed: 7);

        // "The database": the checksum of the CORRECTED rip — the track as it would
        // be read at the true offset. Detection must rediscover that shift.
        uint dbCrc = AccurateRip.Compute(
            window.AsSpan((max + planted) * 4, trackFrames * 4), false, false).V1;
        var db = new[] { new AccurateRip.DbEntry { Confidence = 24, TrackChecksums = new[] { dbCrc } } };

        var sweep = OffsetDetection.SweepV1(window, trackFrames, max);
        var hits = OffsetDetection.Match(sweep, max, db, trackIndex: 0);

        var hit = Assert.Single(hits);
        Assert.Equal(planted, hit.OffsetSamples);
        Assert.Equal(24, hit.Confidence);
        Assert.Equal(dbCrc, hit.CrcV1);
    }

    [Fact]
    public void Detection_FindsANegativeOffset_Too()
    {
        const int trackFrames = 8000, max = 50, planted = -6;
        var window = Pcm(trackFrames + 2 * max, seed: 21);
        uint dbCrc = AccurateRip.Compute(window.AsSpan((max + planted) * 4, trackFrames * 4), false, false).V1;
        var db = new[] { new AccurateRip.DbEntry { Confidence = 3, TrackChecksums = new[] { dbCrc } } };

        var hits = OffsetDetection.Match(OffsetDetection.SweepV1(window, trackFrames, max), max, db, 0);
        Assert.Equal(planted, Assert.Single(hits).OffsetSamples);
    }

    [Fact]
    public void BrutePath_FindsTheOffset_OnAGuardBandedEdgeTrack()
    {
        // A single-track disc: both guard bands apply, the sliding identity does not.
        const int trackFrames = 10 * AccurateRip.SamplesPerSector + 4000;   // long enough to outlive both guards
        const int max = 40, planted = +30;
        var window = Pcm(trackFrames + 2 * max, seed: 3);
        uint dbCrc = AccurateRip.Compute(
            window.AsSpan((max + planted) * 4, trackFrames * 4), isFirstTrack: true, isLastTrack: true).V1;
        var db = new[] { new AccurateRip.DbEntry { Confidence = 9, TrackChecksums = new[] { dbCrc } } };

        var sweep = OffsetDetection.BruteSweepV1(window, trackFrames, max, isFirstTrack: true, isLastTrack: true);
        var hits = OffsetDetection.Match(sweep, max, db, 0);
        Assert.Equal(planted, Assert.Single(hits).OffsetSamples);
    }

    [Fact]
    public void Silence_NeverMatches_EvenWhenTheDatabaseHoldsAZero()
    {
        // An all-zero track sweeps to CRC 0 at every offset. A database entry of 0
        // (a silent DB track) must NOT produce 1,201 spurious "matches".
        const int trackFrames = 3000, max = 20;
        var window = new byte[(trackFrames + 2 * max) * 4];
        var db = new[] { new AccurateRip.DbEntry { Confidence = 50, TrackChecksums = new[] { 0u } } };

        var hits = OffsetDetection.Match(OffsetDetection.SweepV1(window, trackFrames, max), max, db, 0);
        Assert.Empty(hits);
    }

    [Fact]
    public void Match_PrefersTheHigherConfidencePressing()
    {
        const int trackFrames = 6000, max = 40;
        var window = Pcm(trackFrames + 2 * max, seed: 55);
        uint atPlus30 = AccurateRip.Compute(window.AsSpan((max + 30) * 4, trackFrames * 4), false, false).V1;
        uint atMinus6 = AccurateRip.Compute(window.AsSpan((max - 6) * 4, trackFrames * 4), false, false).V1;
        var db = new[]
        {
            new AccurateRip.DbEntry { Confidence = 2,  TrackChecksums = new[] { atMinus6 } },
            new AccurateRip.DbEntry { Confidence = 31, TrackChecksums = new[] { atPlus30 } },
        };

        var hits = OffsetDetection.Match(OffsetDetection.SweepV1(window, trackFrames, max), max, db, 0);
        Assert.Equal(2, hits.Count);
        Assert.Equal(+30, hits[0].OffsetSamples);        // confidence 31 outranks confidence 2
        Assert.Equal(-6, hits[1].OffsetSamples);
    }

    [Fact]
    public void Sweep_RejectsAWindowThatIsTooSmall()
    {
        Assert.Throws<ArgumentException>(() =>
            OffsetDetection.SweepV1(new byte[100], trackFrames: 100, maxOffsetSamples: 10));
    }

    /// <summary>
    /// The disc-ID computation the AccurateRip lookup URL depends on, pinned to a
    /// published third-party test vector (a 9-track TOC with known IDs, from
    /// github.com/davehensley/calculate-accuraterip-id-from-toc). A wrong ID
    /// 404s every lookup and looks exactly like "pressing not in the database" —
    /// this is the test that tells those two apart forever.
    /// </summary>
    [Fact]
    public void DiscIds_MatchThePublishedReferenceVector()
    {
        var offsets = new[] { 20, 27995, 55030, 73782, 88725, 115730, 140640, 156397, 181252, 232432 };
        var (id1, id2, cddb) = AccurateRip.DiscIds(offsets);
        Assert.Equal(0x00105b83u, id1);
        Assert.Equal(0x0077b665u, id2);
        Assert.Equal(0x8b0c1b09u, cddb);
    }
}
