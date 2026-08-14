// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Audio;

namespace DiscForge.Core.GameAudio;

/// <summary>Thrown when a buffer is not a well-formed Nintendo DSP-ADPCM (.dsp) stream.</summary>
public sealed class DspFormatException(string message) : Exception(message);

/// <summary>
/// The 0x60-byte standard Nintendo DSP-ADPCM (.dsp) header: sample/nibble counts, sample rate, the loop
/// window, the 16 signed decode coefficients (eight predictor pairs) and the initial predictor/scale and
/// history the decoder starts from.
/// </summary>
public sealed record DspHeader
{
    public required uint SampleCount { get; init; }
    public required uint NibbleCount { get; init; }
    public required int SampleRate { get; init; }
    public required bool Looped { get; init; }
    public required int Format { get; init; }
    public required uint LoopStartNibble { get; init; }
    public required uint LoopEndNibble { get; init; }
    /// <summary>Sixteen signed coefficients — eight (coef1, coef2) predictor pairs indexed by each frame's header nibble.</summary>
    public required short[] Coefficients { get; init; }
    public required short InitialPredictorScale { get; init; }
    public required short InitialHistory1 { get; init; }
    public required short InitialHistory2 { get; init; }

    /// <summary>Where the ADPCM data begins — immediately after the fixed 0x60-byte header.</summary>
    public const int DataOffset = 0x60;

    /// <summary>Playback length in seconds, from the sample count and rate.</summary>
    public double Seconds => SampleRate > 0 ? SampleCount / (double)SampleRate : 0;
}

/// <summary>
/// dsp-decode — read and decode a Nintendo GameCube/Wii DSP-ADPCM (.dsp) stream to 16-bit PCM. DSP-ADPCM is
/// the console's native 4-bit voice/streaming codec: audio is carried in 8-byte frames (a predictor/scale
/// byte plus fourteen 4-bit samples), reconstructed from the eight predictor-coefficient pairs the file's
/// own header supplies. This decodes that data faithfully to WAV for preservation, exactly as the existing
/// ADX/VAG/XA decoders do for their formats. It reads and converts; it defeats no protection and moves no
/// protected content.
/// </summary>
public static class DspAdpcm
{
    /// <summary>Samples carried by one 8-byte DSP-ADPCM frame (1 header byte + 14 four-bit samples).</summary>
    public const int SamplesPerFrame = 14;
    /// <summary>Bytes in one DSP-ADPCM frame.</summary>
    public const int BytesPerFrame = 8;

    /// <summary>Read the standard 0x60-byte .dsp header. Big-endian throughout, as the format stores it.</summary>
    public static DspHeader ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < DspHeader.DataOffset)
            throw new DspFormatException("Buffer is too small to hold a DSP-ADPCM header (0x60 bytes).");

        uint sampleCount = BinaryPrimitives.ReadUInt32BigEndian(data[0x00..]);
        uint nibbleCount = BinaryPrimitives.ReadUInt32BigEndian(data[0x04..]);
        int sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x08..]);
        ushort loopFlag = BinaryPrimitives.ReadUInt16BigEndian(data[0x0C..]);
        ushort format = BinaryPrimitives.ReadUInt16BigEndian(data[0x0E..]);
        uint loopStart = BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]);
        uint loopEnd = BinaryPrimitives.ReadUInt32BigEndian(data[0x14..]);

        var coef = new short[16];
        for (int i = 0; i < 16; i++)
            coef[i] = BinaryPrimitives.ReadInt16BigEndian(data[(0x1C + i * 2)..]);

        short initPs = BinaryPrimitives.ReadInt16BigEndian(data[0x3E..]);
        short hist1 = BinaryPrimitives.ReadInt16BigEndian(data[0x40..]);
        short hist2 = BinaryPrimitives.ReadInt16BigEndian(data[0x42..]);

        if (format != 0)
            throw new DspFormatException($"Unsupported DSP format {format} (only 0 = ADPCM is decoded).");
        if (sampleRate is <= 0 or > 384000)
            throw new DspFormatException($"Implausible sample rate {sampleRate} Hz — not a DSP-ADPCM stream.");

        return new DspHeader
        {
            SampleCount = sampleCount,
            NibbleCount = nibbleCount,
            SampleRate = sampleRate,
            Looped = loopFlag != 0,
            Format = format,
            LoopStartNibble = loopStart,
            LoopEndNibble = loopEnd,
            Coefficients = coef,
            InitialPredictorScale = initPs,
            InitialHistory1 = hist1,
            InitialHistory2 = hist2,
        };
    }

    /// <summary>Decode a full mono .dsp buffer (header + data) to signed 16-bit PCM samples.</summary>
    public static short[] Decode(ReadOnlySpan<byte> file)
    {
        var header = ReadHeader(file);
        return DecodeData(header, file[DspHeader.DataOffset..]);
    }

    /// <summary>
    /// Decode the ADPCM payload for a header. Applies the canonical DSP-ADPCM step per nibble:
    /// sample = clamp16( ( ((nibble·scale) &lt;&lt; 11) + 1024 + coef1·hist1 + coef2·hist2 ) &gt;&gt; 11 ),
    /// carrying the two-sample history across frames.
    /// </summary>
    public static short[] DecodeData(DspHeader header, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(header);
        int total = checked((int)header.SampleCount);
        var outSamples = new short[total];

        int hist1 = header.InitialHistory1;
        int hist2 = header.InitialHistory2;
        var coef = header.Coefficients;

        int produced = 0;
        int pos = 0;
        while (produced < total)
        {
            if (pos >= data.Length)
                throw new DspFormatException(
                    $"DSP data ends after {produced} of {total} samples — the stream is truncated.");

            int predByte = data[pos];
            int scale = 1 << (predByte & 0x0F);
            int coefIndex = (predByte >> 4) & 0x0F;
            int coef1 = coef[coefIndex * 2];
            int coef2 = coef[coefIndex * 2 + 1];

            for (int i = 0; i < SamplesPerFrame && produced < total; i++)
            {
                int byteIndex = pos + 1 + (i / 2);
                if (byteIndex >= data.Length)
                    throw new DspFormatException(
                        $"DSP frame is truncated at sample {produced} of {total}.");

                int b = data[byteIndex];
                int nibble = (i & 1) == 0 ? (b >> 4) : (b & 0x0F);
                if (nibble >= 8) nibble -= 16;   // sign-extend the 4-bit sample

                int predicted = ((nibble * scale) << 11) + 1024 + coef1 * hist1 + coef2 * hist2;
                int sample = predicted >> 11;
                if (sample > short.MaxValue) sample = short.MaxValue;
                else if (sample < short.MinValue) sample = short.MinValue;

                outSamples[produced++] = (short)sample;
                hist2 = hist1;
                hist1 = sample;
            }

            pos += BytesPerFrame;
        }

        return outSamples;
    }

    /// <summary>Decode a .dsp stream and write a 16-bit mono WAV to <paramref name="wavOut"/>.</summary>
    public static void DecodeToWav(ReadOnlySpan<byte> file, Stream wavOut)
    {
        ArgumentNullException.ThrowIfNull(wavOut);
        var header = ReadHeader(file);
        short[] samples = DecodeData(header, file[DspHeader.DataOffset..]);
        WavWriter.Write(wavOut, samples, header.SampleRate, channels: 1);
    }
}
