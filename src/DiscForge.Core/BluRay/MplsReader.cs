// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.BluRay;

/// <summary>One elementary stream referenced by a PlayItem's STN table.</summary>
public sealed record StreamEntry
{
    /// <summary>Which STN section this entry came from (primary video, audio, PG…).</summary>
    public required StreamKind Kind { get; init; }
    /// <summary>The stream_coding_type byte (0x1B AVC, 0x81 AC-3, 0x90 PG, …).</summary>
    public required byte CodingType { get; init; }
    /// <summary>The transport-stream PID this stream is carried on.</summary>
    public required ushort Pid { get; init; }
    /// <summary>ISO-639 language code for audio/PG/text streams; empty for video.</summary>
    public string Language { get; init; } = "";
    /// <summary>Friendly coding-type name (e.g. "H.264/AVC").</summary>
    public string CodingName => BdmvCoding.Name(CodingType);
}

/// <summary>
/// One PlayItem: a contiguous span of a single .m2ts clip that a playlist plays,
/// bounded by IN_time and OUT_time on the 45 kHz clock, together with the streams
/// its STN table selects.
/// </summary>
public sealed record PlayItem
{
    /// <summary>The 5-digit clip id (the "%05d" name of the .m2ts / .clpi pair).</summary>
    public required string ClipId { get; init; }
    /// <summary>IN_time in 45 kHz ticks.</summary>
    public required long InTime { get; init; }
    /// <summary>OUT_time in 45 kHz ticks.</summary>
    public required long OutTime { get; init; }
    public required IReadOnlyList<StreamEntry> Streams { get; init; }

    /// <summary>Duration in 45 kHz ticks (OUT − IN).</summary>
    public long DurationTicks => OutTime - InTime;
    public TimeSpan Duration => BdmvTime.ToTimeSpan(DurationTicks);
    /// <summary>The clip's stream file name, e.g. "00001.m2ts".</summary>
    public string ClipFileName => $"{ClipId}.m2ts";
}

/// <summary>A PlayListMark — a chapter entry point (or link point) at a timestamp.</summary>
public sealed record PlaylistMark
{
    /// <summary>Index of the PlayItem this mark falls in.</summary>
    public required int PlayItemRef { get; init; }
    /// <summary>Mark timestamp in 45 kHz ticks (on the referenced clip's timeline).</summary>
    public required long TimeTicks { get; init; }
    /// <summary>The raw mark_type byte (0x01 entry/chapter mark, 0x02 link point).</summary>
    public required byte MarkType { get; init; }
    /// <summary>True for an entry mark — i.e. a real chapter stop.</summary>
    public bool IsChapter => MarkType == 0x01;
    public TimeSpan Time => BdmvTime.ToTimeSpan(TimeTicks);
}

/// <summary>A parsed .mpls playlist: its version, ordered PlayItems and chapter marks.</summary>
public sealed record BluRayPlaylist
{
    /// <summary>The 4-char version ("0100", "0200", "0300").</summary>
    public required string Version { get; init; }
    public required IReadOnlyList<PlayItem> Items { get; init; }
    public required IReadOnlyList<PlaylistMark> Marks { get; init; }

    /// <summary>Sum of every PlayItem's duration, in 45 kHz ticks.</summary>
    public long TotalDurationTicks => Items.Sum(i => i.DurationTicks);
    public TimeSpan TotalDuration => BdmvTime.ToTimeSpan(TotalDurationTicks);
    /// <summary>The chapter marks only (entry marks), in playlist order.</summary>
    public IReadOnlyList<PlaylistMark> Chapters => Marks.Where(m => m.IsChapter).ToList();
}

/// <summary>
/// Parses a Blu-ray movie playlist (.mpls), the file that defines a title: which
/// clips play, in what order, from which IN to which OUT time, which streams each
/// segment offers, and where the chapters are.
///
/// Layout (all multi-byte integers big-endian):
///   0x00  4   type_indicator            "MPLS"
///   0x04  4   version_number            "0100" / "0200" / "0300"
///   0x08  u32 PlayList_start_address
///   0x0C  u32 PlayListMark_start_address
///   0x10  u32 ExtensionData_start_address
///   0x14  20  reserved
///   0x28      AppInfoPlayList()         (skipped — we jump by the addresses above)
///
/// PlayList()      @ PlayList_start_address: length, reserved, number_of_PlayItems,
///                 number_of_SubPaths, then each PlayItem (length-prefixed).
/// PlayItem        carries the 5-char clip name + "M2TS" codec id, IN/OUT time,
///                 and an STN_table() selecting video/audio/PG/IG streams.
/// PlayListMark()  @ PlayListMark_start_address: a count then fixed 14-byte marks.
///
/// This reads structure only — it never touches the clip's .m2ts payload, and
/// BDMV metadata is unencrypted even on an AACS disc, so no protection is
/// involved. Clean-room from the public BDAV/BDMV format description.
/// </summary>
public static class MplsReader
{
    public const string Magic = "MPLS";

