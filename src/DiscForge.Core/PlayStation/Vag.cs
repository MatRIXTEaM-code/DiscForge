// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Audio;

namespace DiscForge.Core.PlayStation;

public sealed class VagFormatException(string message) : Exception(message);

/// <summary>
/// Reads the PlayStation VAG audio format — a single SPU-ADPCM sample stream, the
/// container the SPU uses for sound effects and streamed music (the sample half of
/// "BGM 2 WAV"). It decodes to PCM and exports a WAV. A VAG is a plain, unencrypted
/// audio file; nothing here is protection-related.
///
/// Clean-room, from the public VAG / SPU-ADPCM description:
///   Header (big-endian, unusually for the PS1):
///     0x00  4  "VAGp"
///     0x04  4  version
///     0x0C  4  data size (bytes of ADPCM)
///     0x10  4  sample rate (Hz)
///     0x20 16  name (ASCII)
///     0x30     ADPCM data begins
///   SPU-ADPCM: 16-byte blocks — byte0 = shift (low nibble) and filter (high
///   nibble), byte1 = flags, bytes 2-15 = 28 four-bit samples (low nibble first).
///   Decode: sample = signext4(nibble) &lt;&lt; (12 - shift) + (f0·prev1 + f1·prev2)/64,
///   clamped to int16; filters 0-3 are f0 {0,60,115,98}, f1 {0,0,-52,-55}.
/// </summary>
public static class Vag
{
    private const int HeaderSize = 0x30;
    private const int BlockSize = 16;
    private static readonly int[] FilterPos = { 0, 60, 115, 98 };
    private static readonly int[] FilterNeg = { 0, 0, -52, -55 };

    public sealed record VagInfo
    {
        public required string Name { get; init; }
        public required int SampleRate { get; init; }
        public required int DataSize { get; init; }
        public required int Version { get; init; }
    }

    public static bool IsVag(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == (byte)'V' && data[1] == (byte)'A' && data[2] == (byte)'G' && data[3] == (byte)'p';

    public static VagInfo ReadInfo(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize || !IsVag(data))
            throw new VagFormatException("Missing the \"VAGp\" signature — not a VAG audio file.");

        return new VagInfo
        {
            Version = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x04)),
            DataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x0C)),
            SampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x10)),
            Name = Encoding.ASCII.GetString(data, 0x20, 16).TrimEnd('\0', ' '),
        };
    }

    /// <summary>Decode the whole stream to 16-bit mono PCM.</summary>
    public static short[] Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize || !IsVag(data))
            throw new VagFormatException("Not a VAG audio file.");

        int dataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x0C));
        int end = HeaderSize + dataSize;
        if (dataSize <= 0 || end > data.Length) end = data.Length;   // trust the file length if the field is off

        var pcm = new List<short>();
        int prev1 = 0, prev2 = 0;
        for (int pos = HeaderSize; pos + BlockSize <= end; pos += BlockSize)
        {
            int shift = data[pos] & 0x0F;
            int filter = Math.Min((data[pos] >> 4) & 0x0F, 3);
            int flags = data[pos + 1];
            int sh = shift <= 12 ? 12 - shift : 0;
            int f0 = FilterPos[filter], f1 = FilterNeg[filter];

            for (int b = 0; b < 14; b++)
            {
                byte twoNibbles = data[pos + 2 + b];
                for (int half = 0; half < 2; half++)
                {
                    int nibble = half == 0 ? twoNibbles & 0x0F : (twoNibbles >> 4) & 0x0F;
                    int s = (SignExtend4(nibble) << sh) + ((prev1 * f0 + prev2 * f1) >> 6);
                    short sample = (short)Math.Clamp(s, short.MinValue, short.MaxValue);
                    pcm.Add(sample);
                    prev2 = prev1;
                    prev1 = sample;
                }
            }

            if ((flags & 0x01) != 0) break;   // end marker (1 or 7)
        }
        return pcm.ToArray();
    }

    /// <summary>Decode and wrap as a mono WAV.</summary>
    public static byte[] ToWav(byte[] data)
    {
        var info = ReadInfo(data);
        int rate = info.SampleRate > 0 ? info.SampleRate : 44100;
        return WavWriter.ToBytes(Decode(data), rate, channels: 1);
    }

    private static int SignExtend4(int nibble) => (nibble << 28) >> 28;
}
