// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Audio;

/// <summary>Raised when a WAV file can't be used for an audio CD.</summary>
public sealed class WavFormatException(string message) : Exception(message);

/// <summary>What a WAV file contains, and where its samples live.</summary>
public sealed record WavInfo
{
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required int BitsPerSample { get; init; }
    /// <summary>Byte offset of the PCM samples within the file.</summary>
    public required long DataOffset { get; init; }
    /// <summary>Length of the PCM samples in bytes.</summary>
    public required long DataLength { get; init; }

    /// <summary>Red Book: 44,100 Hz, 16-bit, stereo.</summary>
    public bool IsCdAudioFormat => SampleRate == 44100 && Channels == 2 && BitsPerSample == 16;

    public TimeSpan Duration => SampleRate == 0 || Channels == 0 || BitsPerSample == 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds((double)DataLength / (SampleRate * Channels * (BitsPerSample / 8)));

    /// <summary>Sectors this audio occupies on a CD (2352 bytes each, rounded up).</summary>
    public uint SectorCount => (uint)((DataLength + 2351) / 2352);
}

/// <summary>
/// Reads RIFF/WAVE headers and locates the PCM samples. Deliberately strict:
/// an audio CD is Red Book or it is nothing, so anything that isn't 44.1 kHz /
/// 16-bit / stereo is refused with a message saying what it actually is, rather
/// than being silently resampled into something wrong.
///
/// RIFF is a chunked format: the 'data' chunk is NOT at a fixed offset. Files in
/// the wild carry LIST/INFO, fact, cue and other chunks first, so the chunks must
/// be walked. Assuming data starts at byte 44 works until it doesn't.
/// </summary>
public static class WavReader
{
    public static WavInfo Read(Stream wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        if (!wav.CanSeek)
            throw new ArgumentException("Reading a WAV requires a seekable stream.", nameof(wav));

        wav.Seek(0, SeekOrigin.Begin);
        var header = new byte[12];
        if (wav.Read(header, 0, 12) < 12)
            throw new WavFormatException("File is too short to be a WAV.");

        if (Encoding.ASCII.GetString(header, 0, 4) != "RIFF")
            throw new WavFormatException("Not a WAV file (missing the 'RIFF' signature).");
        if (Encoding.ASCII.GetString(header, 8, 4) != "WAVE")
            throw new WavFormatException("Not a WAV file (RIFF container is not 'WAVE').");

        int sampleRate = 0, channels = 0, bits = 0;
        long dataOffset = -1, dataLength = 0;
        bool haveFmt = false;

        // Walk the chunk list; 'data' can be anywhere.
        long pos = 12;
        var chunkHeader = new byte[8];
        while (pos + 8 <= wav.Length)
        {
            wav.Seek(pos, SeekOrigin.Begin);
            if (wav.Read(chunkHeader, 0, 8) < 8) break;

            string id = Encoding.ASCII.GetString(chunkHeader, 0, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
            long body = pos + 8;

            if (id == "fmt ")
            {
                if (size < 16) throw new WavFormatException("The 'fmt ' chunk is too small.");
                var fmt = new byte[Math.Min(size, 16)];
                wav.ReadExactly(fmt, 0, fmt.Length);

                int format = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));

                // 1 = PCM, 0xFFFE = WAVE_FORMAT_EXTENSIBLE (still PCM for our purposes)
                if (format is not (1 or 0xFFFE))
                    throw new WavFormatException(
                        $"The audio is compressed (format tag {format}); an audio CD needs " +
                        "uncompressed PCM.");
                haveFmt = true;
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLength = Math.Min(size, wav.Length - body);
            }

            // Chunks are padded to an even length.
            pos = body + size + (size & 1);
        }

        if (!haveFmt) throw new WavFormatException("The WAV has no 'fmt ' chunk.");
        if (dataOffset < 0) throw new WavFormatException("The WAV has no 'data' chunk.");

        return new WavInfo
        {
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bits,
            DataOffset = dataOffset,
            DataLength = dataLength,
        };
    }

    /// <summary>Read and check a file is usable as a CD audio track.</summary>
    public static WavInfo ReadCdAudio(Stream wav, string name)
    {
        var info = Read(wav);
        if (!info.IsCdAudioFormat)
            throw new WavFormatException(
                $"'{name}' is {info.SampleRate} Hz, {info.Channels} channel(s), " +
                $"{info.BitsPerSample}-bit. An audio CD requires 44100 Hz, 16-bit, stereo " +
                "(Red Book). Convert it first — DiscForge will not resample, because " +
                "guessing at a conversion is how you get a bad master.");
        if (info.DataLength == 0)
            throw new WavFormatException($"'{name}' contains no audio.");
        return info;
    }
}
