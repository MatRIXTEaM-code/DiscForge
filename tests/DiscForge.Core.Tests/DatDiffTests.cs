using System.Linq;
using DiscForge.Core.Dat;
using Xunit;

namespace DiscForge.Core.Tests;

public class DatDiffTests
{
    private static DatFile Dat(params (string game, string name, string sha1)[] roms)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<datafile><header><name>T</name></header>");
        foreach (var g in roms.GroupBy(r => r.game))
        {
            sb.Append($"<game name=\"{System.Security.SecurityElement.Escape(g.Key)}\">");
            foreach (var r in g)
                sb.Append($"<rom name=\"{System.Security.SecurityElement.Escape(r.name)}\" size=\"1\" sha1=\"{r.sha1}\"/>");
            sb.Append("</game>");
        }
        sb.Append("</datafile>");
        return DatFile.ParseText(sb.ToString());
    }

    [Fact]
    public void Identical_dats_show_no_changes()
    {
        var a = Dat(("Sonic (USA)", "s.bin", "aaaa"));
        var b = Dat(("Sonic (USA)", "s.bin", "aaaa"));
        var d = DatDiff.Compare(a, b);
        Assert.True(d.Identical);
    }

    [Fact]
    public void Added_and_removed_games_are_reported()
    {
        var oldDat = Dat(("Sonic (USA)", "s.bin", "aaaa"), ("Old Game (USA)", "o.bin", "bbbb"));
        var newDat = Dat(("Sonic (USA)", "s.bin", "aaaa"), ("New Game (USA)", "n.bin", "cccc"));

        var d = DatDiff.Compare(oldDat, newDat);

        Assert.Equal(new[] { "New Game (USA)" }, d.Added);
        Assert.Equal(new[] { "Old Game (USA)" }, d.Removed);
        Assert.Empty(d.Changed);
        Assert.False(d.Identical);
    }

    [Fact]
    public void A_rehashed_rom_shows_as_changed()
    {
        var oldDat = Dat(("Doom (USA)", "d.bin", "1111"));
        var newDat = Dat(("Doom (USA)", "d.bin", "2222"));   // same name, new hash (redump)

        var d = DatDiff.Compare(oldDat, newDat);

        Assert.Empty(d.Added);
        Assert.Empty(d.Removed);
        Assert.Single(d.Changed);
        Assert.Equal("Doom (USA)", d.Changed[0].Game);
        Assert.Contains("re-hashed", d.Changed[0].Detail);
    }

    [Fact]
    public void A_changed_rom_count_is_described()
    {
        var oldDat = Dat(("Multi (USA)", "a.bin", "1111"));
        var newDat = Dat(("Multi (USA)", "a.bin", "1111"), ("Multi (USA)", "b.bin", "2222"));

        var d = DatDiff.Compare(oldDat, newDat);
        Assert.Single(d.Changed);
        Assert.Contains("→", d.Changed[0].Detail);
    }

    [Fact]
    public void Game_counts_are_carried()
    {
        var oldDat = Dat(("A (USA)", "a", "1"), ("B (USA)", "b", "2"));
        var newDat = Dat(("A (USA)", "a", "1"));
        var d = DatDiff.Compare(oldDat, newDat);
        Assert.Equal(2, d.OldGames);
        Assert.Equal(1, d.NewGames);
    }
}