    /// <summary>Parse a playlist from a whole-file byte buffer.</summary>
    public static BluRayPlaylist Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var r = new BdmvReaderCursor(data);

        r.RequireMagic(Magic);
        string version = r.ReadAscii(4);

        uint playListStart = r.ReadU32();
        uint playListMarkStart = r.ReadU32();
        _ = r.ReadU32();   // ExtensionData_start_address — not decoded

        var items = ParsePlayList(r, playListStart);
        var marks = ParsePlayListMarks(r, playListMarkStart);

        return new BluRayPlaylist { Version = version, Items = items, Marks = marks };
    }

    /// <summary>Read a .mpls file and parse it.</summary>
    public static BluRayPlaylist ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Playlist not found.", path);
        return Parse(File.ReadAllBytes(path));
    }

    private static List<PlayItem> ParsePlayList(BdmvReaderCursor r, uint playListStart)
    {
        r.Seek(playListStart);
        _ = r.ReadU32();                 // length
        _ = r.ReadU16();                 // reserved
        int itemCount = r.ReadU16();     // number_of_PlayItems
        _ = r.ReadU16();                 // number_of_SubPaths

        var items = new List<PlayItem>(itemCount);
        for (int i = 0; i < itemCount; i++)
        {
            int itemStart = r.Position;
            int itemLen = r.ReadU16();   // length of this PlayItem's body
            int bodyEnd = itemStart + 2 + itemLen;

            var item = ParsePlayItem(r);

            // Advance to the next PlayItem by its declared length, so any
            // best-effort slack inside the STN table can't desynchronise us.
            r.Seek(bodyEnd);
            items.Add(item);
        }
        return items;
    }

    private static PlayItem ParsePlayItem(BdmvReaderCursor r)
    {
        string clipId = r.ReadAscii(5);      // clip_information_file_name
        _ = r.ReadAscii(4);                  // clip_codec_identifier ("M2TS")

        ushort flags = r.ReadU16();          // reserved(11) + is_multi_angle(1) + cc(4)
        bool multiAngle = ((flags >> 4) & 1) != 0;

        _ = r.ReadU8();                      // ref_to_STC_id
        long inTime = r.ReadU32();           // IN_time (45 kHz)
        long outTime = r.ReadU32();          // OUT_time (45 kHz)
        r.Skip(8);                           // UO_mask_table
        _ = r.ReadU8();                      // random_access_flag + reserved
        _ = r.ReadU8();                      // still_mode
        _ = r.ReadU16();                     // still_time / reserved

        if (multiAngle)
        {
            int angles = r.ReadU8();         // number_of_angles
            _ = r.ReadU8();                  // reserved + is_different_audios + is_seamless
            for (int a = 1; a < angles; a++)
                r.Skip(10);                  // clip name(5) + codec(4) + ref_to_STC_id(1)
        }

        var streams = ParseStnTable(r);

        return new PlayItem
        {
            ClipId = clipId,
            InTime = inTime,
            OutTime = outTime,
            Streams = streams,
        };
    }

    /// <summary>
    /// Parse the STN_table: a 16-byte header of per-class counts, then the stream
    /// entries in the documented order. Each entry is a length-prefixed
    /// stream_entry() (PID/ref) followed by a length-prefixed stream_attributes()
    /// (coding type + language), which lets us decode the fields we care about and
    /// skip anything trailing without tracking every reserved bit by hand.
    /// </summary>
    private static List<StreamEntry> ParseStnTable(BdmvReaderCursor r)
    {
        _ = r.ReadU16();                     // length
        _ = r.ReadU16();                     // reserved
        int nVideo = r.ReadU8();
        int nAudio = r.ReadU8();
        int nPg = r.ReadU8();
        int nIg = r.ReadU8();
        int nSecAudio = r.ReadU8();
        int nSecVideo = r.ReadU8();
        int nPipPg = r.ReadU8();
        r.Skip(5);                           // reserved

        var streams = new List<StreamEntry>();

        for (int i = 0; i < nVideo; i++) streams.Add(ReadStream(r, StreamKind.Video));
        for (int i = 0; i < nAudio; i++) streams.Add(ReadStream(r, StreamKind.Audio));
        // PG plane: primary PG entries, then PIP (secondary) PG entries.
        for (int i = 0; i < nPg; i++) streams.Add(ReadStream(r, StreamKind.PresentationGraphics));
        for (int i = 0; i < nPipPg; i++) streams.Add(ReadStream(r, StreamKind.PresentationGraphics));
        for (int i = 0; i < nIg; i++) streams.Add(ReadStream(r, StreamKind.InteractiveGraphics));

        // Secondary streams carry a small combination-info block after their
        // attributes; we read the streams and skip the ref block by its counts.
        for (int i = 0; i < nSecAudio; i++)
        {
            streams.Add(ReadStream(r, StreamKind.SecondaryAudio));
            SkipCombInfo(r);                 // primary_audio refs
        }
        for (int i = 0; i < nSecVideo; i++)
        {
            streams.Add(ReadStream(r, StreamKind.SecondaryVideo));
            SkipCombInfo(r);                 // secondary_audio refs
            SkipCombInfo(r);                 // PiP PG refs
        }

        return streams;
    }

    /// <summary>Read one stream_entry() + stream_attributes() pair.</summary>
    private static StreamEntry ReadStream(BdmvReaderCursor r, StreamKind kind)
    {
        // stream_entry(): length-prefixed. type 1 = in this clip, 2/3 = SubPath.
        int entryStart = r.Position;
        int entryLen = r.ReadU8();
        int entryEnd = entryStart + 1 + entryLen;
        byte type = r.ReadU8();
        ushort pid = 0;
        switch (type)
        {
            case 1:                          // played in the main clip
                pid = r.ReadU16();
                break;
            case 2:                          // SubPath: subpath id + subclip id + PID
                _ = r.ReadU8();
                _ = r.ReadU8();
                pid = r.ReadU16();
                break;
            case 3:                          // SubPath: subpath id + PID
                _ = r.ReadU8();
                pid = r.ReadU16();
                break;
        }
        r.Seek(entryEnd);

        // stream_attributes(): length-prefixed. coding type then per-class fields.
        int attrStart = r.Position;
        int attrLen = r.ReadU8();
        int attrEnd = attrStart + 1 + attrLen;
        byte coding = r.ReadU8();
        string language = "";
        if (BdmvCoding.IsAudio(coding))
        {
            _ = r.ReadU8();                  // audio_format(4) + sample_rate(4)
            language = r.ReadLanguage();
        }
        else if (coding is BdmvCoding.PresentationGraphics or BdmvCoding.InteractiveGraphics)
        {
            language = r.ReadLanguage();
        }
        else if (coding == BdmvCoding.TextSubtitle)
        {
            _ = r.ReadU8();                  // character_code
            language = r.ReadLanguage();
        }
        // video: video_format/frame_rate nibbles carry no language — nothing to read.
        r.Seek(attrEnd);

        return new StreamEntry { Kind = kind, CodingType = coding, Pid = pid, Language = language };
    }

    /// <summary>Skip a secondary-stream combination-info block: count, reserved,
    /// then that many 1-byte refs, word-aligned.</summary>
    private static void SkipCombInfo(BdmvReaderCursor r)
    {
        int count = r.ReadU8();
        _ = r.ReadU8();                      // reserved
        r.Skip(count);
        if ((count & 1) == 1) r.Skip(1);     // pad to a 16-bit boundary
    }

    private static List<PlaylistMark> ParsePlayListMarks(BdmvReaderCursor r, uint markStart)
    {
        r.Seek(markStart);
        _ = r.ReadU32();                     // length
        int count = r.ReadU16();             // number_of_PlayList_marks

        var marks = new List<PlaylistMark>(count);
        for (int i = 0; i < count; i++)
        {
            _ = r.ReadU8();                  // reserved
            byte markType = r.ReadU8();      // 0x01 entry mark (chapter), 0x02 link point
            int playItemRef = r.ReadU16();
            long timeStamp = r.ReadU32();    // mark_time_stamp (45 kHz)
            _ = r.ReadU16();                 // entry_ES_PID
            _ = r.ReadU32();                 // duration

            marks.Add(new PlaylistMark
            {
                PlayItemRef = playItemRef,
                TimeTicks = timeStamp,
                MarkType = markType,
            });
        }
        return marks;
    }
}
