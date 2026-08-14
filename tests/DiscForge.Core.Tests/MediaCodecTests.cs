// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.GameAudio;
using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Media codecs from task #110: CRI ADX ADPCM decode, PSX VAB/SEQ structure, and
/// PSX STR demux. Every fixture is synthesised by hand and asserted against
/// independently computed expected output — the project standard.
/// </summary>
public class MediaCodecTests
{
    // ---- ADX builders ----------------------------------------------------

    private static void WriteU16BE(byte[] b, int off, int v)
    {
        b[off] = (byte)(v >> 8);
        b[off + 1] = (byte)v;
    }

    private static void WriteU32BE(byte[] b, int off, long v)
    {
        b[off] = (byte)(v >> 24);
        b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8);
        b[off + 3] = (byte)v;
    }

    /// <summary>Build an 18-byte ADX block: BE scale + 16 bytes of 32 nibbles (high nibble first).</summary>
    private static byte[] Block(int scale, int[] nibbles32)
    {
        var blk = new byte[18];
        WriteU16BE(blk, 0, scale);
        for (int i = 0; i < 16; i++)
        {
            int hi = nibbles32[i * 2] & 0x0F;
            int lo = nibbles32[i * 2 + 1] & 0x0F;
            blk[2 + i] = (byte)((hi << 4) | lo);
        }
        return blk;
    }

    /// <summary>Assemble a minimal valid ADX: data begins at 0x20, "(c)CRI" at 0x1A.</summary>
    private static byte[] BuildAdx(int channels, int total, int cutoff, int rate, byte[][] interleavedBlocks, byte encoding = 0x03)
    {
        const int dataStart = 0x20;
        const int dataOffset = dataStart - 4;          // data starts at dataOffset+4
        int size = dataStart + interleavedBlocks.Length * 18;
        var b = new byte[size];
        WriteU16BE(b, 0x00, 0x8000);
        WriteU16BE(b, 0x02, dataOffset);
        b[0x04] = encoding;
        b[0x05] = 18;
        b[0x06] = 4;
        b[0x07] = (byte)channels;
        WriteU32BE(b, 0x08, rate);
        WriteU32BE(b, 0x0C, total);
        WriteU16BE(b, 0x10, cutoff);
        b[0x12] = 3;
        b[0x13] = 0;
        // "(c)CRI" immediately before the data start (dataOffset-2 = 0x1A).
        byte[] cri = { 0x28, 0x63, 0x29, 0x43, 0x52, 0x49 };
        System.Array.Copy(cri, 0, b, dataOffset - 2, 6);
        int p = dataStart;
        foreach (var blk in interleavedBlocks)
        {
            System.Array.Copy(blk, 0, b, p, 18);
            p += 18;
        }
        return b;
    }

    /// <summary>Independent reference: recompute coefficients from the documented
    /// formula and run the predictor over a signed nibble stream.</summary>
    private static short[] ReferenceDecode(int scale, int[] signedNibbles, int cutoff, int rate)
    {
        double sqrt2 = System.Math.Sqrt(2.0);
        double a = sqrt2 - System.Math.Cos(2.0 * System.Math.PI * cutoff / rate);
        double bb = sqrt2 - 1.0;
        double c = (a - System.Math.Sqrt((a + bb) * (a - bb))) / bb;
        int coef1 = (int)(c * 2.0 * 4096.0);
        int coef2 = (int)(-(c * c) * 4096.0);

        var outp = new short[signedNibbles.Length];
        int h1 = 0, h2 = 0;
        for (int i = 0; i < signedNibbles.Length; i++)
        {
            int prediction = (coef1 * h1 + coef2 * h2) >> 12;
            int val = System.Math.Clamp(scale * signedNibbles[i] + prediction, short.MinValue, short.MaxValue);
            outp[i] = (short)val;
            h2 = h1;
            h1 = val;
        }
        return outp;
    }

    private static int[] Signed(int[] nibbles) =>
        nibbles.Select(n => (n << 28) >> 28).ToArray();

    // ---- ADX tests -------------------------------------------------------

    [Fact]
    public void Adx_IsAdx_Positive()
    {
        var adx = BuildAdx(1, 32, 500, 44100, new[] { Block(16, new int[32]) });
        Assert.True(AdxReader.IsAdx(adx));
    }

    [Fact]
    public void Adx_IsAdx_RejectsBadMagic()
    {
        var adx = BuildAdx(1, 32, 500, 44100, new[] { Block(16, new int[32]) });
        adx[0] = 0x00;                                  // break the 0x8000 magic
        Assert.False(AdxReader.IsAdx(adx));
    }

    [Fact]
    public void Adx_IsAdx_RejectsMissingCopyright()
    {
        var adx = BuildAdx(1, 32, 500, 44100, new[] { Block(16, new int[32]) });
        adx[0x1A] = 0x00;                               // corrupt "(c)CRI"
        Assert.False(AdxReader.IsAdx(adx));
    }

    [Fact]
    public void Adx_IsAdx_RejectsTruncated()
    {
        Assert.False(AdxReader.IsAdx(new byte[] { 0x80, 0x00 }));
    }

    [Fact]
    public void Adx_ReadInfo_ParsesHeader()
    {
        var adx = BuildAdx(2, 64, 500, 44100, new[] { Block(16, new int[32]), Block(16, new int[32]) });
        var info = AdxReader.ReadInfo(new MemoryStream(adx));
        Assert.Equal(2, info.Channels);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(64, info.TotalSamples);
        Assert.Equal((byte)0x03, info.Encoding);
        Assert.Equal(18, info.BlockSize);
        Assert.Equal(500, info.HighpassCutoff);
    }

    [Fact]
    public void Adx_DecodeMono_MatchesReferencePredictor()
    {
        int scale = 16;
        int[] nibbles =
        {
            1, 2, 7, 0xF /*-1*/, 3, 0x8 /*-8*/, 4, 4,
            0, 1, 2, 3, 4, 5, 6, 7,
            0xF, 0xE, 0xD, 0xC, 1, 1, 1, 1,
            0, 0, 0, 0, 2, 2, 2, 2,
        };
        var adx = BuildAdx(1, 32, 500, 44100, new[] { Block(scale, nibbles) });
        short[] got = AdxDecoder.DecodeSamples(adx);

        short[] expected = ReferenceDecode(scale, Signed(nibbles), 500, 44100);
        Assert.Equal(32, got.Length);
        Assert.Equal(expected, got);

        // Spot-check the first two samples by direct arithmetic (hist starts at 0).
        Assert.Equal((short)(scale * 1), got[0]);       // prediction 0
    }

    [Fact]
    public void Adx_DecodeStereo_InterleavesChannels()
    {
        int scale = 20;
        int[] left = Enumerable.Range(0, 32).Select(i => (i % 3) - 1).Select(n => n & 0xF).ToArray();
        int[] right = Enumerable.Range(0, 32).Select(i => (i % 5) - 2).Select(n => n & 0xF).ToArray();

        var adx = BuildAdx(2, 32, 700, 37800, new[] { Block(scale, left), Block(scale, right) });
        short[] got = AdxDecoder.DecodeSamples(adx);

        short[] expL = ReferenceDecode(scale, Signed(left), 700, 37800);
        short[] expR = ReferenceDecode(scale, Signed(right), 700, 37800);

        Assert.Equal(64, got.Length);
        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(expL[i], got[i * 2]);
            Assert.Equal(expR[i], got[i * 2 + 1]);
        }
    }

    [Fact]
    public void Adx_DecodeToWav_WritesMatchingPcm()
    {
        var adx = BuildAdx(1, 32, 500, 44100, new[] { Block(16, new[] { 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, 0 }) });
        var wav = new MemoryStream();
        AdxDecoder.DecodeToWav(new MemoryStream(adx), wav);
        byte[] w = wav.ToArray();

        Assert.Equal((byte)'R', w[0]);
        Assert.Equal((byte)'I', w[1]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(w.AsSpan(22)));       // channels
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(w.AsSpan(24)));  // sample rate

        byte[] pcm = AdxDecoder.DecodePcm(adx);
        int dataLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(w.AsSpan(40));
        Assert.Equal(pcm.Length, dataLen);
        for (int i = 0; i < pcm.Length; i++)
            Assert.Equal(pcm[i], w[44 + i]);
    }

    [Fact]
    public void Adx_UnsupportedEncoding_Throws()
    {
        var adx = BuildAdx(1, 32, 500, 44100, new[] { Block(16, new int[32]) }, encoding: 0x04);
        Assert.Throws<AdxFormatException>(() => AdxDecoder.DecodeSamples(adx));
    }

    // ---- VAB builder + tests --------------------------------------------

    private static byte[] BuildVab()
    {
        const int header = 0x20;
        const int progTable = 128 * 16;
        int present = 2;
        int toneTables = present * 16 * 32;
        const int vagPtrs = 256 * 2;
        var b = new byte[header + progTable + toneTables + vagPtrs];

        // Header (little-endian).
        b[0] = 0x70; b[1] = 0x42; b[2] = 0x41; b[3] = 0x56;   // "pBAV"
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x04), 7);   // version
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x08), 0x1234); // vab id
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x12), 2);   // programs
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x14), 3);   // tones
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x16), 4);   // vags
        b[0x18] = 127;                                                 // master vol
        b[0x19] = 64;                                                  // master pan

        // Program headers: slot 0 (2 tones), slot 5 (1 tone).
        int p0 = header + 0 * 16;
        b[p0 + 0] = 2; b[p0 + 1] = 100; b[p0 + 4] = 60;
        int p5 = header + 5 * 16;
        b[p5 + 0] = 1; b[p5 + 1] = 80; b[p5 + 4] = 50;

        // Tone tables in present order: block0 -> slot0, block1 -> slot5.
        int t = header + progTable;
        // block0 tone0
        b[t + 0 * 32 + 2] = 90; b[t + 0 * 32 + 3] = 60; b[t + 0 * 32 + 4] = 60;
        b[t + 0 * 32 + 6] = 0;  b[t + 0 * 32 + 7] = 127;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(t + 0 * 32 + 22), 1);   // vag 1
        // block0 tone1
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(t + 1 * 32 + 22), 2);   // vag 2
        // block1 tone0
        int t1 = header + progTable + 16 * 32;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(t1 + 0 * 32 + 22), 3);  // vag 3

        // VAG pointer table (size/8).
        int vp = header + progTable + toneTables;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(vp + 1 * 2), 10);   // 80 bytes
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(vp + 2 * 2), 20);   // 160 bytes
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(vp + 3 * 2), 30);   // 240 bytes
        return b;
    }

    [Fact]
    public void Vab_IsVab_PositiveAndNegative()
    {
        Assert.True(Vab.IsVab(BuildVab()));
        Assert.True(Vab.IsVab(new byte[] { 0x56, 0x41, 0x42, 0x70 }));   // "VABp" also accepted
        Assert.False(Vab.IsVab(new byte[] { 0x00, 0x01, 0x02, 0x03 }));
    }

    [Fact]
    public void Vab_Parse_HeaderCounts()
    {
        var vab = Vab.Parse(BuildVab());
        Assert.Equal(2, vab.ProgramCount);
        Assert.Equal(3, vab.ToneCount);
        Assert.Equal(4, vab.VagCount);
        Assert.Equal(127, vab.MasterVolume);
        Assert.Equal(2, vab.Programs.Count);
    }

    [Fact]
    public void Vab_Parse_ProgramsAndTones()
    {
        var vab = Vab.Parse(BuildVab());
        var prog0 = vab.Programs[0];
        Assert.Equal(0, prog0.Index);
        Assert.Equal(2, prog0.ToneCount);
        Assert.Equal(100, prog0.Volume);
        Assert.Equal(2, prog0.Tones.Count);
        Assert.Equal(1, prog0.Tones[0].Vag);
        Assert.Equal(60, prog0.Tones[0].CenterNote);
        Assert.Equal(2, prog0.Tones[1].Vag);

        var prog5 = vab.Programs[1];
        Assert.Equal(5, prog5.Index);
        Assert.Equal(1, prog5.ToneCount);
        Assert.Equal(3, prog5.Tones[0].Vag);
    }

    [Fact]
    public void Vab_Parse_VagPointerTable()
    {
        var vab = Vab.Parse(BuildVab());
        Assert.Equal(256, vab.VagSizes.Count);
        Assert.Equal(80, vab.VagSizes[1]);
        Assert.Equal(160, vab.VagSizes[2]);
        Assert.Equal(240, vab.VagSizes[3]);
        // Offsets accumulate: entry1 at 0, entry2 at 80, entry3 at 240.
        Assert.Equal(0, vab.VagOffsets[1]);
        Assert.Equal(80, vab.VagOffsets[2]);
        Assert.Equal(240, vab.VagOffsets[3]);
    }

    // ---- SEQ builder + tests --------------------------------------------

    private static byte[] BuildSeq()
    {
        var body = new byte[]
        {
            0x00, 0x90, 0x3C, 0x40,   // dt 0, note on
            0x60, 0x80, 0x3C, 0x40,   // dt 0x60, note off
            0x00, 0xFF, 0x2F, 0x00,   // dt 0, end of track
        };
        var b = new byte[0x11 + body.Length];
        b[0] = 0x70; b[1] = 0x51; b[2] = 0x45; b[3] = 0x53;   // "pQES"
        WriteU32BE(b, 0x04, 1);                               // version
        WriteU16BE(b, 0x0A, 480);                             // ppqn
        b[0x0C] = 0x07; b[0x0D] = 0xA1; b[0x0E] = 0x20;       // tempo = 500000
        b[0x0F] = 0x04; b[0x10] = 0x02;                       // rhythm
        System.Array.Copy(body, 0, b, 0x11, body.Length);
        return b;
    }

    [Fact]
    public void Seq_IsSeq_PositiveAndNegative()
    {
        Assert.True(Seq.IsSeq(BuildSeq()));
        Assert.True(Seq.IsSeq(new byte[] { 0x53, 0x45, 0x51, 0x70 }));   // "SEQp" also accepted
        Assert.False(Seq.IsSeq(new byte[] { 0x00, 0x01, 0x02, 0x03 }));
    }

    [Fact]
    public void Seq_Parse_HeaderFields()
    {
        var seq = Seq.Parse(BuildSeq());
        Assert.Equal(1, seq.Version);
        Assert.Equal(480, seq.Ppqn);
        Assert.Equal(0x07A120, seq.Tempo);
    }

    [Fact]
    public void Seq_Parse_CountsEvents()
    {
        var seq = Seq.Parse(BuildSeq());
        // note-on, note-off, end-of-track.
        Assert.Equal(3, seq.EventCount);
    }

    [Fact]
    public void Seq_Parse_RunningStatus()
    {
        // Two note-ons sharing one status byte (running status), then end of track.
        var body = new byte[]
        {
            0x00, 0x90, 0x3C, 0x40,   // note on (explicit status)
            0x10, 0x3E, 0x40,         // note on (running status)
            0x00, 0xFF, 0x2F, 0x00,   // end of track
        };
        var b = new byte[0x11 + body.Length];
        b[0] = 0x70; b[1] = 0x51; b[2] = 0x45; b[3] = 0x53;
        WriteU16BE(b, 0x0A, 240);
        System.Array.Copy(body, 0, b, 0x11, body.Length);
        var seq = Seq.Parse(b);
        Assert.Equal(3, seq.EventCount);
    }

    // ---- STR builder + tests --------------------------------------------

    private static void WriteStrHeader(byte[] sector, int userOff, int chunkIndex, int chunkCount,
        int frameNumber, int declaredBytes, int width, int height)
    {
        var u = sector.AsSpan(userOff);
        BinaryPrimitives.WriteUInt16LittleEndian(u, 0x0160);
        BinaryPrimitives.WriteUInt16LittleEndian(u.Slice(0x02), (ushort)chunkIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(u.Slice(0x04), (ushort)chunkCount);
        BinaryPrimitives.WriteUInt32LittleEndian(u.Slice(0x06), (uint)frameNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(u.Slice(0x0A), (uint)declaredBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(u.Slice(0x0E), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(u.Slice(0x10), (ushort)height);
    }

    [Fact]
    public void Str_Demux2048_ReassemblesFrame()
    {
        const int payloadPerChunk = 2048 - 0x20;    // 2016
        int declared = payloadPerChunk + 10;        // chunk0 full + 10 bytes of chunk1

        var sectors = new byte[2 * 2048];
        // chunk 0
        WriteStrHeader(sectors, 0, 0, 2, 1, declared, 320, 240);
        for (int i = 0; i < payloadPerChunk; i++) sectors[0x20 + i] = 0xAA;
        // chunk 1
        WriteStrHeader(sectors, 2048, 1, 2, 1, declared, 320, 240);
        for (int i = 0; i < 10; i++) sectors[2048 + 0x20 + i] = (byte)(i + 1);

        var result = StrDemuxer.Demux(new MemoryStream(sectors), StrDemuxer.Layout.UserData2048);
        Assert.Single(result.Frames);
        var f = result.Frames[0];
        Assert.Equal(1, f.FrameNumber);
        Assert.Equal(320, f.Width);
        Assert.Equal(240, f.Height);
        Assert.True(f.Complete);
        Assert.Equal(declared, f.Bitstream.Length);
        Assert.Equal((byte)0xAA, f.Bitstream[0]);
        Assert.Equal((byte)0xAA, f.Bitstream[payloadPerChunk - 1]);
        Assert.Equal((byte)1, f.Bitstream[payloadPerChunk]);
        Assert.Equal((byte)10, f.Bitstream[payloadPerChunk + 9]);
        Assert.Equal(2, result.VideoSectorCount);
        Assert.Empty(result.AudioSectors);
    }

    [Fact]
    public void Str_Demux2352_SplitsAudioAndVideo()
    {
        var sectors = new byte[2 * 2352];
        // Sector 0: video (submode bit 0x02), single-chunk frame.
        sectors[16 + 2] = 0x02;                     // subheader submode = video
        WriteStrHeader(sectors, 24, 0, 1, 7, 100, 64, 64);
        for (int i = 0; i < 100; i++) sectors[24 + 0x20 + i] = (byte)(i & 0xFF);
        // Sector 1: audio (submode bit 0x04).
        sectors[2352 + 16 + 2] = 0x04;

        var result = StrDemuxer.Demux(new MemoryStream(sectors), StrDemuxer.Layout.Raw2352);
        Assert.Single(result.Frames);
        Assert.Equal(7, result.Frames[0].FrameNumber);
        Assert.Equal(100, result.Frames[0].Bitstream.Length);
        Assert.Equal(1, result.VideoSectorCount);
        Assert.Single(result.AudioSectors);
        Assert.Equal(2, result.TotalSectorCount);
    }

    [Fact]
    public void Str_Demux_IncompleteFrame_MarkedNotComplete()
    {
        // A frame declaring 3 chunks but only chunk 0 present.
        var sector = new byte[2048];
        WriteStrHeader(sector, 0, 0, 3, 42, 2016, 128, 96);
        var result = StrDemuxer.Demux(new MemoryStream(sector), StrDemuxer.Layout.UserData2048);
        Assert.Single(result.Frames);
        Assert.False(result.Frames[0].Complete);
        Assert.Equal(42, result.Frames[0].FrameNumber);
    }
}
