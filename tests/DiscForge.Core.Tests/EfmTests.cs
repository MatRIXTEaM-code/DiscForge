using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

public class EfmTests
{
    [Fact]
    public void The_codebook_covers_all_256_bytes()
    {
        Assert.Equal(256, Efm.CodebookSize);
    }

    [Fact]
    public void Encoding_then_decoding_is_the_identity()
    {
        var data = new byte[2000];
        new System.Random(7).NextBytes(data);
        var channel = Efm.Encode(data);
        Assert.Equal(data.Length * 14 + (data.Length - 1) * 3, channel.Length);   // words + merges between them
        var back = Efm.Decode(channel, data.Length);
        Assert.Equal(data, back);
    }

    [Fact]
    public void Every_byte_value_round_trips()
    {
        var all = new byte[256];
        for (int i = 0; i < 256; i++) all[i] = (byte)i;
        var back = Efm.Decode(Efm.Encode(all), all.Length);
        Assert.Equal(all, back);
    }

    [Fact]
    public void The_channel_stream_obeys_the_run_length_rule()
    {
        var data = new byte[4000];
        new System.Random(3).NextBytes(data);
        var ch = Efm.Analyze(data);
        Assert.True(ch.ConstraintOk, $"runs {ch.MinRunT}..{ch.MaxRunT}T");
        Assert.True(ch.MinRunT >= 3);
        Assert.True(ch.MaxRunT <= 11);
    }

    [Fact]
    public void The_merging_bits_keep_the_dsv_bounded()
    {
        var data = new byte[8000];
        new System.Random(11).NextBytes(data);
        var ch = Efm.Analyze(data);
        // With DSV-minimising merging, the running balance stays far below the stream length.
        Assert.True(ch.MaxAbsDsv < ch.ChannelBits / 20,
            $"DSV {ch.MaxAbsDsv} vs {ch.ChannelBits} bits");
        Assert.True(ch.TransitionDensity > 0.05);
    }
}
