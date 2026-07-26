using DiscForge.Core.Audio;
using Xunit;

namespace DiscForge.Core.Tests;

public class JitterCorrectionTests
{
    private const int S = JitterCorrection.BytesPerSample;   // 4

    /// <summary>Pseudo-random but deterministic "music" — real audio is not
    /// uniform, which is exactly what makes alignment possible.</summary>
    private static byte[] Audio(int samples, int seed = 1)
    {
        var d = new byte[samples * S];
        uint x = (uint)seed * 2654435761u + 1;
        for (int i = 0; i < d.Length; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            d[i] = (byte)x;
        }
        return d;
    }

    /// <summary>
    /// Simulate a drive handing back audio shifted by <paramref name="jitter"/>
    /// samples from where it was asked to read.
    /// </summary>
    private static byte[] ReadWithJitter(byte[] disc, int startSample, int lengthSamples, int jitter)
    {
        int start = (startSample + jitter) * S;
        var outp = new byte[lengthSamples * S];
        for (int i = 0; i < outp.Length; i++)
        {
            int src = start + i;
            outp[i] = (src >= 0 && src < disc.Length) ? disc[src] : (byte)0;
        }
        return outp;
    }

    // --- alignment ------------------------------------------------------------

    [Fact]
    public void Perfectly_positioned_read_reports_no_offset()
    {
        var disc = Audio(2000);
        var reference = ReadWithJitter(disc, 100, 200, 0);
        var candidate = ReadWithJitter(disc, 100, 200, 0);

        var a = JitterCorrection.Align(reference, candidate);

        Assert.True(a.Confident);
        Assert.Equal(0, a.OffsetSamples);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(-7)]
    [InlineData(31)]
    [InlineData(-31)]
    public void Jittered_read_is_detected_with_the_right_offset(int jitter)
    {
        // The drive returned audio from `jitter` samples away from where asked.
        var disc = Audio(4000);
        var reference = ReadWithJitter(disc, 500, 300, 0);
        var candidate = ReadWithJitter(disc, 500, 300, jitter);

        var a = JitterCorrection.Align(reference, candidate);

        Assert.True(a.Confident);
        Assert.Equal(jitter, a.OffsetSamples);
        Assert.Equal(jitter * S, a.OffsetBytes);
    }

    [Fact]
    public void Silence_is_reported_as_not_confident_rather_than_guessed()
    {
        // Every offset matches in silence, so any answer would be invented.
        var silence = new byte[400 * S];

        var a = JitterCorrection.Align(silence, silence);

        Assert.False(a.Confident);
        Assert.Equal(0, a.OffsetSamples);
    }

    [Fact]
    public void A_constant_tone_is_also_not_confident()
    {
        var tone = new byte[400 * S];
        for (int i = 0; i < tone.Length; i += S)
        {
            tone[i] = 0x11; tone[i + 1] = 0x22; tone[i + 2] = 0x33; tone[i + 3] = 0x44;
        }

        Assert.False(JitterCorrection.Align(tone, tone).Confident);
    }

    [Fact]
    public void Jitter_beyond_the_search_window_is_not_claimed()
    {
        var disc = Audio(4000);
        var reference = ReadWithJitter(disc, 500, 300, 0);
        var candidate = ReadWithJitter(disc, 500, 300, 60);   // outside +/-32

        var a = JitterCorrection.Align(reference, candidate, maxOffsetSamples: 32);

        Assert.False(a.Confident);
    }

    [Fact]
    public void Unrelated_audio_does_not_produce_a_false_alignment()
    {
        var a = JitterCorrection.Align(Audio(300, seed: 1), Audio(300, seed: 99));
        Assert.False(a.Confident);
    }

    [Fact]
    public void Too_small_an_overlap_is_reported_as_not_confident()
    {
        // Sliding +/- maxOffset costs 2x maxOffset of the buffer. An overlap that
        // leaves no window can't be judged — and must not be guessed at.
        var disc = Audio(1000);
        var small = ReadWithJitter(disc, 0, 40, 0);      // 40 < 2*32

        Assert.False(JitterCorrection.Align(small, small, maxOffsetSamples: 32).Confident);
        Assert.Equal(128, JitterCorrection.MinimumOverlapSamples(32));
    }

