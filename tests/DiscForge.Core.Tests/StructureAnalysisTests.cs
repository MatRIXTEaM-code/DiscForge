// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.DvdVideo;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the DVD structure judgement — the code that decides whether a
/// title table describes a disc's contents or is trying to obscure them.
///
/// The judgement has three legs, and each is pinned separately: the capacity
/// argument (declared bytes exceeding what a DVD-9 can physically hold — pure
/// arithmetic, the strongest evidence), byte-identical duplicate title sets
/// (authoring for content does not produce matching sizes), and the older
/// many-identical-titles heuristic. Just as important is what must NOT
/// trigger: boxsets, compilations, and discs that are merely full.
/// </summary>
public class StructureAnalysisTests
{
    // --- builders ------------------------------------------------------------

    private static IfoReader.Title Title(int number, int set, int chapters) => new()
    {
        TitleNumber = number,
        TitleSet = set,
        VtsTitle = 1,
        Chapters = chapters,
        AngleCount = 1,
    };

    private static IfoReader.TitleSet Set(int number, long videoBytes, params IfoReader.Title[] titles) => new()
    {
        Number = number,
        MenuVobBytes = 0,
        TitleVobBytes = videoBytes,
        Titles = titles,
    };

    private static IfoReader.DvdStructure Disc(params IfoReader.TitleSet[] sets) => new()
    {
        TitleSets = sets,
        Titles = sets.SelectMany(s => s.Titles).ToList(),
    };

    /// <summary>An ordinary film disc: one feature, a couple of extras.</summary>
    private static IfoReader.DvdStructure OrdinaryDisc() => Disc(
        Set(1, 4_000_000_000, Title(1, 1, 24)),
        Set(2, 500_000_000, Title(2, 2, 2), Title(3, 2, 1)));

    // --- the ordinary and the merely unusual ---------------------------------

    [Fact]
    public void An_ordinary_disc_is_normal_and_names_the_feature()
    {
        var finding = StructureAnalysis.Judge(OrdinaryDisc());

        Assert.Equal(StructureVerdict.Normal, finding.Verdict);
        Assert.Contains("title 1", finding.Summary);
        Assert.Contains(finding.Evidence, e => e.Contains("main feature"));
    }

    [Fact]
    public void Short_titles_are_explained_as_menus_and_extras()
    {
        var finding = StructureAnalysis.Judge(OrdinaryDisc());

        Assert.Contains(finding.Evidence, e => e.Contains("one or two chapters"));
    }

    [Fact]
    public void A_disc_with_no_titles_is_flagged_but_not_called_obfuscated()
    {
        var finding = StructureAnalysis.Judge(Disc(Set(1, 1_000_000)));

        Assert.Equal(StructureVerdict.Unusual, finding.Verdict);
        Assert.Contains("no titles", finding.Summary);
    }

    [Fact]
    public void A_boxset_with_many_but_varied_titles_is_unusual_not_obfuscated()
    {
        // 32 titles with genuinely varying chapter counts and sizes — a series
        // boxset. Many, but nothing repeats enough to look like a template.
        var sets = Enumerable.Range(1, 8)
            .Select(v => Set(v, 900_000_000 + v * 1_000_003,
                Enumerable.Range(0, 4)
                    .Select(i => Title((v - 1) * 4 + i + 1, v, 3 + (v * 4 + i) % 9))
                    .ToArray()))
            .ToArray();

        var finding = StructureAnalysis.Judge(Disc(sets));

        Assert.Equal(StructureVerdict.Unusual, finding.Verdict);
        Assert.Contains("differ", finding.Summary);
    }

    // --- the capacity argument -----------------------------------------------

    [Fact]
    public void Declared_video_beyond_dvd9_capacity_is_obfuscation()
    {
        // Eleven sets of 7 GB each: 77 GB declared on a medium that holds 8.5.
        // Few titles, no repetition — the arithmetic alone must carry it.
        var sets = Enumerable.Range(1, 11)
            .Select(v => Set(v, 7_000_000_000 + v, Title(v, v, 5 + v)))
            .ToArray();

        var finding = StructureAnalysis.Judge(Disc(sets));

        Assert.Equal(StructureVerdict.Obfuscated, finding.Verdict);
        Assert.Contains("holds at most", finding.Summary);
        Assert.Contains(finding.Evidence, e => e.Contains("physically contain"));
    }

