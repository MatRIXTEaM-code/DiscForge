// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using DiscForge.Core.Audio;

namespace DiscForge.Core.GameAudio;

/// <summary>Raised when an ADX stream is too short or lacks the CRI signature.</summary>
public sealed class AdxFormatException(string message) : Exception(message);

/// <summary>
/// Public header fields of a CRI ADX audio stream (the "(c)CRI" ADPCM container
/// widely used for streamed game music/voice). Big-endian on disk, unlike the
/// little-endian consoles it usually ships on.
/// </summary>
public sealed class AdxFile
{
    public required int Channels { get; init; }
    public required int SampleRate { get; init; }
    public required int TotalSamples { get; init; }

    /// <summary>0x02 fixed-coef, 0x03 standard ADX ADPCM (common), 0x04 exponential.</summary>
    public required byte Encoding { get; init; }

    // Extra fields, useful for the CLI / decoder — not part of the required surface.
    public required int BlockSize { get; init; }
    public required int BitDepth { get; init; }
    public required int HighpassCutoff { get; init; }
    public required int Version { get; init; }
    public required int DataOffset { get; init; }
}

/// <summary>
/// Parses the ADX header and answers <see cref="IsAdx(System.ReadOnlySpan{byte})"/>.
///
/// Clean-room, from the public ADX description. Header (BIG-ENDIAN):
///   0x00 u16  magic 0x8000
///   0x02 u16  copyright/data offset — data starts at (offset+4); "(c)CRI" sits at (offset-2)
///   0x04 u8   encoding type (0x02 fixed-coef, 0x03 standard ADX ADPCM, 0x04 exponential)
///   0x05 u8   block size (usually 18)
///   0x06 u8   sample bit depth (usually 4)
///   0x07 u8   channel count
///   0x08 u32  sample rate
///   0x0C u32  total samples (per channel)
///   0x10 u16  high-pass cutoff frequency
///   0x12 u8   version
///   0x13 u8   flags
/// </summary>
public static class AdxReader
{
    // "(c)CRI"
    private static readonly byte[] Copyright = { 0x28, 0x63, 0x29, 0x43, 0x52, 0x49 };

    public static bool IsAdx(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        if (data[0] != 0x80 || data[1] != 0x00) return false;          // magic 0x8000
        int dataOffset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2));
        int copyPos = dataOffset - 2;                                    // "(c)CRI" precedes the data
        if (copyPos < 0 || copyPos + Copyright.Length > data.Length) return false;
        for (int i = 0; i < Copyright.Length; i++)
            if (data[copyPos + i] != Copyright[i]) return false;
        return true;
    }

    public static bool IsAdx(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return IsAdx(AdxDecoder.ReadAll(stream));
    }

    public static AdxFile ReadInfo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ReadInfo(AdxDecoder.ReadAll(stream));
    }

    public static AdxFile ReadInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x14)
            throw new AdxFormatException("Too short to hold an ADX header.");
        if (!IsAdx(data))
            throw new AdxFormatException("Missing the 0x8000 magic / \"(c)CRI\" copyright — not an ADX file.");

        return new AdxFile
        {
            DataOffset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2)),
            Encoding = data[4],
            BlockSize = data[5],
            BitDepth = data[6],
            Channels = data[7],
            SampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8)),
            TotalSamples = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x0C)),
            HighpassCutoff = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(0x10)),
            Version = data[0x12],
        };
    }
}

