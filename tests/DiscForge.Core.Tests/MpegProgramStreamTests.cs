using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DiscForge.Core.Mpeg;
using Xunit;

namespace DiscForge.Core.Tests;

public class MpegProgramStreamTests
{
    private static void PutStartCode(List<byte> b, byte code)
    {
        b.Add(0x00); b.Add(0x00); b.Add(0x01); b.Add(code);
    }

    private static void PutPes(List<byte> b, byte streamId, byte[] payload)
    {
        PutStartCode(b, streamId);
        b.Add((byte)(payload.Length >> 8));
        b.Add((byte)(payload.Length & 0xFF));
        b.AddRange(payload);
    }

    // A minimal MPEG-1 program stream: one pack, a video, an audio and a
    // private_stream_1 (AC3) PES packet, then the program-end code.
    private static byte[] SampleStream()
    {
        var b = new List<byte>();
        // MPEG-1 pack header (top 2 bits of the byte after BA are not '01').
        PutStartCode(b, 0xBA);
        b.AddRange(new byte[] { 0x21, 0, 0, 0, 0, 0, 0, 0 });

        // MPEG-1 PES: 0x0F = "no PTS/DTS", then the payload.
        PutPes(b, 0xE0, new byte[] { 0x0F }.Concat(Encoding.ASCII.GetBytes("VID")).ToArray());
        PutPes(b, 0xC0, new byte[] { 0x0F }.Concat(Encoding.ASCII.GetBytes("AUD")).ToArray());
        // private_1: after the PES header, [substream=0x80][3-byte AC3 hdr][data].
        PutPes(b, 0xBD, new byte[] { 0x0F, 0x80, 0x01, 0x00, 0x04 }.Concat(Encoding.ASCII.GetBytes("AC3")).ToArray());

        PutStartCode(b, 0xB9);   // program end
        return b.ToArray();
    }

    [Fact]
    public void Demux_counts_packs_and_pes_packets()
    {
        var r = MpegProgramStream.Demux(SampleStream());
        Assert.Equal(1, r.PackCount);
        Assert.Equal(3, r.PesPacketCount);
        Assert.False(r.SawMpeg2);
    }

    [Fact]
    public void Demux_separates_video_audio_and_private_streams()
    {
        var r = MpegProgramStream.Demux(SampleStream());

        var video = r.Streams.Single(s => s.Kind == MpegStreamKind.Video);
        Assert.Equal(0xE0, video.StreamId);
        Assert.Equal("VID", Encoding.ASCII.GetString(video.Data));

        var audio = r.Streams.Single(s => s.Kind == MpegStreamKind.Audio);
        Assert.Equal(0xC0, audio.StreamId);
        Assert.Equal("AUD", Encoding.ASCII.GetString(audio.Data));

        var priv = r.Streams.Single(s => s.Kind == MpegStreamKind.Private1);
        Assert.Equal(0xBD, priv.StreamId);
        Assert.Equal(0x80, priv.SubStreamId);
        Assert.Equal("AC3", Encoding.ASCII.GetString(priv.Data));   // sub-header stripped
    }

    [Fact]
    public void Private1_ac3_substream_suggests_an_ac3_name()
    {
        var r = MpegProgramStream.Demux(SampleStream());
        var priv = r.Streams.Single(s => s.Kind == MpegStreamKind.Private1);
        Assert.EndsWith(".ac3", priv.SuggestedName());
    }

    [Fact]
    public void Mpeg2_pack_header_is_detected_and_skipped()
    {
        var b = new List<byte>();
        PutStartCode(b, 0xBA);
        // MPEG-2 pack: byte after BA has top 2 bits '01'; last of 10 bytes carries
        // a 3-bit stuffing length (0 here).
        b.AddRange(new byte[] { 0x44, 0, 0, 0, 0, 0, 0, 0, 0, 0x00 });
        PutPes(b, 0xE0, new byte[] { 0x80, 0x00, 0x00 }.Concat(Encoding.ASCII.GetBytes("HELLO")).ToArray());

        var r = MpegProgramStream.Demux(b.ToArray());
        Assert.True(r.SawMpeg2);
        Assert.Equal(1, r.PackCount);
        var video = r.Streams.Single(s => s.Kind == MpegStreamKind.Video);
        Assert.Equal("HELLO", Encoding.ASCII.GetString(video.Data));
    }

    [Fact]
    public void Same_stream_id_across_packets_is_concatenated()
    {
        var b = new List<byte>();
        PutStartCode(b, 0xBA);
        b.AddRange(new byte[] { 0x21, 0, 0, 0, 0, 0, 0, 0 });
        PutPes(b, 0xE0, new byte[] { 0x0F }.Concat(Encoding.ASCII.GetBytes("AAA")).ToArray());
        PutPes(b, 0xE0, new byte[] { 0x0F }.Concat(Encoding.ASCII.GetBytes("BBB")).ToArray());

        var r = MpegProgramStream.Demux(b.ToArray());
        var video = r.Streams.Single(s => s.Kind == MpegStreamKind.Video);
        Assert.Equal("AAABBB", Encoding.ASCII.GetString(video.Data));
        Assert.Equal(2, video.PacketCount);
    }

    [Fact]
    public void Demux_from_stream_matches_span_overload()
    {
        var data = SampleStream();
        using var ms = new MemoryStream(data);
        var r = MpegProgramStream.Demux(ms);
        Assert.Equal(3, r.PesPacketCount);
    }
}
