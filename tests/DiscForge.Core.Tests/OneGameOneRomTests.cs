using System.Linq;
using DiscForge.Core.Dat;
using Xunit;

namespace DiscForge.Core.Tests;

public class GameNameTests
{
    [Fact]
    public void Region_and_title_are_split_out()
    {
        var g = GameName.Parse("Chrono Trigger (USA)");
        Assert.Equal("Chrono Trigger", g.Title);
        Assert.Equal(new[] { "USA" }, g.Regions);
        Assert.False(g.IsPrerelease);
    }

    [Fact]
    public void Multiple_regions_and_languages_are_parsed()
    {
        var g = GameName.Parse("Metal Slug (Europe) (En,Fr,De)");
        Assert.Equal("Metal Slug", g.Title);
        Assert.Equal(new[] { "Europe" }, g.Regions);
        Assert.Equal(new[] { "EN", "FR", "DE" }, g.Languages);
    }

    [Fact]
    public void A_comma_region_list_expands()
    {
        var g = GameName.Parse("Some Game (USA, Europe)");
        Assert.Contains("USA", g.Regions);
        Assert.Contains("Europe", g.Regions);
    }

    [Fact]
    public void Revision_and_prerelease_flags_are_read()
    {
        Assert.Equal(1, GameName.Parse("Doom (USA) (Rev 1)").Revision);
        Assert.Equal(2, GameName.Parse("Game (USA) (Rev B)").Revision);   // B → 2
        Assert.True(GameName.Parse("Game (USA) (Beta)").IsPrerelease);
        Assert.True(GameName.Parse("Game (Japan) (Proto)").IsPrerelease);
    }

    [Fact]
    public void Disc_number_is_extracted()
    {
        Assert.Equal(2, GameName.Parse("Final Fantasy VII (USA) (Disc 2)").Disc);
        Assert.Equal(0, GameName.Parse("Single Disc Game (USA)").Disc);
    }
}

public class OneGameOneRomTests
{
    private static DatFile Dat(params (string game, string crc)[] entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<datafile><header><name>Test</name></header>");
        foreach (var (game, crc) in entries)
            sb.Append($"<game name=\"{System.Security.SecurityElement.Escape(game)}\">" +
                      $"<rom name=\"{System.Security.SecurityElement.Escape(game)}.bin\" size=\"1\" crc=\"{crc}\"/></game>");
        sb.Append("</datafile>");
        return DatFile.ParseText(sb.ToString());
    }

    [Fact]
    public void One_region_is_kept_per_game_by_priority()
    {
        var dat = Dat(
            ("Sonic (USA)", "11111111"),
            ("Sonic (Europe)", "22222222"),
            ("Sonic (Japan)", "33333333"));

        var r = OneGameOneRom.Build(dat);

        Assert.Equal(1, r.Families);
        Assert.Equal("Sonic", r.Choices[0].Family);
        Assert.Equal("Sonic (USA)", r.Choices[0].Chosen.Game);
        Assert.Equal(2, r.Choices[0].Rejected.Count);
    }

    [Fact]
    public void Region_priority_is_configurable()
    {
        var dat = Dat(("Sonic (USA)", "1"), ("Sonic (Japan)", "2"));
        var opts = new OneGameOneRomOptions { RegionPriority = new[] { "Japan", "USA" } };

        var r = OneGameOneRom.Build(dat, opts);
        Assert.Equal("Sonic (Japan)", r.Choices[0].Chosen.Game);
    }

    [Fact]
    public void Prerelease_is_dropped_when_a_final_exists()
    {
        var dat = Dat(("Halo (USA)", "1"), ("Halo (USA) (Beta)", "2"));
        var r = OneGameOneRom.Build(dat);
        Assert.Equal("Halo (USA)", r.Choices[0].Chosen.Game);
    }

    [Fact]
    public void The_only_copy_is_kept_even_if_it_is_a_prototype()
    {
        var dat = Dat(("Lost Game (USA) (Proto)", "1"));
        var r = OneGameOneRom.Build(dat);
        Assert.Equal(1, r.Families);
        Assert.Equal("Lost Game (USA) (Proto)", r.Choices[0].Chosen.Game);
    }

    [Fact]
    public void Higher_revision_wins_within_the_same_region()
    {
        var dat = Dat(("Doom (USA)", "1"), ("Doom (USA) (Rev 1)", "2"), ("Doom (USA) (Rev 2)", "3"));
        var r = OneGameOneRom.Build(dat);
        Assert.Equal("Doom (USA) (Rev 2)", r.Choices[0].Chosen.Game);
    }

    [Fact]
    public void Each_disc_of_a_multi_disc_game_is_kept()
    {
        var dat = Dat(
            ("FF7 (USA) (Disc 1)", "1"), ("FF7 (Europe) (Disc 1)", "2"),
            ("FF7 (USA) (Disc 2)", "3"), ("FF7 (Europe) (Disc 2)", "4"),
            ("FF7 (USA) (Disc 3)", "5"), ("FF7 (Europe) (Disc 3)", "6"));

        var r = OneGameOneRom.Build(dat);

        Assert.Equal(3, r.Families);                         // one family per disc
        Assert.All(r.Choices, c => Assert.Contains("(USA)", c.Chosen.Game));
        Assert.Equal(new[] { "FF7 (Disc 1)", "FF7 (Disc 2)", "FF7 (Disc 3)" },
                     r.Choices.Select(c => c.Family).ToArray());
    }

    [Fact]
    public void Distinct_games_stay_separate()
    {
        var dat = Dat(("Sonic (USA)", "1"), ("Mario (USA)", "2"));
        var r = OneGameOneRom.Build(dat);
        Assert.Equal(2, r.Families);
        Assert.Equal(2, r.TotalGames);
    }

    [Fact]
    public void Clones_fold_into_their_parent_via_cloneof()
    {
        // Two differently-titled entries linked by cloneof still form one family.
        string xml =
            "<datafile><header><name>t</name></header>" +
            "<game name=\"Wonder Boy (USA)\"><rom name=\"a.bin\" size=\"1\" crc=\"1\"/></game>" +
            "<game name=\"Adventure Island (Japan)\" cloneof=\"Wonder Boy (USA)\"><rom name=\"b.bin\" size=\"1\" crc=\"2\"/></game>" +
            "</datafile>";
        var dat = DatFile.ParseText(xml);
        var r = OneGameOneRom.Build(dat);

        Assert.Equal(1, r.Families);
        Assert.Equal("Wonder Boy (USA)", r.Choices[0].Chosen.Game);   // USA beats Japan
    }
}