/// <summary>
/// Decodes standard ADX ADPCM (encoding type 0x03) to 16-bit PCM / WAV.
///
/// The audio is a run of <c>block_size</c>-byte blocks, one block per channel,
/// interleaved by channel. Each block is a 2-byte big-endian scale followed by
/// 16 bytes = 32 signed 4-bit nibbles (high nibble first). A per-stream 2-tap
/// predictor is derived from the high-pass cutoff and sample rate:
///   a = sqrt(2) - cos(2*pi*cutoff/rate);  b = sqrt(2) - 1;
///   c = (a - sqrt((a+b)*(a-b))) / b;
///   coef1 = (int)(2*c*4096);  coef2 = (int)(-(c*c)*4096);   // 12-bit fixed point
/// Per sample:
///   prediction = (coef1*hist1 + coef2*hist2) >> 12;
///   sample     = clamp16(scale*nibble + prediction);   // then shift history
/// </summary>
public static class AdxDecoder
{
    internal static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> seg) && seg.Offset == 0)
            return seg.Array!.Length == seg.Count ? seg.Array! : ms.ToArray();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Compute the two fixed-point predictor coefficients for a stream.</summary>
    public static (int Coef1, int Coef2) PredictorCoefficients(int cutoff, int sampleRate)
    {
        double sqrt2 = Math.Sqrt(2.0);
        double a = sqrt2 - Math.Cos(2.0 * Math.PI * cutoff / sampleRate);
        double b = sqrt2 - 1.0;
        double c = (a - Math.Sqrt((a + b) * (a - b))) / b;
        int coef1 = (int)(c * 2.0 * 4096.0);
        int coef2 = (int)(-(c * c) * 4096.0);
        return (coef1, coef2);
    }

    /// <summary>Decode to interleaved 16-bit PCM samples (length = TotalSamples * Channels).</summary>
    public static short[] DecodeSamples(Stream stream) => DecodeSamples(ReadAll(stream));

    public static short[] DecodeSamples(ReadOnlySpan<byte> data)
    {
        var info = AdxReader.ReadInfo(data);
        if (info.Encoding != 0x03 && info.Encoding != 0x02)
            throw new AdxFormatException(
                $"ADX encoding type 0x{info.Encoding:X2} is not supported (only standard ADPCM 0x02/0x03).");

        int channels = info.Channels;
        int blockSize = info.BlockSize;
        int total = info.TotalSamples;
        if (channels < 1 || channels > 8) throw new AdxFormatException($"Bad channel count {channels}.");
        if (blockSize < 3) throw new AdxFormatException($"Bad block size {blockSize}.");

        int samplesPerBlock = (blockSize - 2) * 2;
        var (coef1, coef2) = PredictorCoefficients(info.HighpassCutoff, info.SampleRate);

        var output = new short[(long)total * channels < int.MaxValue ? total * channels : 0];
        if (output.Length == 0 && total > 0)
            throw new AdxFormatException("Sample count overflow.");

        var hist1 = new int[channels];
        var hist2 = new int[channels];

        int pos = info.DataOffset + 4;
        int frameBase = 0;
        while (frameBase < total)
        {
            // One block per channel, interleaved.
            if (pos + blockSize * channels > data.Length) break;
            for (int ch = 0; ch < channels; ch++)
            {
                int scale = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos));
                int p = pos + 2;
                int h1 = hist1[ch], h2 = hist2[ch];
                for (int i = 0; i < blockSize - 2; i++)
                {
                    byte bb = data[p + i];
                    for (int half = 0; half < 2; half++)
                    {
                        int nibble = half == 0 ? (bb >> 4) & 0x0F : bb & 0x0F;
                        int s = (nibble << 28) >> 28;                    // sign-extend 4-bit
                        int prediction = (coef1 * h1 + coef2 * h2) >> 12;
                        int sample = Math.Clamp(scale * s + prediction, short.MinValue, short.MaxValue);
                        h2 = h1;
                        h1 = sample;
                        int sIndex = frameBase + i * 2 + half;
                        if (sIndex < total)
                            output[sIndex * channels + ch] = (short)sample;
                    }
                }
                hist1[ch] = h1;
                hist2[ch] = h2;
                pos += blockSize;
            }
            frameBase += samplesPerBlock;
        }

        return output;
    }

    /// <summary>Decode to a little-endian 16-bit interleaved PCM byte buffer.</summary>
    public static byte[] DecodePcm(Stream stream) => DecodePcm(ReadAll(stream));

    public static byte[] DecodePcm(ReadOnlySpan<byte> data)
    {
        short[] samples = DecodeSamples(data);
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    /// <summary>Decode and write a canonical 16-bit PCM WAV.</summary>
    public static void DecodeToWav(Stream adx, Stream wavOut)
    {
        ArgumentNullException.ThrowIfNull(adx);
        ArgumentNullException.ThrowIfNull(wavOut);
        byte[] all = ReadAll(adx);
        var info = AdxReader.ReadInfo(all);
        short[] samples = DecodeSamples(all);
        int rate = info.SampleRate > 0 ? info.SampleRate : 44100;
        WavWriter.Write(wavOut, samples, rate, info.Channels);
    }
}
