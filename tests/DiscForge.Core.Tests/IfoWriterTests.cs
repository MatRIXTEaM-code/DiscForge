// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.DvdVideo;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the DVD-Video IFO writer. As with UDF, XISO and NRG, the format has
/// no external oracle here, so the proof is a round trip: a plan is written to IFO
/// bytes and read back through <see cref="IfoReader"/>; a plan that survives with
/// its title-set count, titles (chapters, angles, VTS mapping) and audio/subpicture
/// streams intact shows the writer and reader agree on the structural layout —
/// including the big-endian fields that are the usual source of nonsense.
/// </summary>
public class IfoWriterTests
{
    /// <summary>Feed a written IFO set straight into the reader.</summary>
    private sealed class WrittenSource : IfoReader.IVideoTsSource
    {
        private readonly IReadOnlyDictionary<string, byte[]> _files;
        public WrittenSource(IReadOnlyDictionary<string, byte[]> files) => _files = files;
        public byte[]? ReadFile(string name) => _files.TryGetValue(name, out var b) ? b : null;
        public long FileSize(string name) => _files.TryGetValue(name, out var b) ? b.Length : 0;
    }

    private static IfoReader.DvdStructure WriteAndRead(IfoWriter.DvdPlan plan) =>
        IfoReader.Read(new WrittenSource(IfoWriter.Write(plan)));

    // ---- the round trip -----------------------------------------------------

    [Fact]
    public void A_written_disc_reads_back_as_a_valid_dvd()
    {
        var plan = new IfoWriter.DvdPlan
        {
            TitleSets = new[]
            {
                new IfoWriter.TitleSetPlan
                {
                    Number = 1,
                    Titles = new[] { new IfoWriter.TitlePlan { Chapters = 12, Angles = 1 } },
                },
            },
        };

        var dvd = WriteAndRead(plan);

        Assert.Single(dvd.TitleSets);
        var title = Assert.Single(dvd.Titles);
        Assert.Equal(1, title.TitleNumber);
        Assert.Equal(1, title.TitleSet);
        Assert.Equal(12, title.Chapters);
    }

    [Fact]
    public void Chapter_and_angle_counts_survive()
    {
        // 300 chapters (0x012C) is the big-endian canary — a byte-swap would read
        // it as 11265.
        var plan = new IfoWriter.DvdPlan
        {
            TitleSets = new[]
            {
                new IfoWriter.TitleSetPlan
                {
                    Number = 1,
                    Titles = new[]
                    {
                        new IfoWriter.TitlePlan { Chapters = 300, Angles = 3 },
                        new IfoWriter.TitlePlan { Chapters = 1, Angles = 1 },
                    },
                },
            },
        };

        var dvd = WriteAndRead(plan);

        Assert.Equal(300, dvd.Titles[0].Chapters);
        Assert.Equal(3, dvd.Titles[0].AngleCount);
        Assert.Equal(1, dvd.Titles[1].Chapters);
    }

    [Fact]
    public void Titles_across_two_sets_are_numbered_globally()
    {
        var plan = new IfoWriter.DvdPlan
        {
            TitleSets = new[]
            {
                new IfoWriter.TitleSetPlan
                {
                    Number = 1,
                    Titles = new[] { new IfoWriter.TitlePlan { Chapters = 8 } },
                },
                new IfoWriter.TitleSetPlan
                {
                    Number = 2,
                    Titles = new[]
                    {
                        new IfoWriter.TitlePlan { Chapters = 4 },
                        new IfoWriter.TitlePlan { Chapters = 6 },
                    },
                },
            },
        };

        var dvd = WriteAndRead(plan);

        Assert.Equal(new[] { 1, 2, 3 }, dvd.Titles.Select(t => t.TitleNumber).ToArray());
        Assert.Equal(new[] { 1, 2, 2 }, dvd.Titles.Select(t => t.TitleSet).ToArray());
        Assert.Equal(new[] { 1, 1, 2 }, dvd.Titles.Select(t => t.VtsTitle).ToArray());
    }

