// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.GameAudio;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the Nintendo DSP-ADPCM decoder. The header is read big-endian; the decode step is anchored
/// against a hand-traceable frame (a zero predictor pair with unit scale, where each output sample reduces
/// to its signed nibble); and a truncated payload is reported rather than read past its end.
/// </summary>
public class DspAdpcmTests
{
    // Build a standard 0x60-byte mono .dsp header + payload.
    private static byte[] BuildDsp(int sampleRate, uint sampleCount, short[] coef, byte[] data)
    {
        var buf = new byte[DspHeader.DataOffset + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x00), sampleCount);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x04), (uint)(data.Length / 8 * 16));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x08), (uint)sampleRate);
        // format (0x0E) = 0 = ADPCM; loop flag (0x0C) = 0.
        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(0x1C + i * 2), coef[i]);
        data.CopyTo(buf, DspHeader.DataOffset);
        return buf;
    }

    [Fact]
    public void Reads_the_header_big_endian()
    {
        var coef = new short[16];
        for (int i = 0; i < 16; i++) coef[i] = (short)(i - 8);
        var dsp = BuildDsp(48000, sampleCount: 14, coef, new byte[8]);

        var h = DspAdpcm.ReadHeader(dsp);
        Assert.Equal(48000, h.SampleRate);
        Assert.Equal(14u, h.SampleCount);
        Assert.False(h.Looped);
        Assert.Equal((short)-8, h.Coefficients[0]);
        Assert.Equal((short)7, h.Coefficients[15]);
    }

    [Fact]
    public void A_zero_predictor_unit_scale_frame_decodes_each_nibble_to_itself()
    {
        // Predictor pair 0 = (0,0) and header low nibble 0 => scale 1, so
        // sample = ((nibble<<11) + 1024) >> 11 == nibble for the 4-bit signed range.
        var coef = new short[16];                       // all pairs zero
        byte[] frame =
        {
            0x00,                                       // coef index 0, scale 1<<0
            0x12, 0x34, 0x56, 0x70, 0x00, 0x00, 0x00,   // nibbles 1,2,3,4,5,6,7,0,0,0,0,0,0,0
        };
        var dsp = BuildDsp(32000, sampleCount: 14, coef, frame);

        short[] pcm = DspAdpcm.Decode(dsp);
        Assert.Equal(new short[] { 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0, 0, 0, 0 }, pcm);
    }

    [Fact]
    public void Negative_nibbles_are_sign_extended()
    {
        var coef = new short[16];
        byte[] frame = { 0x00, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }; // nibble 0 = 0xF -> -1, nibble 1 = 8 -> -8
        var dsp = BuildDsp(32000, sampleCount: 2, coef, frame);

        short[] pcm = DspAdpcm.Decode(dsp);
        Assert.Equal((short)-1, pcm[0]);
        Assert.Equal((short)-8, pcm[1]);
    }

    [Fact]
    public void A_truncated_payload_is_reported()
    {
        var coef = new short[16];
        // Claim 14 samples but supply only a partial frame (2 bytes).
        var dsp = BuildDsp(32000, sampleCount: 14, coef, new byte[] { 0x00, 0x12 });
        Assert.Throws<DspFormatException>(() => DspAdpcm.Decode(dsp));
    }
}