    [Fact]
    public void Buffers_must_be_whole_samples()
    {
        Assert.Throws<ArgumentException>(() =>
            JitterCorrection.Align(new byte[10], new byte[10]));
    }

    // --- stitching ------------------------------------------------------------

    [Fact]
    public void Stitching_a_clean_read_yields_exactly_the_new_audio()
    {
        var disc = Audio(3000);
        // Overlap must exceed 2*maxOffset with a window to spare.
        const int overlapSamples = 256;

        // Already accepted: samples 0..499. Its tail is the overlap reference.
        var accepted = ReadWithJitter(disc, 0, 500, 0);
        var tail = accepted[^(overlapSamples * S)..];

        // Next read starts at the overlap, i.e. sample 244.
        var chunk = ReadWithJitter(disc, 500 - overlapSamples, 600, 0);

        var fresh = JitterCorrection.NewBytes(tail, chunk, overlapSamples * S, out var a);

        Assert.True(a.Confident);
        Assert.Equal(0, a.OffsetSamples);
        // 600 read - 256 overlap = 344 genuinely new samples.
        Assert.Equal(344 * S, fresh.Length);
        Assert.Equal(disc.AsSpan(500 * S, 344 * S).ToArray(), fresh.ToArray());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-3)]
    public void Stitching_a_jittered_read_still_produces_continuous_audio(int jitter)
    {
        // This is the whole point: blind concatenation here would repeat or drop
        // `jitter` samples at every join — clicks, and drift over a track.
        var disc = Audio(3000);
        const int overlapSamples = 256;

        var accepted = ReadWithJitter(disc, 0, 500, 0);
        var tail = accepted[^(overlapSamples * S)..];

        // The drive was asked for (500 - overlap) but returned audio `jitter`
        // samples away from that.
        var chunk = ReadWithJitter(disc, 500 - overlapSamples, 600, jitter);

        var fresh = JitterCorrection.NewBytes(tail, chunk, overlapSamples * S, out var a);

        Assert.True(a.Confident);
        Assert.Equal(jitter, a.OffsetSamples);

        // Whatever the drive did, the audio that follows must be the real thing —
        // blind concatenation here would repeat or drop `jitter` samples at every
        // join: clicks, and drift across a track.
        int check = 200 * S;
        Assert.Equal(disc.AsSpan(500 * S, check).ToArray(), fresh[..check].ToArray());
    }

    [Fact]
    public void A_whole_track_reassembles_byte_identically_despite_jitter_on_every_read()
    {
        // End to end: read a "track" in overlapping chunks, with the drive
        // jittering differently each time, and rebuild it exactly.
        var disc = Audio(20000);
        const int overlapSamples = 256;
        const int chunkSamples = 800;
        var jitters = new[] { 0, 2, -1, 5, -3, 1, 0, -2, 4, -5, 3, 0, 1, -1, 2, -4, 6, -6 };

        var output = new List<byte>();
        // First chunk is taken as read — nothing precedes it to align against.
        output.AddRange(ReadWithJitter(disc, 0, chunkSamples, 0));

        int position = chunkSamples;
        int j = 0;
        while (position < 20000 - chunkSamples)
        {
            int readStart = position - overlapSamples;
            var chunk = ReadWithJitter(disc, readStart, chunkSamples, jitters[j++ % jitters.Length]);

            var tail = output.GetRange(output.Count - overlapSamples * S, overlapSamples * S).ToArray();
            var fresh = JitterCorrection.NewBytes(tail, chunk, overlapSamples * S, out var a);

            Assert.True(a.Confident);
            output.AddRange(fresh.ToArray());
            position += fresh.Length / S;
        }

        // Every byte rebuilt must match the disc, despite the drive jittering
        // differently on every single read.
        Assert.True(output.Count > 19000 * S);
        Assert.Equal(disc.AsSpan(0, output.Count).ToArray(), output.ToArray());
    }
}
