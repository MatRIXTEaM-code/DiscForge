using System.Security.Cryptography;
using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class DiscGenomeTests
{
    // A smoothly-varying stereo signal so per-sector loudness actually differs
    // sector to sector (making the envelope informative, unlike white noise).
    private static short Signal(int frame)
    {
        double amp = 0.5 + 0.5 * Math.Sin(2 * Math.PI * frame / 9000.0);
        double s = Math.Sin(2 * Math.PI * frame / 32.0);
        return (short)(amp * 28000 * s);
    }

    // Extract `sectors` audio sectors starting at a given physical frame — this is
    // exactly how a read offset manifests: the same signal, windowed a few frames over.
    private static byte[] AudioWindow(int startFrame, int sectors, Func<int, short> sig = null!)
    {
        sig ??= Signal;
        var b = new byte[sectors * 2352];
        int frames = sectors * 588;
        for (int f = 0; f < frames; f++)
        {
            short v = sig(startFrame + f);
            int o = f * 4;
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);          // left
            b[o + 2] = (byte)v; b[o + 3] = (byte)(v >> 8);      // right
        }
        return b;
    }

    private static byte[] DataTrack(int seed, int sectors)
    {
        var rng = new Random(seed);
        var b = new byte[sectors * 2048];
        rng.NextBytes(b);
        return b;
    }

    [Fact]
    public void The_same_disc_read_at_a_different_offset_matches()
    {
        var data = DataTrack(1, 50);
        var ripA = new List<GenomeTrack>
        {
            new(1, true, data),
            new(2, false, AudioWindow(1000, 100)),
        };
        var ripB = new List<GenomeTrack>
        {
            new(1, true, (byte[])data.Clone()),
            new(2, false, AudioWindow(1030, 100)),   // read 30 frames later — a real read offset
        };

        var ga = DiscGenome.Compute(ripA);
        var gb = DiscGenome.Compute(ripB);
        var m = DiscGenome.Compare(ga, gb);

        Assert.True(m.LayoutMatch);
        Assert.True(m.DataMatch);
        Assert.True(m.AudioSimilarity > 0.97);
        Assert.True(m.SameDisc);
    }

    [Fact]
    public void A_naive_full_hash_would_have_missed_that_match()
    {
        // The whole point: byte-for-byte the two audio rips differ, so a plain SHA
        // says "different" where the genome says "same disc".
        var a = AudioWindow(1000, 100);
        var b = AudioWindow(1030, 100);
        string ha = System.Convert.ToHexString(SHA256.HashData(a));
        string hb = System.Convert.ToHexString(SHA256.HashData(b));

        Assert.NotEqual(ha, hb);
    }

    [Fact]
    public void The_layout_hash_is_offset_invariant()
    {
        var a = DiscGenome.Compute(new List<GenomeTrack> { new(1, false, AudioWindow(1000, 80)) });
        var b = DiscGenome.Compute(new List<GenomeTrack> { new(1, false, AudioWindow(2500, 80)) });

        Assert.Equal(a.LayoutHash, b.LayoutHash);   // same geometry, different samples
    }

    [Fact]
    public void A_different_pressing_layout_does_not_match()
    {
        var a = DiscGenome.Compute(new List<GenomeTrack>
        {
            new(1, true, DataTrack(1, 50)),
            new(2, false, AudioWindow(1000, 100)),
        });
        var b = DiscGenome.Compute(new List<GenomeTrack>
        {
            new(1, true, DataTrack(1, 50)),
            new(2, false, AudioWindow(1000, 120)),   // a track of a different length
        });

        var m = DiscGenome.Compare(a, b);
        Assert.False(m.LayoutMatch);
        Assert.False(m.SameDisc);
    }

    [Fact]
    public void Same_layout_but_altered_data_is_not_the_same_disc()
    {
        var data = DataTrack(5, 40);
        var altered = (byte[])data.Clone();
        altered[12345] ^= 0xFF;                      // one changed data byte

        var a = DiscGenome.Compute(new List<GenomeTrack> { new(1, true, data) });
        var b = DiscGenome.Compute(new List<GenomeTrack> { new(1, true, altered) });

        var m = DiscGenome.Compare(a, b);
        Assert.True(m.LayoutMatch);                  // geometry unchanged
        Assert.False(m.DataMatch);                   // content changed
        Assert.False(m.SameDisc);
    }

    // A raw data track: `dataSectors` sync-bearing Mode 1 sectors, then `pregapSectors`
    // of trailing "audio" (no sync) standing in for the following audio track's pregap.
    private static byte[] DataTrackWithPregap(int dataSectors, int pregapSectors, int pregapSeed)
    {
        var b = new byte[(dataSectors + pregapSectors) * 2352];
        for (int s = 0; s < dataSectors; s++)
        {
            int o = s * 2352;
            b[o] = 0x00;
            for (int i = 1; i <= 10; i++) b[o + i] = 0xFF;
            b[o + 11] = 0x00;
            b[o + 15] = 0x01;                                  // Mode 1
            for (int i = 16; i < 2352; i++) b[o + i] = (byte)((s * 131 + i) & 0xFF);
        }
        var rng = new Random(pregapSeed);
        for (int s = dataSectors; s < dataSectors + pregapSectors; s++)
        {
            int o = s * 2352;
            for (int i = 0; i < 2352; i++) b[o + i] = (byte)rng.Next(1, 256);
            b[o] = 0x7F;                                       // ensure no accidental sync mark
        }
        // A pregap sector read raw can still carry a sync mark. Plant one in the LAST pregap
        // sector: the correct rule (leading contiguous run) must still stop at the first
        // non-sync pregap sector and exclude everything after it, including this one.
        int last = (dataSectors + pregapSectors - 1) * 2352;
        b[last] = 0x00;
        for (int i = 1; i <= 10; i++) b[last + i] = 0xFF;
        b[last + 11] = 0x00;
        return b;
    }

    // A data track of a fixed length whose tail (the transition/pregap zone before the audio)
    // differs per dump — simulating two drives disagreeing on where data stops.
    private static byte[] DataTrackVaryingTail(int totalSectors, int tailSeed)
    {
        var b = new byte[totalSectors * 2352];
        int pure = totalSectors - 250;
        for (int s = 0; s < pure; s++)
        {
            int o = s * 2352;
            b[o] = 0x00; for (int i = 1; i <= 10; i++) b[o + i] = 0xFF; b[o + 11] = 0x00; b[o + 15] = 0x01;
            for (int i = 16; i < 2352; i++) b[o + i] = (byte)((s * 131 + i) & 0xFF);
        }
        var rng = new Random(tailSeed);
        for (int s = pure; s < totalSectors; s++)
        {
            int o = s * 2352;
            b[o] = 0x00; for (int i = 1; i <= 10; i++) b[o + i] = 0xFF; b[o + 11] = 0x00; b[o + 15] = 0x01;
            for (int i = 16; i < 2352; i++) b[o + i] = (byte)rng.Next(256);   // drive-dependent tail
        }
        return b;
    }

    [Fact]
    public void The_drive_dependent_transition_zone_before_audio_is_trimmed_from_the_data_hash()
    {
        // Same disc, two drives: identical addressed data, but the last ~250 sectors before the
        // audio differ (each drive stops serving data at a slightly different sector). With an
        // audio track following, the genome trims that zone and the data hashes match.
        var a = new List<GenomeTrack> { new(1, true, DataTrackVaryingTail(800, 1)), new(2, false, AudioWindow(1000, 50)) };
        var b = new List<GenomeTrack> { new(1, true, DataTrackVaryingTail(800, 2)), new(2, false, AudioWindow(1030, 50)) };

        Assert.Equal(DiscGenome.Compute(a).DataHash, DiscGenome.Compute(b).DataHash);

        // Control: with no audio following, there is no transition zone to trim, so a tail
        // difference is real and must still register.
        var c = new List<GenomeTrack> { new(1, true, DataTrackVaryingTail(800, 1)) };
        var d = new List<GenomeTrack> { new(1, true, DataTrackVaryingTail(800, 2)) };
        Assert.NotEqual(DiscGenome.Compute(c).DataHash, DiscGenome.Compute(d).DataHash);
    }

    [Fact]
    public void A_data_tracks_trailing_audio_pregap_is_excluded_from_the_data_hash()
    {
        // Two rips identical in their addressed data but with a DIFFERENT trailing pregap
        // (what happens when two drives with different read offsets read that CD-DA pregap).
        // The data hash must ignore it, so the two still read as the same data.
        var a = new List<GenomeTrack> { new(1, true, DataTrackWithPregap(100, 6, 1)) };
        var b = new List<GenomeTrack> { new(1, true, DataTrackWithPregap(100, 6, 999)) };

        var ga = DiscGenome.Compute(a);
        var gb = DiscGenome.Compute(b);

        Assert.Equal(ga.DataHash, gb.DataHash);   // pregap excluded; addressed data matches

        // But a genuine change to the addressed data still shows up.
        var c = new List<GenomeTrack> { new(1, true, DataTrackWithPregap(101, 6, 1)) };
        Assert.NotEqual(ga.DataHash, DiscGenome.Compute(c).DataHash);
    }

    [Fact]
    public void Two_genuinely_different_audio_discs_score_low_similarity()
    {
        short Other(int f) => (short)(20000 * Math.Sin(2 * Math.PI * f / 7.0));   // very different timbre/loudness

        var a = DiscGenome.Compute(new List<GenomeTrack> { new(1, false, AudioWindow(1000, 100)) });
        var b = DiscGenome.Compute(new List<GenomeTrack> { new(1, false, AudioWindow(1000, 100, Other)) });

        var m = DiscGenome.Compare(a, b);
        Assert.True(m.AudioSimilarity < 0.9);
        Assert.False(m.SameDisc);
    }
}
