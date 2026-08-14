// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.DvdVideo;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the DVD-Video IFO reader (the DvdVideo one — the survivor of the
/// duplicate-reader merge).
///
/// These pin the parser to what the format specification says, using IFOs built
/// byte by byte through a fake <see cref="IfoReader.IVideoTsSource"/>. Passing
/// tests mean the code matches my reading of the spec, not that it matches a
/// real DVD; if a genuine disc later produces nonsense, these are what tell us
/// whether the code drifted or the reading was wrong in the first place.
///
/// The thing they catch reliably is endianness. DVD structures are big-endian,
/// which is unusual on a PC and the single commonest source of absurd results —
/// 24 chapters read as 6144, that sort of thing.
/// </summary>
public class IfoReaderTests
{
    private const int SectorSize = 2048;

    /// <summary>In-memory VIDEO_TS: file contents for IFOs, plain sizes for VOBs.</summary>
    private sealed class FakeSource : IfoReader.IVideoTsSource
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public byte[]? ReadFile(string name) => Files.TryGetValue(name, out var b) ? b : null;
        public long FileSize(string name) =>
            Sizes.TryGetValue(name, out var s) ? s :
            Files.TryGetValue(name, out var b) ? b.Length : 0;
    }

    /// <summary>
    /// Build a video manager IFO: the signature, the title-set count at 0x3E,
    /// a pointer to the title table at 0xC4, and the TT_SRPT itself in the
    /// second sector.
    /// </summary>
    private static byte[] BuildVideoManager(int vtsCount,
                                            params (int Chapters, int Angles, int TitleSet, int VtsTitle)[] titles)
    {
        var ifo = new byte[SectorSize * 2];

        Encoding.ASCII.GetBytes("DVDVIDEO-VMG").CopyTo(ifo, 0);
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(0x3E), (ushort)vtsCount);

        // TT_SRPT lives at sector 1 of this file; the pointer is big-endian.
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0xC4), 1);

        int table = SectorSize;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(table), (ushort)titles.Length);

        for (int i = 0; i < titles.Length; i++)
        {
            int at = table + 8 + i * 12;
            ifo[at] = 0x00;                                   // playback type
            ifo[at + 1] = (byte)titles[i].Angles;             // low nibble
            BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(at + 2), (ushort)titles[i].Chapters);
            ifo[at + 6] = (byte)titles[i].TitleSet;
            ifo[at + 7] = (byte)titles[i].VtsTitle;
        }

        return ifo;
    }

    /// <summary>Build a title set IFO carrying the given TITLE-domain streams.
    /// Audio: count at 0x203, 8-byte attributes from 0x204. Subpictures: count
    /// at 0x255, 6-byte attributes from 0x256.</summary>
    private static byte[] BuildTitleSet(
        (int CodingMode, int Channels, string? Lang)[]? audio = null,
        string?[]? subtitles = null)
    {
        var ifo = new byte[SectorSize];
        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(ifo, 0);

        audio ??= Array.Empty<(int, int, string?)>();
        subtitles ??= Array.Empty<string?>();

        ifo[0x203] = (byte)audio.Length;
        for (int i = 0; i < audio.Length; i++)
        {
            int a = 0x204 + i * 8;
            ifo[a] = (byte)(audio[i].CodingMode << 5);
            if (audio[i].Lang is { Length: 2 } lang)
            {
                ifo[a] |= 0x04;                    // language-present bits == 1
                ifo[a + 2] = (byte)lang[0];
                ifo[a + 3] = (byte)lang[1];
            }
            ifo[a + 1] = (byte)(audio[i].Channels - 1);
        }

        ifo[0x255] = (byte)subtitles.Length;
        for (int i = 0; i < subtitles.Length; i++)
        {
            int s = 0x256 + i * 6;
            if (subtitles[i] is { Length: 2 } lang)
            {
                ifo[s] = 0x01;                     // subp_attr type == 1 (language present), low 2 bits
                ifo[s + 2] = (byte)lang[0];
                ifo[s + 3] = (byte)lang[1];
            }
        }

        return ifo;
    }

    private static FakeSource DiscWith(byte[] vmg, params (int Number, byte[] Ifo)[] sets)
    {
        var src = new FakeSource();
        src.Files["VIDEO_TS.IFO"] = vmg;
        foreach (var (number, ifo) in sets)
            src.Files[$"VTS_{number:00}_0.IFO"] = ifo;
        return src;
    }

    // --- refusals ------------------------------------------------------------

    [Fact]
    public void A_missing_video_manager_is_refused()
    {
        Assert.Throws<IfoFormatException>(() => IfoReader.Read(new FakeSource()));
    }

    [Fact]
    public void A_file_without_the_signature_is_refused()
    {
        var src = new FakeSource();
        var notAnIfo = new byte[SectorSize];
        Encoding.ASCII.GetBytes("NOT AN IFO !").CopyTo(notAnIfo, 0);
        src.Files["VIDEO_TS.IFO"] = notAnIfo;

        var ex = Assert.Throws<IfoFormatException>(() => IfoReader.Read(src));
        Assert.Contains("VMG", ex.Message);
    }

    [Fact]
    public void A_file_too_short_to_be_an_ifo_is_refused()
    {
        var src = new FakeSource();
        src.Files["VIDEO_TS.IFO"] = Encoding.ASCII.GetBytes("DVDVIDEO-VMG");

        Assert.Throws<IfoFormatException>(() => IfoReader.Read(src));
    }

    // --- the title table -----------------------------------------------------

    [Fact]
    public void Titles_are_read_with_their_chapter_and_angle_counts()
    {
        var vmg = BuildVideoManager(1,
            (Chapters: 24, Angles: 1, TitleSet: 1, VtsTitle: 1),
            (Chapters: 3, Angles: 2, TitleSet: 1, VtsTitle: 2));
        var src = DiscWith(vmg, (1, BuildTitleSet()));

        var dvd = IfoReader.Read(src);

        Assert.Equal(2, dvd.Titles.Count);
        Assert.Equal(24, dvd.Titles[0].Chapters);
        Assert.Equal(1, dvd.Titles[0].AngleCount);
        Assert.Equal(3, dvd.Titles[1].Chapters);
        Assert.Equal(2, dvd.Titles[1].AngleCount);
    }

    [Fact]
    public void Chapter_counts_are_big_endian()
    {
        // 300 chapters is 0x012C. Read little-endian it would be 0x2C01 = 11265 —
        // the classic absurd result this test exists to prevent.
        var vmg = BuildVideoManager(1, (Chapters: 300, Angles: 1, TitleSet: 1, VtsTitle: 1));
        var src = DiscWith(vmg, (1, BuildTitleSet()));

        Assert.Equal(300, IfoReader.Read(src).Titles[0].Chapters);
    }

    [Fact]
    public void An_angle_count_of_zero_is_reported_as_one()
    {
        // Angles live in a nibble and zero is authoring sloppiness, not a disc
        // with no angles: everything plays through exactly one.
        var vmg = BuildVideoManager(1, (Chapters: 5, Angles: 0, TitleSet: 1, VtsTitle: 1));
        var src = DiscWith(vmg, (1, BuildTitleSet()));

        Assert.Equal(1, IfoReader.Read(src).Titles[0].AngleCount);
    }

    [Fact]
    public void Titles_are_numbered_globally_across_title_sets()
    {
        var vmg = BuildVideoManager(2,
            (Chapters: 12, Angles: 1, TitleSet: 1, VtsTitle: 1),
            (Chapters: 4, Angles: 1, TitleSet: 2, VtsTitle: 1),
            (Chapters: 6, Angles: 1, TitleSet: 2, VtsTitle: 2));
        var src = DiscWith(vmg, (1, BuildTitleSet()), (2, BuildTitleSet()));

        var dvd = IfoReader.Read(src);

        Assert.Equal(new[] { 1, 2, 3 }, dvd.Titles.Select(t => t.TitleNumber).ToArray());
        Assert.Equal(new[] { 1, 2, 2 }, dvd.Titles.Select(t => t.TitleSet).ToArray());
        Assert.Single(dvd.TitleSets[0].Titles);
        Assert.Equal(2, dvd.TitleSets[1].Titles.Count);
    }

    [Fact]
    public void A_gap_in_title_set_numbering_is_skipped_not_fatal()
    {
        // VTS 2 is declared in the count but its IFO is absent. The set — and
        // the titles pointing into it — simply do not appear.
        var vmg = BuildVideoManager(3,
            (Chapters: 10, Angles: 1, TitleSet: 1, VtsTitle: 1),
            (Chapters: 7, Angles: 1, TitleSet: 2, VtsTitle: 1),
            (Chapters: 2, Angles: 1, TitleSet: 3, VtsTitle: 1));
        var src = DiscWith(vmg, (1, BuildTitleSet()), (3, BuildTitleSet()));

        var dvd = IfoReader.Read(src);

        Assert.Equal(2, dvd.TitleSets.Count);
        Assert.Equal(new[] { 1, 3 }, dvd.TitleSets.Select(s => s.Number).ToArray());
        Assert.Equal(2, dvd.Titles.Count);
        Assert.DoesNotContain(dvd.Titles, t => t.TitleSet == 2);
    }

    // --- VOB sizes -----------------------------------------------------------

    [Fact]
    public void Title_vob_parts_are_summed_until_the_first_gap()
    {
        var vmg = BuildVideoManager(1, (Chapters: 8, Angles: 1, TitleSet: 1, VtsTitle: 1));
        var src = DiscWith(vmg, (1, BuildTitleSet()));
        src.Sizes["VTS_01_1.VOB"] = 1_000_000;
        src.Sizes["VTS_01_2.VOB"] = 2_000_000;
        // No part 3 — part 4 must not be counted even though it "exists":
        src.Sizes["VTS_01_4.VOB"] = 4_000_000;

        var dvd = IfoReader.Read(src);

        Assert.Equal(3_000_000, dvd.TitleSets[0].TitleVobBytes);
    }

    [Fact]
    public void Menu_vobs_are_kept_apart_from_title_video()
    {
        var vmg = BuildVideoManager(1, (Chapters: 8, Angles: 1, TitleSet: 1, VtsTitle: 1));
        var src = DiscWith(vmg, (1, BuildTitleSet()));
        src.Sizes["VIDEO_TS.VOB"] = 111;          // VMG menu
        src.Sizes["VTS_01_0.VOB"] = 222;          // VTS menu
        src.Sizes["VTS_01_1.VOB"] = 5_000;        // actual video

        var dvd = IfoReader.Read(src);

        Assert.Equal(111, dvd.MenuVobBytes);
        Assert.Equal(222, dvd.TitleSets[0].MenuVobBytes);
        Assert.Equal(5_000, dvd.TitleSets[0].TitleVobBytes);
        Assert.Equal(5_000, dvd.TotalVideoBytes);
        Assert.Equal(333, dvd.TotalMenuBytes);
    }

    // --- streams -------------------------------------------------------------

    [Fact]
    public void Audio_streams_carry_codec_channels_and_language()
    {
        var vts = BuildTitleSet(audio: new[]
        {
            (CodingMode: 0, Channels: 6, Lang: (string?)"en"),   // AC3 5.1
            (CodingMode: 6, Channels: 2, Lang: (string?)"fr"),   // DTS stereo
            (CodingMode: 4, Channels: 2, Lang: (string?)null),   // LPCM, no language
        });
        var vmg = BuildVideoManager(1, (Chapters: 8, Angles: 1, TitleSet: 1, VtsTitle: 1));
        var src = DiscWith(vmg, (1, vts));

        var audio = IfoReader.Read(src).Titles[0].Audio;

        Assert.Equal(3, audio.Count);
        Assert.Equal("AC3", audio[0].Codec);
        Assert.Equal(6, audio[0].Channels);
        Assert.Equal("en", audio[0].Language);
        Assert.Equal("DTS", audio[1].Codec);
        Assert.Equal("fr", audio[1].Language);
        Assert.Equal("LPCM", audio[2].Codec);
        Assert.Equal("", audio[2].Language);
    }

    [Fact]
    public void Subtitle_streams_carry_their_language_when_declared()
    {
        var vts = BuildTitleSet(subtitles: new[] { "en", null, "de" });
        var vmg = BuildVideoManager(1, (Chapters: 8, Angles: 1, TitleSet: 1, VtsTitle: 1));
        var src = DiscWith(vmg, (1, vts));

        var subs = IfoReader.Read(src).Titles[0].Subtitles;

        Assert.Equal(3, subs.Count);
        Assert.Equal("en", subs[0].Language);
        Assert.Equal("", subs[1].Language);
        Assert.Equal("de", subs[2].Language);
        Assert.Equal(new[] { 0, 1, 2 }, subs.Select(s => s.Index).ToArray());
    }

    [Fact]
    public void An_absurd_audio_count_is_capped_at_the_format_maximum_of_eight()
    {
        var vts = BuildTitleSet();
        vts[0x203] = 200;   // corrupt count; attributes beyond are zeroed
        var vmg = BuildVideoManager(1, (Chapters: 8, Angles: 1, TitleSet: 1, VtsTitle: 1));
        var src = DiscWith(vmg, (1, vts));

        Assert.True(IfoReader.Read(src).Titles[0].Audio.Count <= 8);
    }

    [Fact]
    public void The_summary_totals_are_arithmetic_over_the_sets()
    {
        var vmg = BuildVideoManager(2,
            (Chapters: 12, Angles: 1, TitleSet: 1, VtsTitle: 1),
            (Chapters: 3, Angles: 1, TitleSet: 2, VtsTitle: 1));
        var src = DiscWith(vmg, (1, BuildTitleSet()), (2, BuildTitleSet()));
        src.Sizes["VTS_01_1.VOB"] = 4_000;
        src.Sizes["VTS_02_1.VOB"] = 6_000;
        src.Sizes["VTS_02_0.VOB"] = 500;

        var dvd = IfoReader.Read(src);

        Assert.Equal(10_000, dvd.TotalVideoBytes);
        Assert.Equal(500, dvd.TotalMenuBytes);
        Assert.Contains("2 title set(s)", dvd.Summary);
    }
}