    [Fact]
    public void A_disc_that_is_merely_full_is_not_obfuscated()
    {
        // Right at the DVD-9 ceiling, but not over it. Full is not fake.
        var finding = StructureAnalysis.Judge(Disc(
            Set(1, StructureAnalysis.Dvd9Bytes, Title(1, 1, 28))));

        Assert.Equal(StructureVerdict.Normal, finding.Verdict);
    }

    [Fact]
    public void Menu_bytes_count_toward_the_declared_total()
    {
        var over = Disc(Set(1, StructureAnalysis.Dvd9Bytes, Title(1, 1, 20)));
        over = over with
        {
            TitleSets = new[]
            {
                new IfoReader.TitleSet
                {
                    Number = 1,
                    MenuVobBytes = 1,   // one byte over the line, via the menus
                    TitleVobBytes = StructureAnalysis.Dvd9Bytes,
                    Titles = new[] { Title(1, 1, 20) },
                },
            },
        };

        Assert.Equal(StructureVerdict.Obfuscated, StructureAnalysis.Judge(over).Verdict);
    }

    // --- duplicate title sets ------------------------------------------------

    [Fact]
    public void Three_byte_identical_title_sets_are_obfuscation()
    {
        var finding = StructureAnalysis.Judge(Disc(
            Set(1, 2_000_000_000, Title(1, 1, 18)),
            Set(2, 2_000_000_000, Title(2, 2, 18)),
            Set(3, 2_000_000_000, Title(3, 3, 18)),
            Set(4, 300_000_000, Title(4, 4, 2))));

        Assert.Equal(StructureVerdict.Obfuscated, finding.Verdict);
        Assert.Contains("identical sizes", finding.Summary);
        Assert.Contains(finding.Evidence, e => e.Contains("identical to the byte"));
    }

    [Fact]
    public void Two_matching_sets_are_coincidence_not_obfuscation()
    {
        // Two sets the same size happens (dual-format releases, silly authoring).
        // The signature needs three.
        var finding = StructureAnalysis.Judge(Disc(
            Set(1, 2_000_000_000, Title(1, 1, 18)),
            Set(2, 2_000_000_000, Title(2, 2, 4)),
            Set(3, 700_000_000, Title(3, 3, 2))));

        Assert.Equal(StructureVerdict.Normal, finding.Verdict);
    }

    [Fact]
    public void Empty_title_sets_do_not_count_as_duplicates_of_each_other()
    {
        // Several sets with zero video are common on ordinary discs (menu-only
        // sets); zero must not group as "identical sizes".
        var finding = StructureAnalysis.Judge(Disc(
            Set(1, 4_000_000_000, Title(1, 1, 24)),
            Set(2, 0, Title(2, 2, 1)),
            Set(3, 0, Title(3, 3, 1)),
            Set(4, 0, Title(4, 4, 1))));

        Assert.Equal(StructureVerdict.Normal, finding.Verdict);
    }

    // --- the repetition heuristic --------------------------------------------

    [Fact]
    public void A_wall_of_identical_titles_is_obfuscation()
    {
        // 40 titles, 30 of them with exactly 12 chapters — template authoring.
        // Sizes vary so only the repetition heuristic can catch it.
        var titles = Enumerable.Range(1, 30).Select(i => Title(i, 1, 12))
            .Concat(Enumerable.Range(31, 10).Select(i => Title(i, 2, i % 7 + 1)))
            .ToArray();
        var finding = StructureAnalysis.Judge(Disc(
            Set(1, 3_000_000_000, titles.Take(30).ToArray()),
            Set(2, 1_000_000_007, titles.Skip(30).ToArray())));

        Assert.Equal(StructureVerdict.Obfuscated, finding.Verdict);
        Assert.Contains(finding.Evidence, e => e.Contains("12 chapters"));
    }

