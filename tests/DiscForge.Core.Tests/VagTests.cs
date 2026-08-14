// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the VAG (SPU-ADPCM) reader. VAG has no external oracle here, so the
/// header (big-endian, the usual trap) and the ADPCM decode maths are pinned with
/// hand-built files: filter 0 / shift 0 turns a nibble straight into (nibble &lt;&lt; 12),
/// so a known nibble yields a known sample.
/// </summary>
public class VagTests
{
    private static byte[] BuildVag(int sampleRate, string name, params byte[] adpcm)
    {
        var v = new byte[0x30 + adpcm.Length];
        Encoding.ASCII.GetBytes("VAGp").CopyTo(v, 0);
        BinaryPrimitives.WriteUInt32BigEndian(v.AsSpan(0x04), 0x20);           // version
        BinaryPrimitives.WriteUInt32BigEndian(v.AsSpan(0x0C), (uint)adpcm.Length); // data size
        BinaryPrimitives.WriteUInt32BigEndian(v.AsSpan(0x10), (uint)sampleRate);   // rate
        Encoding.ASCII.GetBytes(name).CopyTo(v, 0x20);
        adpcm.CopyTo(v, 0x30);
        return v;
    }

    // One 16-byte block: byte0 = shift/filter, byte1 = flags, then 14 data bytes.
    private static byte[] Block(byte shiftFilter, byte flags, byte firstDataByte)
    {
        var b = new byte[16];
        b[0] = shiftFilter;
        b[1] = flags;
        b[2] = firstDataByte;
        return b;
    }

    [Fact]
    public void The_header_is_read_big_endian()
    {
        var vag = BuildVag(22050, "TESTSND", Block(0x00, 0x00, 0x00));
        var info = Vag.ReadInfo(vag);

        Assert.Equal(22050, info.SampleRate);
        Assert.Equal("TESTSND", info.Name);
        Assert.Equal(16, info.DataSize);
    }

    [Fact]
    public void Filter0_shift0_turns_a_nibble_into_nibble_shifted_left_12()
    {
        // shift 0, filter 0 → sample = signext4(nibble) << 12, no history term.
        var vag = BuildVag(44100, "S", Block(0x00, 0x00, 0x01));   // first nibble = 1
        var pcm = Vag.Decode(vag);

        Assert.Equal(1 << 12, pcm[0]);       // 4096
        Assert.Equal(0, pcm[1]);
    }

    [Fact]
    public void A_negative_nibble_is_sign_extended()
    {
        var vag = BuildVag(44100, "S", Block(0x00, 0x00, 0x0F));   // nibble 0xF = -1
        Assert.Equal(-(1 << 12), Vag.Decode(vag)[0]);
    }

    [Fact]
    public void An_end_flag_stops_decoding()
    {
        // Two blocks; the first carries the end flag (bit 0), so the second is not
        // decoded → only 28 samples.
        var two = new byte[32];
        Block(0x00, 0x01, 0x02).CopyTo(two, 0);   // end flag set
        Block(0x00, 0x00, 0x04).CopyTo(two, 16);
        var vag = BuildVag(44100, "S", two);

        Assert.Equal(28, Vag.Decode(vag).Length);
    }

    [Fact]
    public void The_wav_export_is_mono_at_the_header_rate()
    {
        var vag = BuildVag(11025, "S", Block(0x00, 0x00, 0x03));
        var wav = Vag.ToWav(vag);

        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2)));    // channels
        Assert.Equal(11025u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4)));
    }

    [Fact]
    public void A_non_vag_is_refused()
    {
        Assert.False(Vag.IsVag(new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<VagFormatException>(() => Vag.ReadInfo(new byte[0x30]));
    }
}
