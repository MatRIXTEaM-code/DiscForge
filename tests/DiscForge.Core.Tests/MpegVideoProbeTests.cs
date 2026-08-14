// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Mpeg;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the MPEG program-stream video probe. A minimal but structurally valid MPEG-1 program stream
/// is built by hand — a pack header and one video PES packet carrying a sequence header — so the sequence
/// header parse (dimensions, aspect, frame rate) and the program-stream detection are pinned. The probe
/// is additionally exercised against real ffmpeg-generated MPEG-1/-2 files in the validation harness.
/// </summary>
public class MpegVideoProbeTests
{
    // A minimal MPEG-1 program stream: pack header + a video PES whose payload is a sequence header for
    // a 320×240, aspect 1:1, 29.97 fps (frame-rate code 4), variable-bit-rate video.
    private static byte[] MinimalPs()
    {
        var seq = new byte[]
        {
            0x00, 0x00, 0x01, 0xB3,             // sequence_header_code
            0x14, 0x00, 0xF0,                   // width 320 (0x140), height 240 (0x0F0)
            0x14,                               // aspect code 1, frame-rate code 4
            0xFF, 0xFF, 0xFF,                   // bit_rate = 0x3FFFF (variable)
            0x00,                               // padding so ≥8 bytes follow the start code
        };
        var pesPayload = new byte[1 + seq.Length];
        pesPayload[0] = 0x0F;                   // MPEG-1 PES header: no PTS/DTS
        seq.CopyTo(pesPayload, 1);

        var bytes = new List<byte>();
        // pack header (MPEG-1): 00 00 01 BA + 8 bytes (first nibble 0x2 => MPEG-1 path)
        bytes.AddRange(new byte[] { 0x00, 0x00, 0x01, 0xBA, 0x21, 0x00, 0x01, 0x00, 0x01, 0x80, 0x80, 0x80 });
        // video PES: 00 00 01 E0 + 16-bit big-endian length + payload
        bytes.AddRange(new byte[] { 0x00, 0x00, 0x01, 0xE0 });
        bytes.Add((byte)(pesPayload.Length >> 8));
        bytes.Add((byte)(pesPayload.Length & 0xFF));
        bytes.AddRange(pesPayload);
        return bytes.ToArray();
    }

    [Fact]
    public void Reads_the_video_dimensions_and_frame_rate_from_the_sequence_header()
    {
        var m = MpegVideoProbe.Probe(MinimalPs());
        Assert.True(m.IsProgramStream);
        Assert.True(m.HasVideo);
        Assert.Equal(320, m.Width);
        Assert.Equal(240, m.Height);
        Assert.Equal(1, m.AspectCode);
        Assert.Equal(4, m.FrameRateCode);
        Assert.Equal(29.97, m.Fps, 2);
        Assert.False(m.IsMpeg2);
        Assert.True(m.VariableBitrate);
        Assert.Contains(m.Streams, s => s.Kind == MpegStreamKind.Video);
    }

    [Fact]
    public void Bytes_that_do_not_start_with_a_pack_header_are_not_a_program_stream()
    {
        var m = MpegVideoProbe.Probe(new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A });
        Assert.False(m.IsProgramStream);
        Assert.False(m.HasVideo);
    }
}
