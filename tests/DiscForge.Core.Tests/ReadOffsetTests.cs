using System;
using DiscForge.Core.Audio;
using Xunit;

namespace DiscForge.Core.Tests;

public class ReadOffsetTests
{
    private const int S = ReadOffset.BytesPerSample;   // 4

    private static byte[] Ramp(int samples, int start = 1)
    {
        // Distinct, non-zero bytes per sample so a shift is visible and never silent.
        var d = new byte[samples * S];
        for (int i = 0; i < samples; i++)
        {
            byte v = (byte)(start + i);
            if (v == 0) v = 0xFF;   // keep every sample non-zero
            d[i * S] = v; d[i * S + 1] = v; d[i * S + 2] = v; d[i * S + 3] = v;
        }
        return d;
    }

    // --- geometry -------------------------------------------------------------

    [Fact]
    public void Sector_geometry_is_the_red_book_constants()
    {
        Assert.Equal(4, ReadOffset.BytesPerSample);
        Assert.Equal(588, ReadOffset.SamplesPerSector);
        Assert.Equal(2352, ReadOffset.BytesPerSector);
    }

    [Fact]
    public void Sample_and_byte_conversions_round_trip()
    {
        Assert.Equal(2352, ReadOffset.SamplesToBytes(588));
        Assert.Equal(588, ReadOffset.BytesToSamples(2352));
        Assert.Throws<ArgumentException>(() => ReadOffset.BytesToSamples(2350));
    }

    [Theory]
    [InlineData(667, 0, 667)]
    [InlineData(667, -12, 655)]
    [InlineData(-24, 6, -18)]
    public void Combined_offset_is_drive_plus_disc(int drive, int disc, int expected)
        => Assert.Equal(expected, ReadOffset.Combine(drive, disc));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(588, 1)]
    [InlineData(589, 2)]
    [InlineData(-589, 2)]
    [InlineData(1176, 2)]
    public void Overread_sectors_is_ceil_of_offset_over_a_sector(int offset, int expected)
        => Assert.Equal(expected, ReadOffset.OverreadSectors(offset));

    // --- applying an offset ---------------------------------------------------

    [Fact]
    public void Zero_offset_returns_the_audio_unchanged()
    {
        var pcm = Ramp(100);
        var outp = ReadOffset.Apply(pcm, 0);
        Assert.Equal(pcm, outp);
    }

    [Fact]
    public void Positive_offset_slides_audio_earlier_and_pads_silence_at_the_end()
    {
        // output[i] = input[i + offset]; the last `offset` samples become silence.
        var pcm = Ramp(10);                 // samples 1..10
        var outp = ReadOffset.Apply(pcm, 3);

        // output sample 0 == input sample 3 (value 4)
        Assert.Equal(pcm.AsSpan(3 * S, (10 - 3) * S).ToArray(), outp.AsSpan(0, 7 * S).ToArray());
        // last 3 samples silent
        Assert.True(Silence.IsSilent(outp.AsSpan(7 * S)));
    }

    [Fact]
    public void Negative_offset_slides_audio_later_and_pads_silence_at_the_front()
    {
        var pcm = Ramp(10);
        var outp = ReadOffset.Apply(pcm, -3);

        Assert.True(Silence.IsSilent(outp.AsSpan(0, 3 * S)));
        // output sample 3 == input sample 0
        Assert.Equal(pcm.AsSpan(0, 7 * S).ToArray(), outp.AsSpan(3 * S, 7 * S).ToArray());
    }

    [Fact]
    public void Applying_an_offset_and_its_negation_restores_the_interior()
    {
        // The samples that never fell off either edge come back byte-identical.
        var pcm = Ramp(200);
        var shifted = ReadOffset.Apply(pcm, 10);
        var back = ReadOffset.Apply(shifted, -10);

        // Interior [10 .. 190) survived both shifts.
        Assert.Equal(pcm.AsSpan(10 * S, 180 * S).ToArray(), back.AsSpan(10 * S, 180 * S).ToArray());
    }

    [Fact]
    public void Apply_keeps_the_buffer_length_and_rejects_partial_samples()
    {
        Assert.Equal(40 * S, ReadOffset.Apply(Ramp(40), 5).Length);
        Assert.Throws<ArgumentException>(() => ReadOffset.Apply(new byte[10], 1));
    }

    [Fact]
    public void An_offset_past_the_buffer_yields_all_silence()
    {
        var outp = ReadOffset.Apply(Ramp(8), 100);
        Assert.True(Silence.IsSilent(outp));
    }

    // --- discard-only-silence guard ------------------------------------------

    [Fact]
    public void Shift_that_drops_silent_edge_is_reported_safe()
    {
        // 5 silent samples, then tone. A +5 offset drops only the silence.
        var pcm = new byte[20 * S];
        for (int i = 5; i < 20; i++) { pcm[i * S] = 0x40; pcm[i * S + 1] = 0x40; }
        Assert.True(ReadOffset.ShiftDiscardsOnlySilence(pcm, 5));
        Assert.True(ReadOffset.ShiftDiscardsOnlySilence(pcm, 3));
    }

    [Fact]
    public void Shift_that_would_drop_real_audio_is_flagged()
    {
        var pcm = Ramp(20);   // non-zero from the very first sample
        Assert.False(ReadOffset.ShiftDiscardsOnlySilence(pcm, 4));
        Assert.False(ReadOffset.ShiftDiscardsOnlySilence(pcm, -4));
        Assert.True(ReadOffset.ShiftDiscardsOnlySilence(pcm, 0));
    }
}

public class SilenceTests
{
    private const int S = ReadOffset.BytesPerSample;

    [Fact]
    public void All_zero_pcm_is_silent_and_empty_is_silent()
    {
        Assert.True(Silence.IsSilent(new byte[400]));
        Assert.True(Silence.IsSilent(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void A_single_nonzero_byte_breaks_silence()
    {
        var d = new byte[400];
        d[199] = 1;
        Assert.False(Silence.IsSilent(d));
    }

    [Fact]
    public void Leading_and_trailing_silence_are_counted_in_samples()
    {
        // 4 silent samples, 10 tone samples, 6 silent samples.
        var d = new byte[20 * S];
        for (int i = 4; i < 14; i++) { d[i * S] = 0x33; d[i * S + 2] = 0x33; }

        Assert.Equal(4, Silence.LeadingSilenceSamples(d));
        Assert.Equal(6, Silence.TrailingSilenceSamples(d));
    }

    [Fact]
    public void Fully_silent_buffer_counts_every_sample_both_ways()
    {
        var d = new byte[10 * S];
        Assert.Equal(10, Silence.LeadingSilenceSamples(d));
        Assert.Equal(10, Silence.TrailingSilenceSamples(d));
    }

    [Fact]
    public void Peak_is_zero_for_silence_and_the_max_magnitude_otherwise()
    {
        Assert.Equal(0, Silence.Peak(new byte[40]));

        // One sample at -32768 (0x0000 8000 LE) → magnitude 32768.
        var d = new byte[2 * S];
        d[0] = 0x00; d[1] = 0x80;         // left = -32768
        d[2] = 0xFF; d[3] = 0x7F;         // right = +32767
        Assert.Equal(32768, Silence.Peak(d));
    }
}
