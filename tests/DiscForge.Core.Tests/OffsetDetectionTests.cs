// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

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

    // ---- offset-shift analysis (a mastering anomaly, not a single global offset) ------------

    private static OffsetDetection.TrackOffsetResult Hit(int track, int offset, int confidence = 10)
        => new() { TrackNumber = track, OffsetSamples = offset, Confidence = confidence };

    private static OffsetDetection.TrackOffsetResult Miss(int track)
        => new() { TrackNumber = track, OffsetSamples = null };

    [Fact]
    public void AnalyzeRuns_OneConsistentOffset_IsNotAShift()
    {
        var tracks = new[] { Hit(1, 30), Hit(2, 30), Hit(3, 30), Hit(4, 30) };
        var report = OffsetDetection.AnalyzeRuns(tracks);

        Assert.False(report.ShiftDetected);
        var run = Assert.Single(report.Runs);
        Assert.Equal(30, run.OffsetSamples);
        Assert.Equal(1, run.FirstTrack);
        Assert.Equal(4, run.LastTrack);
        Assert.Contains("Consistent offset", report.Summary());
    }

    [Fact]
    public void AnalyzeRuns_AMidDiscChange_IsDetectedAndLocatedExactly()
    {
        // Tracks 1-4 master at +30, tracks 5-8 master at +36 — a real mastering anomaly.
        var tracks = new[]
        {
            Hit(1, 30), Hit(2, 30), Hit(3, 30), Hit(4, 30),
            Hit(5, 36), Hit(6, 36), Hit(7, 36), Hit(8, 36),
        };
        var report = OffsetDetection.AnalyzeRuns(tracks);

        Assert.True(report.ShiftDetected);
        Assert.Equal(2, report.Runs.Count);
        Assert.Equal((30, 1, 4), (report.Runs[0].OffsetSamples, report.Runs[0].FirstTrack, report.Runs[0].LastTrack));
        Assert.Equal((36, 5, 8), (report.Runs[1].OffsetSamples, report.Runs[1].FirstTrack, report.Runs[1].LastTrack));
        Assert.Contains("OFFSET SHIFT DETECTED", report.Summary());
        Assert.Contains("tracks 1-4 @ +30", report.Summary());
        Assert.Contains("tracks 5-8 @ +36", report.Summary());
    }

    [Fact]
    public void AnalyzeRuns_UnmatchedTracksAreExcluded_NotTreatedAsAThirdOffset()
    {
        var tracks = new[] { Hit(1, 30), Miss(2), Hit(3, 30), Hit(4, 30) };
        var report = OffsetDetection.AnalyzeRuns(tracks);

        Assert.False(report.ShiftDetected);
        Assert.Equal(1, report.UnmatchedCount);
        var run = Assert.Single(report.Runs);
        Assert.Equal(30, run.OffsetSamples);
    }

    [Fact]
    public void AnalyzeRuns_ReturningToAnEarlierOffset_IsTwoRunsNotOne()
    {
        // +30, +36, then back to +30 — runs must see three separate stretches, not collapse
        // the two +30 tracks back together across the +36 track in between.
        var tracks = new[] { Hit(1, 30), Hit(2, 36), Hit(3, 30) };
        var report = OffsetDetection.AnalyzeRuns(tracks);

        Assert.True(report.ShiftDetected);
        Assert.Equal(3, report.Runs.Count);
        Assert.Equal(30, report.Runs[0].OffsetSamples);
        Assert.Equal(36, report.Runs[1].OffsetSamples);
        Assert.Equal(30, report.Runs[2].OffsetSamples);
    }

    [Fact]
    public void AnalyzeRuns_NoMatchesAtAll_ReportsItCannotAssess()
    {
        var report = OffsetDetection.AnalyzeRuns(new[] { Miss(1), Miss(2) });
        Assert.False(report.ShiftDetected);
        Assert.Empty(report.Runs);
        Assert.Contains("can't assess", report.Summary());
    }

    /// <summary>
    /// End-to-end against the real primitives (no hand-built TrackOffsetResult): two synthetic
    /// tracks, sweep + Match each independently against a shared database, and AnalyzeRuns must
    /// surface the planted shift — this is the scenario `offset-shift-scan` actually runs.
    /// </summary>
    [Fact]
    public void EndToEnd_TwoTracksAtDifferentPlantedOffsets_ScanDetectsTheShift()
    {
        const int frames = 20000, max = 100;
        var t1 = Pcm(frames + 2 * max, seed: 11);
        var t2 = Pcm(frames + 2 * max, seed: 12);
        const int off1 = +30, off2 = +36;

        uint crc1 = AccurateRip.Compute(t1.AsSpan((max + off1) * 4, frames * 4), false, false).V1;
        uint crc2 = AccurateRip.Compute(t2.AsSpan((max + off2) * 4, frames * 4), false, false).V1;
        var db = new[] { new AccurateRip.DbEntry { Confidence = 20, TrackChecksums = new[] { crc1, crc2 } } };

        var hits1 = OffsetDetection.Match(OffsetDetection.SweepV1(t1, frames, max), max, db, trackIndex: 0);
        var hits2 = OffsetDetection.Match(OffsetDetection.SweepV1(t2, frames, max), max, db, trackIndex: 1);

        var perTrack = new[]
        {
            hits1.Count > 0
                ? new OffsetDetection.TrackOffsetResult { TrackNumber = 1, OffsetSamples = hits1[0].OffsetSamples, Confidence = hits1[0].Confidence }
                : new OffsetDetection.TrackOffsetResult { TrackNumber = 1, OffsetSamples = null },
            hits2.Count > 0
                ? new OffsetDetection.TrackOffsetResult { TrackNumber = 2, OffsetSamples = hits2[0].OffsetSamples, Confidence = hits2[0].Confidence }
                : new OffsetDetection.TrackOffsetResult { TrackNumber = 2, OffsetSamples = null },
        };

        var report = OffsetDetection.AnalyzeRuns(perTrack);
        Assert.True(report.ShiftDetected);
        Assert.Equal(off1, report.Runs[0].OffsetSamples);
        Assert.Equal(off2, report.Runs[1].OffsetSamples);
    }
}