    [Fact]
    public void Obfuscation_evidence_mentions_the_99_title_limit_when_at_it()
    {
        var titles = Enumerable.Range(1, 99).Select(i => Title(i, 1, 15)).ToArray();
        var finding = StructureAnalysis.Judge(Disc(Set(1, 3_000_000_000, titles)));

        Assert.Equal(StructureVerdict.Obfuscated, finding.Verdict);
        Assert.Contains(finding.Evidence, e => e.Contains("maximum of 99"));
    }

    [Fact]
    public void Obfuscation_evidence_counts_title_sets_with_no_video()
    {
        var titles = Enumerable.Range(1, 40).Select(i => Title(i, i % 4 + 1, 10)).ToArray();
        var finding = StructureAnalysis.Judge(Disc(
            Set(1, 3_000_000_000, titles.Where(t => t.TitleSet == 1).ToArray()),
            Set(2, 0, titles.Where(t => t.TitleSet == 2).ToArray()),
            Set(3, 0, titles.Where(t => t.TitleSet == 3).ToArray()),
            Set(4, 0, titles.Where(t => t.TitleSet == 4).ToArray())));

        Assert.Equal(StructureVerdict.Obfuscated, finding.Verdict);
        Assert.Contains(finding.Evidence, e => e.Contains("no video file at all"));
    }

    [Fact]
    public void The_verdict_always_says_the_disc_is_not_damaged()
    {
        var titles = Enumerable.Range(1, 60).Select(i => Title(i, 1, 9)).ToArray();
        var finding = StructureAnalysis.Judge(Disc(Set(1, 3_000_000_000, titles)));

        Assert.Contains(finding.Evidence, e => e.Contains("not damaged"));
    }

    // --- Distinctive ---------------------------------------------------------

    [Fact]
    public void Distinctive_returns_everything_on_a_small_disc()
    {
        var picks = StructureAnalysis.Distinctive(OrdinaryDisc());

        Assert.Equal(3, picks.Count);
        Assert.Equal(new[] { 1, 2, 3 }, picks.Select(t => t.TitleNumber).ToArray());
    }

    [Fact]
    public void Distinctive_drops_titles_whose_set_holds_no_video()
    {
        var disc = Disc(
            Set(1, 4_000_000_000, Title(1, 1, 24)),
            Set(2, 0, Title(2, 2, 24)));

        var picks = StructureAnalysis.Distinctive(disc);

        Assert.Single(picks);
        Assert.Equal(1, picks[0].TitleNumber);
    }

    [Fact]
    public void Distinctive_keeps_one_representative_of_byte_identical_sets()
    {
        // Eleven sets declaring the same bytes: keep the first, drop the copies.
        var sets = Enumerable.Range(1, 11)
            .Select(v => Set(v, 7_000_000_000, Title(v, v, 20)))
            .Append(Set(12, 400_000_000, Title(12, 12, 2)))
            .ToArray();

        var picks = StructureAnalysis.Distinctive(Disc(sets));

        Assert.Equal(2, picks.Count);
        Assert.Equal(new[] { 1, 12 }, picks.Select(t => t.TitleNumber).ToArray());
    }

    [Fact]
    public void Distinctive_thins_a_chapter_count_crowd_to_one_representative()
    {
        // 30 titles with 12 chapters and a handful of distinct ones, all in
        // sets with video and no duplicate sizes: the crowd collapses to one.
        var crowd = Enumerable.Range(1, 30).Select(i => Title(i, 1, 12));
        var real = new[] { Title(31, 2, 24), Title(32, 3, 2) };
        var disc = Disc(
            Set(1, 3_000_000_000, crowd.ToArray()),
            Set(2, 2_000_000_001, real[0]),
            Set(3, 500_000_000, real[1]));

        var picks = StructureAnalysis.Distinctive(disc);

        Assert.Equal(3, picks.Count);
        Assert.Contains(picks, t => t.TitleNumber == 31);
        Assert.Contains(picks, t => t.TitleNumber == 32);
        Assert.Single(picks, t => t.Chapters == 12);
    }

    [Fact]
    public void Distinctive_returns_empty_for_an_empty_disc()
    {
        Assert.Empty(StructureAnalysis.Distinctive(Disc(Set(1, 0))));
    }
}
