// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using DiscForge.Core.Audio;
using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the XA-ADPCM decoder. There's no external oracle wired in, so the
/// decode maths is pinned with hand-built sound groups whose output is predictable:
/// filter 0 with range 0 turns a nibble straight into (nibble &lt;&lt; 12), so a known
/// nibble pattern yields known samples. Structure checks pin the sample counts and
/// the coding-info bit decode.
/// </summary>
public class XaAdpcmTests
{
    // Build a 2304-byte data area with one non-zero unit-0 sample in group 0.
    private static byte[] DataAreaWithFirstNibble(int nibble, byte unit0Param)
    {
        var d = new byte[XaAdpcm.DataAreaSize];
        d[0] = unit0Param;                 // SP0 (unit 0): filter/range
        d[16] = (byte)(nibble & 0x0F);     // word 0, byte 0, low nibble = unit 0 sample 0
        return d;
    }

    [Fact]
    public void Coding_info_decodes_rate_stereo_and_bits()
    {
        Assert.Equal(new XaAdpcm.CodingInfo(37800, false, false), XaAdpcm.CodingInfo.Parse(0x00));
        Assert.Equal(new XaAdpcm.CodingInfo(37800, true, false), XaAdpcm.CodingInfo.Parse(0x01));  // stereo
        Assert.Equal(new XaAdpcm.CodingInfo(18900, false, false), XaAdpcm.CodingInfo.Parse(0x04));  // half rate
        Assert.True(XaAdpcm.CodingInfo.Parse(0x10).EightBit);                                        // 8-bit
    }

    [Fact]
    public void A_silent_data_area_decodes_to_the_right_number_of_zero_samples()
    {
        var pcm = XaAdpcm.DecodeDataArea(new byte[XaAdpcm.DataAreaSize], stereo: false, new XaAdpcm.State());
        Assert.Equal(18 * 8 * 28, pcm.Length);   // 4032 mono samples
        Assert.All(pcm, s => Assert.Equal(0, s));
    }

    [Fact]
    public void Stereo_produces_interleaved_frames_of_the_same_total_count()
    {
        var pcm = XaAdpcm.DecodeDataArea(new byte[XaAdpcm.DataAreaSize], stereo: true, new XaAdpcm.State());
        Assert.Equal(18 * 8 * 28, pcm.Length);   // 4032 total = 2016 stereo frames
    }

    [Fact]
    public void Filter0_range0_turns_a_nibble_straight_into_nibble_shifted_left_12()
    {
        // param 0x00 = filter 0, range 0 → shift 12, no history term.
        var pcm = XaAdpcm.DecodeDataArea(DataAreaWithFirstNibble(1, 0x00), stereo: false, new XaAdpcm.State());
        Assert.Equal(1 << 12, pcm[0]);           // 4096
        Assert.Equal(0, pcm[1]);
    }

    [Fact]
    public void A_negative_nibble_is_sign_extended()
    {
        // nibble 0xF = -1 → -1 << 12 = -4096.
        var pcm = XaAdpcm.DecodeDataArea(DataAreaWithFirstNibble(0xF, 0x00), stereo: false, new XaAdpcm.State());
        Assert.Equal(-(1 << 12), pcm[0]);
    }

    [Fact]
    public void History_carries_between_sectors()
    {
        // Two identical data areas; with filter 0 there's no feedback, so the second
        // area's first sample equals the first's — a simple continuity smoke test.
        var area = DataAreaWithFirstNibble(1, 0x00);
        var pcm = XaAdpcm.DecodeSectors(new[] { area, area }, stereo: false);
        Assert.Equal(2 * 18 * 8 * 28, pcm.Length);
        Assert.Equal(1 << 12, pcm[0]);
        Assert.Equal(1 << 12, pcm[18 * 8 * 28]);   // first sample of the second sector
    }

    [Fact]
    public void The_extractor_finds_audio_sectors_and_decodes_them()
    {
        // Two raw 2352 sectors: one marked audio (submode bit 2), one not.
        var img = new byte[2352 * 2];
        // Sector 0: audio, mono, 37800, first nibble 1.
        img[16 + 2] = 0x04;                 // submode: audio
        img[16 + 3] = 0x00;                 // coding: mono, 37800, 4-bit
        img[24 + 0] = 0x00;                 // SP0
        img[24 + 16] = 0x01;                // unit0 sample0 nibble = 1
        // Sector 1: data (not audio) — must be skipped.
        img[2352 + 16 + 2] = 0x08;          // submode: data bit

        using var ms = new MemoryStream(img);
        var result = XaExtract.Extract(ms, XaExtract.SectorLayout.Raw2352);

        Assert.Equal(1, result.SectorsUsed);
        Assert.Equal(1, result.Channels);
        Assert.Equal(37800, result.SampleRate);
        Assert.Equal(1 << 12, result.Pcm[0]);
    }

    [Fact]
    public void The_wav_writer_emits_a_valid_header_with_the_given_rate()
    {
        var wav = WavWriter.ToBytes(new short[] { 100, -100, 200, -200 }, 18900, 2);

        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2)));    // channels
        Assert.Equal(18900u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4))); // rate
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40, 4)));   // data bytes = 4 samples*2
    }
}
