using System.Collections.Generic;
using System.Linq;
using System.Text;
using DiscForge.Core.Mpeg;
using Xunit;

namespace DiscForge.Core.Tests;

public class MpegProgramStreamEdgeTests
{
    private static void Sc(List<byte> b, byte code) { b.Add(0); b.Add(0); b.Add(1); b.Add(code); }
    private static void Pes(List<byte> b, byte id, byte[] payload)
    {
        Sc(b, id);
        b.Add((byte)(payload.Length >> 8));
        b.Add((byte)(payload.Length & 0xFF));
        b.AddRange(payload);
    }
    private static void Pack(List<byte> b) { Sc(b, 0xBA); b.AddRange(new byte[] { 0x21, 0, 0, 0, 0, 0, 0, 0 }); }

    [Fact]
    public void Empty_input_yields_nothing()
    {
        var r = MpegProgramStream.Demux(System.Array.Empty<byte>());
        Assert.Empty(r.Streams);
        Assert.Equal(0, r.PackCount);
        Assert.Equal(0, r.PesPacketCount);
    }

    [Fact]
    public void Bytes_with_no_start_codes_yield_nothing()
    {
        var r = MpegProgramStream.Demux(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Empty(r.Streams);
    }

    [Fact]
    public void System_header_and_padding_are_skipped_and_not_counted()
    {
        var b = new List<byte>();
        Pack(b);
        Pes(b, 0xBB, new byte[] { 1, 2, 3 });                 // system header
        Pes(b, 0xBE, new byte[] { 9, 9, 9, 9 });              // padding stream
        Pes(b, 0xE0, new byte[] { 0x0F }.Concat(Encoding.ASCII.GetBytes("V")).ToArray());

        var r = MpegProgramStream.Demux(b.ToArray());
        Assert.Equal(1, r.PesPacketCount);                    // only the real video PES
        Assert.Single(r.Streams);
        Assert.Equal(MpegStreamKind.Video, r.Streams[0].Kind);
    }

    [Fact]
    public void Private_stream_2_navigation_is_captured()
    {
        var b = new List<byte>();
        Pack(b);
        Pes(b, 0xBF, new byte[] { 0xAA, 0xBB, 0xCC });        // nav pack, no PES header
        var r = MpegProgramStream.Demux(b.ToArray());
        var nav = r.Streams.Single(s => s.Kind == MpegStreamKind.Private2);
        Assert.Equal(0xBF, nav.StreamId);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, nav.Data);
        Assert.Equal("nav_bf.bin", nav.SuggestedName());
    }

    [Fact]
    public void Truncated_pes_length_is_tolerated()
    {
        var b = new List<byte>();
        Pack(b);
        Sc(b, 0xE0);
        b.Add(0x00); b.Add(0x64);                             // claims 100 bytes
        b.Add(0x0F);
        b.AddRange(Encoding.ASCII.GetBytes("ABCD"));          // but only 5 are present
        var r = MpegProgramStream.Demux(b.ToArray());
        var v = r.Streams.Single(s => s.Kind == MpegStreamKind.Video);
        Assert.Equal("ABCD", Encoding.ASCII.GetString(v.Data));
    }

    [Fact]
    public void Different_video_ids_stay_separate()
    {
        var b = new List<byte>();
        Pack(b);
        Pes(b, 0xE0, new byte[] { 0x0F, (byte)'A' });
        Pes(b, 0xE1, new byte[] { 0x0F, (byte)'B' });
        var r = MpegProgramStream.Demux(b.ToArray());
        Assert.Equal(2, r.Streams.Count(s => s.Kind == MpegStreamKind.Video));
        Assert.Contains(r.Streams, s => s.StreamId == 0xE0);
        Assert.Contains(r.Streams, s => s.StreamId == 0xE1);
    }

    [Fact]
    public void Private1_substreams_get_correct_names_and_strip_lengths()
    {
        var b = new List<byte>();
        Pack(b);
        Pes(b, 0xBD, new byte[] { 0x0F, 0x80, 0, 0, 0 }.Concat(Encoding.ASCII.GetBytes("AC")).ToArray()); // AC3, 4-byte hdr
        Pes(b, 0xBD, new byte[] { 0x0F, 0x88, 0, 0, 0 }.Concat(Encoding.ASCII.GetBytes("DT")).ToArray()); // DTS, 4-byte hdr
        Pes(b, 0xBD, new byte[] { 0x0F, 0xA0, 0, 0, 0, 0, 0, 0 }.Concat(Encoding.ASCII.GetBytes("PC")).ToArray()); // LPCM, 7-byte hdr
        Pes(b, 0xBD, new byte[] { 0x0F, 0x20, 0, 0, 0 }.Concat(Encoding.ASCII.GetBytes("SP")).ToArray()); // subpicture

        var r = MpegProgramStream.Demux(b.ToArray());
        var ac3 = r.Streams.Single(s => s.SubStreamId == 0x80);
        var dts = r.Streams.Single(s => s.SubStreamId == 0x88);
        var pcm = r.Streams.Single(s => s.SubStreamId == 0xA0);
        var sup = r.Streams.Single(s => s.SubStreamId == 0x20);

        Assert.EndsWith(".ac3", ac3.SuggestedName()); Assert.Equal("AC", Encoding.ASCII.GetString(ac3.Data));
        Assert.EndsWith(".dts", dts.SuggestedName()); Assert.Equal("DT", Encoding.ASCII.GetString(dts.Data));
        Assert.EndsWith(".lpcm", pcm.SuggestedName()); Assert.Equal("PC", Encoding.ASCII.GetString(pcm.Data));
        Assert.EndsWith(".sup", sup.SuggestedName()); Assert.Equal("SP", Encoding.ASCII.GetString(sup.Data));
    }
}
