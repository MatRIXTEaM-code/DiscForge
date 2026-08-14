// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.BluRay;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the Blu-ray BDMV readers (MPLS playlists, CLPI clip-info). There is
/// no BDMV oracle in this repo, so — as with the other no-oracle formats — the
/// fixtures are hand-built byte buffers assembled from the public format
/// description: real magic, real version, real big-endian offsets, one or two
/// PlayItems with IN/OUT times and stream entries, and chapter marks. A green
/// test means the parser agrees with my reading of the layout; the thing it
/// catches most reliably is endianness (BDMV is big-endian, unusual on a PC).
/// </summary>
public class BdmvTests
{
    // ---- little-endian-free builders (everything written big-endian) ----------

    private static void U16(List<byte> b, int v)
    {
        Span<byte> s = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(s, (ushort)v);
        b.AddRange(s.ToArray());
    }

    private static void U32(List<byte> b, long v)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(s, (uint)v);
        b.AddRange(s.ToArray());
    }

    private static void Ascii(List<byte> b, string s) => b.AddRange(Encoding.ASCII.GetBytes(s));

    private static void Lang(List<byte> b, string code)
    {
        var bytes = new byte[3];
        Encoding.ASCII.GetBytes(code).CopyTo(bytes, 0);
        b.AddRange(bytes);
    }

    // stream_entry(): length-prefixed; type 1 = played in this clip.
    private static byte[] StreamEntry(ushort pid)
    {
        var b = new List<byte> { 3 /*length*/, 1 /*type*/ };
        U16(b, pid);
        return b.ToArray();
    }

    private static byte[] VideoAttrs(byte coding)
    {
        var b = new List<byte> { 2 /*length*/, coding, 0x62 /*fmt/rate*/ };
        return b.ToArray();
    }

    private static byte[] AudioAttrs(byte coding, string lang)
    {
        var b = new List<byte> { 5 /*length*/, coding, 0x61 /*fmt/rate*/ };
        Lang(b, lang);
        return b.ToArray();
    }

    private static byte[] PgAttrs(byte coding, string lang)
    {
        var b = new List<byte> { 4 /*length*/, coding };
        Lang(b, lang);
        return b.ToArray();
    }

    private sealed record StreamDef(StreamKind Kind, ushort Pid, byte Coding, string Lang);

    private static byte[] StnTable(IReadOnlyList<StreamDef> streams)
    {
        int nV = streams.Count(s => s.Kind == StreamKind.Video);
        int nA = streams.Count(s => s.Kind == StreamKind.Audio);
        int nP = streams.Count(s => s.Kind == StreamKind.PresentationGraphics);
        int nI = streams.Count(s => s.Kind == StreamKind.InteractiveGraphics);

        var body = new List<byte>();
        U16(body, 0);                 // reserved
        body.Add((byte)nV);
        body.Add((byte)nA);
        body.Add((byte)nP);
        body.Add((byte)nI);
        body.Add(0);                  // secondary audio
        body.Add(0);                  // secondary video
        body.Add(0);                  // PiP PG
        body.AddRange(new byte[5]);   // reserved

        // Entries in STN order: video, audio, PG, IG.
        foreach (var s in streams.Where(s => s.Kind == StreamKind.Video))
        {
            body.AddRange(StreamEntry(s.Pid));
            body.AddRange(VideoAttrs(s.Coding));
        }
        foreach (var s in streams.Where(s => s.Kind == StreamKind.Audio))
        {
            body.AddRange(StreamEntry(s.Pid));
            body.AddRange(AudioAttrs(s.Coding, s.Lang));
        }
        foreach (var s in streams.Where(s => s.Kind == StreamKind.PresentationGraphics))
        {
            body.AddRange(StreamEntry(s.Pid));
            body.AddRange(PgAttrs(s.Coding, s.Lang));
        }

        var full = new List<byte>();
        U16(full, body.Count);        // STN length (bytes after this field)
        full.AddRange(body);
        return full.ToArray();
    }

    private static byte[] PlayItem(string clipId, long inTime, long outTime,
                                   IReadOnlyList<StreamDef> streams)
    {
        var body = new List<byte>();
        Ascii(body, clipId);          // clip_information_file_name (5)
        Ascii(body, "M2TS");          // clip_codec_identifier
        U16(body, 0);                 // reserved + is_multi_angle + connection_condition
        body.Add(0);                  // ref_to_STC_id
        U32(body, inTime);            // IN_time
        U32(body, outTime);           // OUT_time
        body.AddRange(new byte[8]);   // UO_mask_table
        body.Add(0);                  // random_access_flag + reserved
        body.Add(0);                  // still_mode
        U16(body, 0);                 // still_time / reserved
        body.AddRange(StnTable(streams));

        var full = new List<byte>();
        U16(full, body.Count);        // PlayItem length
        full.AddRange(body);
        return full.ToArray();
    }

    private sealed record MarkDef(byte Type, int PlayItemRef, long Ts);

    private static byte[] BuildMpls(string version,
                                    IReadOnlyList<byte[]> items,
                                    IReadOnlyList<MarkDef> marks)
    {
        // AppInfoPlayList: length(4) then 14 reserved bytes.
        var appInfo = new List<byte>();
        U32(appInfo, 14);
        appInfo.AddRange(new byte[14]);

        // PlayList section.
        var plBody = new List<byte>();
        U16(plBody, 0);               // reserved
        U16(plBody, items.Count);     // number_of_PlayItems
        U16(plBody, 0);               // number_of_SubPaths
        foreach (var it in items) plBody.AddRange(it);
        var playList = new List<byte>();
        U32(playList, plBody.Count);
        playList.AddRange(plBody);

        // PlayListMark section.
        var mkBody = new List<byte>();
        U16(mkBody, marks.Count);
        foreach (var m in marks)
        {
            mkBody.Add(0);            // reserved
            mkBody.Add(m.Type);       // mark_type
            U16(mkBody, m.PlayItemRef);
            U32(mkBody, m.Ts);        // mark_time_stamp
            U16(mkBody, 0);           // entry_ES_PID
            U32(mkBody, 0);           // duration
        }
        var marksSection = new List<byte>();
        U32(marksSection, mkBody.Count);
        marksSection.AddRange(mkBody);

        int headerLen = 40;           // 0x00..0x27
        long playListStart = headerLen + appInfo.Count;
        long markStart = playListStart + playList.Count;

        var file = new List<byte>();
        Ascii(file, "MPLS");
        Ascii(file, version);
        U32(file, playListStart);
        U32(file, markStart);
        U32(file, 0);                 // ExtensionData_start_address
        file.AddRange(new byte[20]);  // reserved -> 0x28
        file.AddRange(appInfo);
        file.AddRange(playList);
        file.AddRange(marksSection);
        return file.ToArray();
    }

    // CLPI StreamCodingInfo builders.
    private static byte[] ClpiVideo(ushort pid, byte coding)
    {
        var b = new List<byte>();
        U16(b, pid);
        b.Add(3);                     // StreamCodingInfo length
        b.Add(coding);
        b.Add(0x62);                  // video_format(6)=1080p + frame_rate(2)=24
        b.Add(0x30);                  // aspect_ratio(3)=16:9
        return b.ToArray();
    }

    private static byte[] ClpiAudio(ushort pid, byte coding, string lang)
    {
        var b = new List<byte>();
        U16(b, pid);
        b.Add(5);                     // StreamCodingInfo length
        b.Add(coding);
        b.Add(0x61);                  // audio_format(6)=multichannel + sample_rate(1)=48kHz
        Lang(b, lang);
        return b.ToArray();
    }

    private static byte[] BuildClpi(string version, IReadOnlyList<byte[]> streams)
    {
        var piBody = new List<byte>();
        piBody.Add(0);                // reserved
        piBody.Add(1);                // number_of_programs
        U32(piBody, 0);               // SPN_program_sequence_start
        U16(piBody, 0x0100);          // program_map_PID
        piBody.Add((byte)streams.Count);
        piBody.Add(0);                // reserved
        foreach (var s in streams) piBody.AddRange(s);

        var programInfo = new List<byte>();
        U32(programInfo, piBody.Count);
        programInfo.AddRange(piBody);

        int headerLen = 40;           // 0x00..0x27
        long programInfoStart = headerLen;

        var file = new List<byte>();
        Ascii(file, "HDMV");
        Ascii(file, version);
        U32(file, 0);                 // SequenceInfo
        U32(file, programInfoStart);  // ProgramInfo
        U32(file, 0);                 // CPI
        U32(file, 0);                 // ClipMark
        U32(file, 0);                 // ExtensionData
        file.AddRange(new byte[12]);  // reserved -> 0x28
        file.AddRange(programInfo);
        return file.ToArray();
    }

    private static byte[] SampleMpls() => BuildMpls("0200",
        new[]
        {
            PlayItem("00001", 0, 2_700_000, new[]
            {
                new StreamDef(StreamKind.Video, 0x1011, BdmvCoding.Avc, ""),
                new StreamDef(StreamKind.Audio, 0x1100, BdmvCoding.Ac3, "eng"),
                new StreamDef(StreamKind.PresentationGraphics, 0x1200, BdmvCoding.PresentationGraphics, "spa"),
            }),
            PlayItem("00002", 45_000, 1_395_000, new[]
            {
                new StreamDef(StreamKind.Video, 0x1011, BdmvCoding.Hevc, ""),
                new StreamDef(StreamKind.Audio, 0x1100, BdmvCoding.DtsHdMa, "jpn"),
            }),
        },
        new[]
        {
            new MarkDef(0x01, 0, 0),          // chapter 1
            new MarkDef(0x01, 1, 45_000),     // chapter 2
            new MarkDef(0x02, 1, 900_000),    // link point (not a chapter)
        });

    // ---- MPLS tests -----------------------------------------------------------

    [Fact]
    public void Mpls_ParsesHeaderAndPlayItemCount()
    {
        var pl = MplsReader.Parse(SampleMpls());
        Assert.Equal("0200", pl.Version);
        Assert.Equal(2, pl.Items.Count);
        Assert.Equal("00001", pl.Items[0].ClipId);
        Assert.Equal("00002.m2ts", pl.Items[1].ClipFileName);
    }

    [Fact]
    public void Mpls_InOutTimesAndDurations()
    {
        var pl = MplsReader.Parse(SampleMpls());
        Assert.Equal(0, pl.Items[0].InTime);
        Assert.Equal(2_700_000, pl.Items[0].OutTime);
        Assert.Equal(2_700_000, pl.Items[0].DurationTicks);      // 60.000 s
        Assert.Equal(1_350_000, pl.Items[1].DurationTicks);      // 30.000 s
    }

    [Fact]
    public void Mpls_TotalDurationSumsItems()
    {
        var pl = MplsReader.Parse(SampleMpls());
        Assert.Equal(4_050_000, pl.TotalDurationTicks);          // 90.000 s
        Assert.Equal(90.0, pl.TotalDuration.TotalSeconds, 3);
    }

    [Fact]
    public void Mpls_StreamEntriesCarryKindPidCodingLanguage()
    {
        var pl = MplsReader.Parse(SampleMpls());
        var streams = pl.Items[0].Streams;
        Assert.Equal(3, streams.Count);

        Assert.Equal(StreamKind.Video, streams[0].Kind);
        Assert.Equal((ushort)0x1011, streams[0].Pid);
        Assert.Equal(BdmvCoding.Avc, streams[0].CodingType);
        Assert.Equal("", streams[0].Language);

        Assert.Equal(StreamKind.Audio, streams[1].Kind);
        Assert.Equal((ushort)0x1100, streams[1].Pid);
        Assert.Equal(BdmvCoding.Ac3, streams[1].CodingType);
        Assert.Equal("eng", streams[1].Language);

        Assert.Equal(StreamKind.PresentationGraphics, streams[2].Kind);
        Assert.Equal((ushort)0x1200, streams[2].Pid);
        Assert.Equal("spa", streams[2].Language);
    }

    [Fact]
    public void Mpls_ChaptersFilterLinkPointsAndKeepTimestamps()
    {
        var pl = MplsReader.Parse(SampleMpls());
        Assert.Equal(3, pl.Marks.Count);
        var chapters = pl.Chapters;
        Assert.Equal(2, chapters.Count);                          // the link point is excluded
        Assert.True(chapters[0].IsChapter);
        Assert.Equal(0, chapters[0].PlayItemRef);
        Assert.Equal(0, chapters[0].TimeTicks);
        Assert.Equal(1, chapters[1].PlayItemRef);
        Assert.Equal(45_000, chapters[1].TimeTicks);
        Assert.False(pl.Marks[2].IsChapter);
    }

    // ---- CLPI tests -----------------------------------------------------------

    private static byte[] SampleClpi() => BuildClpi("0100",
        new[]
        {
            ClpiVideo(0x1011, BdmvCoding.Avc),
            ClpiAudio(0x1100, BdmvCoding.Ac3, "eng"),
        });

    [Fact]
    public void Clpi_ParsesVersionAndStreamCount()
    {
        var clip = ClpiReader.Parse(SampleClpi());
        Assert.Equal("0100", clip.Version);
        Assert.Equal(2, clip.Streams.Count);
    }

    [Fact]
    public void Clpi_VideoStreamAttributes()
    {
        var clip = ClpiReader.Parse(SampleClpi());
        var v = clip.Streams[0];
        Assert.Equal((ushort)0x1011, v.Pid);
        Assert.Equal(BdmvCoding.Avc, v.CodingType);
        Assert.Equal(StreamKind.Video, v.Kind);
        Assert.Equal("1080p", v.VideoFormat);
        Assert.Equal("24", v.FrameRate);
        Assert.Equal("16:9", v.AspectRatio);
    }

    [Fact]
    public void Clpi_AudioStreamAttributes()
    {
        var clip = ClpiReader.Parse(SampleClpi());
        var a = clip.Streams[1];
        Assert.Equal((ushort)0x1100, a.Pid);
        Assert.Equal(BdmvCoding.Ac3, a.CodingType);
        Assert.Equal(StreamKind.Audio, a.Kind);
        Assert.Equal("multichannel", a.AudioFormat);
        Assert.Equal("48kHz", a.SampleRate);
        Assert.Equal("eng", a.Language);
    }

    // ---- time helper ----------------------------------------------------------

    [Fact]
    public void BdmvTime_FormatsTicksAsClock()
    {
        Assert.Equal("00:00:01.000", BdmvTime.Format(45_000));
        Assert.Equal("00:01:00.000", BdmvTime.Format(2_700_000));
        Assert.Equal("01:00:00.000", BdmvTime.Format(45_000L * 3600));
    }

    // ---- malformed input throws the domain exception --------------------------

    [Fact]
    public void Mpls_ShortInputThrowsDomainException()
    {
        Assert.Throws<BluRayFormatException>(() => MplsReader.Parse(new byte[3]));
    }

    [Fact]
    public void Mpls_BadMagicThrowsDomainException()
    {
        var bad = new byte[64];
        Encoding.ASCII.GetBytes("XXXX0200").CopyTo(bad, 0);
        Assert.Throws<BluRayFormatException>(() => MplsReader.Parse(bad));
    }

    [Fact]
    public void Mpls_TruncatedBodyThrowsDomainException()
    {
        // Valid header, but the PlayList start address points past the file end.
        var buf = new byte[40];
        Encoding.ASCII.GetBytes("MPLS0200").CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x08), 10_000);   // playlist addr
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x0C), 10_000);   // mark addr
        Assert.Throws<BluRayFormatException>(() => MplsReader.Parse(buf));
    }

    [Fact]
    public void Clpi_ShortInputThrowsDomainException()
    {
        Assert.Throws<BluRayFormatException>(() => ClpiReader.Parse(new byte[2]));
    }

    // ---- folder enumeration ---------------------------------------------------

    [Fact]
    public void EnumerateTitles_CorrelatesPlaylistToClipInfo()
    {
        string root = Path.Combine(Path.GetTempPath(), "dforge_bdmv_" + Guid.NewGuid().ToString("N"));
        try
        {
            string playlistDir = Path.Combine(root, "BDMV", "PLAYLIST");
            string clipDir = Path.Combine(root, "BDMV", "CLIPINF");
            Directory.CreateDirectory(playlistDir);
            Directory.CreateDirectory(clipDir);
            File.WriteAllBytes(Path.Combine(playlistDir, "00000.mpls"), SampleMpls());
            File.WriteAllBytes(Path.Combine(clipDir, "00001.clpi"), SampleClpi());
            File.WriteAllBytes(Path.Combine(clipDir, "00002.clpi"), SampleClpi());

            var titles = BdmvReader.EnumerateTitles(root);
            Assert.Single(titles);
            var title = titles[0];
            Assert.Equal("00000.mpls", title.PlaylistFile);
            Assert.Equal(2, title.ChapterCount);
            Assert.Equal(4_050_000, title.Playlist.TotalDurationTicks);
            Assert.Equal(new[] { "00001", "00002" }, title.ClipIds);
            Assert.True(title.Clips.ContainsKey("00001"));
            Assert.Equal(2, title.Clips["00001"].Streams.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
