// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Audio;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// <see cref="FlacDecoder"/> decodes a standalone (container) FLAC file by
/// parsing its STREAMINFO and then handing frames to the same
/// <see cref="DiscForge.Core.Chd.ChdFlac"/> core CHD's own cdfl reader uses.
/// These tests round-trip through DiscForge's own clean-room
/// <see cref="FlacEncoder"/> — a self-consistent proof that doesn't depend on
/// any external tool being installed in CI.
/// </summary>
public class FlacDecoderTests
{
    private static short[] SineWave(int frames, int channels = 2)
    {
        var s = new short[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(short.MaxValue / 4 * Math.Sin(2 * Math.PI * 440 * i / 44100.0));
            for (int c = 0; c < channels; c++) s[i * channels + c] = (short)(v + c * 137);
        }
        return s;
    }

    private static byte[] InterleavedBytes(short[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            b[i * 2] = (byte)(samples[i] & 0xFF);
            b[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }
        return b;
    }

    [Fact]
    public void Decodes_a_stereo_flac_stream_back_to_identical_pcm()
    {
        var samples = SineWave(20000);   // several frames' worth at the encoder's 4096 block size
        var flac = FlacEncoder.Encode(samples, sampleRate: 44100, channels: 2);

        var result = FlacDecoder.Decode(flac, "test.flac");

        Assert.Equal(44100, result.SampleRate);
        Assert.Equal(2, result.Channels);
        Assert.Equal(16, result.BitsPerSample);
        Assert.Equal(InterleavedBytes(samples), result.Pcm);
    }

    [Fact]
    public void Decodes_a_short_single_frame_stream()
    {
        var samples = SineWave(500);
        var flac = FlacEncoder.Encode(samples, sampleRate: 44100, channels: 2);

        var result = FlacDecoder.Decode(flac, "short.flac");
        Assert.Equal(InterleavedBytes(samples), result.Pcm);
    }

    [Fact]
    public void Decodes_silence_via_the_constant_subframe()
    {
        var samples = new short[44100 * 2];   // one second of digital silence, stereo
        var flac = FlacEncoder.Encode(samples, sampleRate: 44100, channels: 2);

        var result = FlacDecoder.Decode(flac, "silence.flac");
        Assert.All(result.Pcm, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Mono_flac_is_reported_as_such()
    {
        var samples = SineWave(2000, channels: 1);
        var flac = FlacEncoder.Encode(samples, sampleRate: 44100, channels: 1);

        var result = FlacDecoder.Decode(flac, "mono.flac");
        Assert.Equal(1, result.Channels);
    }

    [Fact]
    public void Missing_flac_marker_is_refused()
    {
        var ex = Assert.Throws<AudioDecodeException>(() =>
            FlacDecoder.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "bad.flac"));
        Assert.Contains("fLaC", ex.Message);
    }

    [Fact]
    public void Decode_to_temp_wav_file_round_trips_through_WavReader()
    {
        var samples = SineWave(5000);
        var flac = FlacEncoder.Encode(samples, sampleRate: 44100, channels: 2);
        var flacPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".flac");
        File.WriteAllBytes(flacPath, flac);

        string? wavPath = null;
        try
        {
            wavPath = FlacDecoder.DecodeToTempWavFile(flacPath);
            using var fs = File.OpenRead(wavPath);
            var info = WavReader.ReadCdAudio(fs, "decoded.wav");
            Assert.True(info.IsCdAudioFormat);

            var got = new byte[info.DataLength];
            fs.Seek(info.DataOffset, SeekOrigin.Begin);
            fs.ReadExactly(got);
            Assert.Equal(InterleavedBytes(samples), got);
        }
        finally
        {
            File.Delete(flacPath);
            if (wavPath is not null) File.Delete(wavPath);
        }
    }
}
