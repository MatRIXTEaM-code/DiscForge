// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.Audio;

/// <summary>
/// Writes a canonical 16-bit PCM WAV (RIFF/WAVE) for any sample rate and channel
/// count — used to export decoded XA-ADPCM audio, which is 37800 or 18900 Hz mono
/// or stereo, so the fixed 44100/stereo header used for CD audio elsewhere doesn't
/// fit. Little-endian throughout, as WAV requires.
/// </summary>
public static class WavWriter
{
    /// <summary>Write a complete WAV file from interleaved 16-bit samples.</summary>
    public static void Write(Stream output, ReadOnlySpan<short> samples, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));

        int dataBytes = samples.Length * 2;
        WriteHeader(output, dataBytes, sampleRate, channels);

        Span<byte> two = stackalloc byte[2];
        foreach (short s in samples)
        {
            BinaryPrimitives.WriteInt16LittleEndian(two, s);
            output.Write(two);
        }
    }

    public static byte[] ToBytes(ReadOnlySpan<short> samples, int sampleRate, int channels)
    {
        using var ms = new MemoryStream();
        Write(ms, samples, sampleRate, channels);
        return ms.ToArray();
    }

    private static void WriteHeader(Stream s, int dataBytes, int sampleRate, int channels)
    {
        const int bits = 16;
        int byteRate = sampleRate * channels * (bits / 8);
        short blockAlign = (short)(channels * (bits / 8));

        using var w = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8.ToArray());
        w.Write((uint)(36 + dataBytes));
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16u);                    // PCM fmt chunk size
        w.Write((short)1);               // PCM
        w.Write((short)channels);
        w.Write((uint)sampleRate);
        w.Write((uint)byteRate);
        w.Write(blockAlign);
        w.Write((short)bits);
        w.Write("data"u8.ToArray());
        w.Write((uint)dataBytes);
    }
}