    [Fact]
    public void Audio_streams_round_trip_with_codec_channels_and_language()
    {
        var plan = new IfoWriter.DvdPlan
        {
            TitleSets = new[]
            {
                new IfoWriter.TitleSetPlan
                {
                    Number = 1,
                    Titles = new[] { new IfoWriter.TitlePlan { Chapters = 5 } },
                    Audio = new[]
                    {
                        new IfoWriter.AudioPlan { Codec = "AC3", Channels = 6, Language = "en" },
                        new IfoWriter.AudioPlan { Codec = "DTS", Channels = 2, Language = "fr" },
                        new IfoWriter.AudioPlan { Codec = "LPCM", Channels = 2, Language = "" },
                    },
                },
            },
        };

        var audio = WriteAndRead(plan).Titles[0].Audio;

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
    public void Subtitle_streams_round_trip_with_their_languages()
    {
        var plan = new IfoWriter.DvdPlan
        {
            TitleSets = new[]
            {
                new IfoWriter.TitleSetPlan
                {
                    Number = 1,
                    Titles = new[] { new IfoWriter.TitlePlan { Chapters = 5 } },
                    Subtitles = new[]
                    {
                        new IfoWriter.SubtitlePlan { Language = "en" },
                        new IfoWriter.SubtitlePlan { Language = "" },
                        new IfoWriter.SubtitlePlan { Language = "de" },
                    },
                },
            },
        };

        var subs = WriteAndRead(plan).Titles[0].Subtitles;

        Assert.Equal(3, subs.Count);
        Assert.Equal("en", subs[0].Language);
        Assert.Equal("", subs[1].Language);
        Assert.Equal("de", subs[2].Language);
    }

    // ---- determinism --------------------------------------------------------

    [Fact]
    public void The_same_plan_writes_byte_identical_ifos()
    {
        IfoWriter.DvdPlan Plan() => new()
        {
            TitleSets = new[]
            {
                new IfoWriter.TitleSetPlan
                {
                    Number = 1,
                    Titles = new[] { new IfoWriter.TitlePlan { Chapters = 9, Angles = 2 } },
                    Audio = new[] { new IfoWriter.AudioPlan { Codec = "AC3", Channels = 6, Language = "en" } },
                },
            },
        };

        var a = IfoWriter.Write(Plan());
        var b = IfoWriter.Write(Plan());
        Assert.Equal(a.Keys.OrderBy(k => k), b.Keys.OrderBy(k => k));
        foreach (var key in a.Keys)
            Assert.Equal(a[key], b[key]);
    }

    // ---- structural rewrite -------------------------------------------------

    [Fact]
    public void Read_write_read_is_stable_for_a_whole_disc()
    {
        // Author a disc, read it, re-emit it from the read structure, and read the
        // re-emission: the enumeration must be identical (a genuine rewrite loop).
        var original = new IfoWriter.DvdPlan
        {
            TitleSets = new[]
            {
                new IfoWriter.TitleSetPlan
                {
                    Number = 1,
                    Titles = new[] { new IfoWriter.TitlePlan { Chapters = 20, Angles = 1 } },
                    Audio = new[] { new IfoWriter.AudioPlan { Codec = "AC3", Channels = 6, Language = "en" } },
                    Subtitles = new[] { new IfoWriter.SubtitlePlan { Language = "en" } },
                },
                new IfoWriter.TitleSetPlan
                {
                    Number = 2,
                    Titles = new[] { new IfoWriter.TitlePlan { Chapters = 3, Angles = 1 } },
                    Audio = new[] { new IfoWriter.AudioPlan { Codec = "DTS", Channels = 2, Language = "ja" } },
                },
            },
        };

        var firstRead = WriteAndRead(original);
        var rewritten = WriteAndRead(IfoWriter.PlanFrom(firstRead));

        Assert.Equal(firstRead.TitleSets.Count, rewritten.TitleSets.Count);
        Assert.Equal(
            firstRead.Titles.Select(t => (t.TitleNumber, t.TitleSet, t.Chapters)).ToArray(),
            rewritten.Titles.Select(t => (t.TitleNumber, t.TitleSet, t.Chapters)).ToArray());
        Assert.Equal("AC3", rewritten.Titles[0].Audio[0].Codec);
        Assert.Equal("ja", rewritten.Titles[1].Audio[0].Language);
        Assert.Equal("en", rewritten.Titles[0].Subtitles[0].Language);
    }

    [Fact]
    public void Keeping_a_subset_renumbers_the_survivors()
    {
        var disc = new IfoWriter.DvdPlan
        {
            TitleSets = new[]
            {
                new IfoWriter.TitleSetPlan { Number = 1, Titles = new[] { new IfoWriter.TitlePlan { Chapters = 5 } } },
                new IfoWriter.TitleSetPlan { Number = 2, Titles = new[] { new IfoWriter.TitlePlan { Chapters = 8 } } },
                new IfoWriter.TitleSetPlan { Number = 3, Titles = new[] { new IfoWriter.TitlePlan { Chapters = 2 } } },
            },
        };
        var read = WriteAndRead(disc);

        // Keep VTS 1 and 3; they must re-emit as a contiguous VTS 1 and 2.
        var kept = WriteAndRead(IfoWriter.Keep(read, new[] { 1, 3 }));

        Assert.Equal(2, kept.TitleSets.Count);
        Assert.Equal(new[] { 1, 2 }, kept.TitleSets.Select(s => s.Number).ToArray());
        Assert.Equal(new[] { 5, 2 }, kept.Titles.Select(t => t.Chapters).ToArray());
    }

    // ---- guards -------------------------------------------------------------

    [Fact]
    public void An_empty_plan_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            IfoWriter.Write(new IfoWriter.DvdPlan { TitleSets = Array.Empty<IfoWriter.TitleSetPlan>() }));
    }

    [Fact]
    public void Duplicate_title_set_numbers_are_refused()
    {
        var plan = new IfoWriter.DvdPlan
        {
            TitleSets = new[]
            {
                new IfoWriter.TitleSetPlan { Number = 1, Titles = new[] { new IfoWriter.TitlePlan() } },
                new IfoWriter.TitleSetPlan { Number = 1, Titles = new[] { new IfoWriter.TitlePlan() } },
            },
        };
        Assert.Throws<ArgumentException>(() => IfoWriter.Write(plan));
    }
}
