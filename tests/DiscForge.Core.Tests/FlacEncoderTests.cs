// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Audio;
using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The clean-room FLAC encoder, verified the strongest way available without an
/// external tool: encode PCM, then decode the result with DiscForge's own FLAC
/// decoder (<see cref="ChdFlac"/>, an independent implementation) and require the
/// samples back byte-for-byte. Content is chosen to exercise every subframe path —
/// a tone (FIXED predictors), silence (CONSTANT), and noise (large Rice residuals) —
/// across multiple blocks including a partial final one.
/// </summary>
public class FlacEncoderTests
{
    // Frames begin right after "fLaC" (4) + the STREAMINFO block header (4) + the
    // 34-byte STREAMINFO body.
    private const int FrameStart = 4 + 4 + 34;

    private static short[] Stereo(int n)
    {
        var s = new short[n * 2];
        var rng = new Random(20260724);
        for (int i = 0; i < n; i++)
        {
            short l, r;
            if (i < n / 3)                                   // a tone → FIXED predictors compress it
            {
                l = (short)(8000 * Math.Sin(i * 0.05));
                r = (short)(6000 * Math.Sin(i * 0.07));
            }
            else if (i < 2 * n / 3)                          // silence → CONSTANT subframe
            {
                l = r = 0;
            }
            else                                             // noise → large Rice residuals
            {
                l = (short)(rng.Next(-32768, 32768));
                r = (short)(rng.Next(-32768, 32768));
            }
            s[i * 2] = l; s[i * 2 + 1] = r;
        }
        return s;
    }

    private static byte[] BigEndian(short[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            b[i * 2] = (byte)((samples[i] >> 8) & 0xFF);
            b[i * 2 + 1] = (byte)(samples[i] & 0xFF);
        }
        return b;
    }

    private static byte[] LittleEndian(short[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            b[i * 2] = (byte)(samples[i] & 0xFF);
            b[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }
        return b;
    }

    [Theory]
    [InlineData(4096)]     // exactly one block
    [InlineData(10000)]    // three blocks, partial last
    [InlineData(1)]        // single sample
    [InlineData(5000)]     // two blocks
    public void Encoded_flac_decodes_back_to_identical_pcm(int frames)
    {
        var pcm = Stereo(frames);
        var flac = FlacEncoder.Encode(pcm, 44100, 2);

        Assert.Equal((byte)'f', flac[0]);
        Assert.Equal("fLaC"u8.ToArray(), flac[..4]);
        Assert.Equal(0x80, flac[4]);               // last metadata block, type STREAMINFO
        Assert.Equal(34, flac[7]);                 // STREAMINFO length

        var (decoded, _) = ChdFlac.Decode(flac, FrameStart, frames * 2 * 2);
        Assert.Equal(BigEndian(pcm), decoded);
    }

    [Fact]
    public void Silence_uses_constant_subframes_and_stays_tiny()
    {
        // 2 seconds of stereo silence should compress to a small fraction of the PCM.
        var silence = new short[44100 * 2 * 2];
        var flac = FlacEncoder.Encode(silence, 44100, 2);
        Assert.True(flac.Length < silence.Length * 2 / 20,
            $"Silence compressed to {flac.Length} bytes from {silence.Length * 2} PCM bytes.");

        var (decoded, _) = ChdFlac.Decode(flac, FrameStart, silence.Length * 2);
        Assert.Equal(BigEndian(silence), decoded);
    }

    [Fact]
    public void Streaminfo_carries_the_source_audio_md5()
    {
        var pcm = Stereo(10000);
        var flac = FlacEncoder.Encode(pcm, 44100, 2);
        // STREAMINFO body starts at offset 8; its MD5 is the last 16 of the 34 bytes.
        var md5InStream = flac[26..42];
        var expected = System.Security.Cryptography.MD5.HashData(LittleEndian(pcm));
        Assert.Equal(expected, md5InStream);
    }

    [Fact]
    public void Correlated_stereo_compresses_better_with_decorrelation()
    {
        // Highly correlated channels (L ≈ R) — mid/side should beat coding them
        // independently, so the file is comfortably smaller than the PCM.
        int frames = 20000;
        var pcm = new short[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(9000 * Math.Sin(i * 0.02));
            pcm[i * 2] = v;
            pcm[i * 2 + 1] = (short)(v + (i % 5 - 2));   // R is L plus a tiny difference
        }
        var flac = FlacEncoder.Encode(pcm, 44100, 2);
        Assert.True(flac.Length < pcm.Length * 2 / 2,
            $"Correlated stereo did not decorrelate well: {flac.Length} vs {pcm.Length * 2} PCM bytes.");

        // And it still decodes losslessly.
        var (decoded, _) = ChdFlac.Decode(flac, FrameStart, frames * 2 * 2);
        Assert.Equal(BigEndian(pcm), decoded);
    }

    [Fact]
    public void A_tone_actually_compresses_below_the_pcm_size()
    {
        int frames = 20000;
        var pcm = new short[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            pcm[i * 2] = (short)(10000 * Math.Sin(i * 0.02));
            pcm[i * 2 + 1] = (short)(10000 * Math.Sin(i * 0.02 + 1));
        }
        var flac = FlacEncoder.Encode(pcm, 44100, 2);
        Assert.True(flac.Length < pcm.Length * 2,
            $"Tone did not compress: {flac.Length} vs {pcm.Length * 2} PCM bytes.");
    }
}
