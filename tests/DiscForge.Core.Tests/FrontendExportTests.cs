using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using DiscForge.Core.Frontend;
using Xunit;

namespace DiscForge.Core.Tests;

public class FrontendExportTests
{
    // --- M3U ------------------------------------------------------------------

    [Fact]
    public void M3u_lists_each_disc_on_its_own_line_in_order()
    {
        var m3u = FrontendExport.BuildM3u(new[] { "Game (Disc 1).chd", "Game (Disc 2).chd" });
        Assert.Equal("Game (Disc 1).chd\nGame (Disc 2).chd\n", m3u);
    }

    [Fact]
    public void M3u_skips_blank_entries_and_trims()
    {
        var m3u = FrontendExport.BuildM3u(new[] { " a.cue ", "", "   ", "b.cue" });
        Assert.Equal("a.cue\nb.cue\n", m3u);
    }

    // --- RetroArch LPL --------------------------------------------------------

    [Fact]
    public void RetroArch_lpl_is_valid_json_with_the_expected_shape()
    {
        var items = new[]
        {
            new PlaylistItem { Path = @"C:\Games\Cool Game.chd", Label = "Cool Game", Crc32Hex = "1a2b3c4d" },
            new PlaylistItem { Path = @"C:\Games\Other.chd", Label = "Other" },
        };

        string json = FrontendExport.BuildRetroArchLpl("PlayStation", items);
        using var doc = JsonDocument.Parse(json);   // must parse
        var root = doc.RootElement;

        Assert.Equal("1.5", root.GetProperty("version").GetString());
        var arr = root.GetProperty("items");
        Assert.Equal(2, arr.GetArrayLength());

        var first = arr[0];
        Assert.Equal(@"C:\Games\Cool Game.chd", first.GetProperty("path").GetString());
        Assert.Equal("Cool Game", first.GetProperty("label").GetString());
        Assert.Equal("DETECT", first.GetProperty("core_path").GetString());
        Assert.Equal("DETECT", first.GetProperty("core_name").GetString());
        // CRC becomes uppercase + "|crc"; db_name gets the .lpl suffix.
        Assert.Equal("1A2B3C4D|crc", first.GetProperty("crc32").GetString());
        Assert.Equal("PlayStation.lpl", first.GetProperty("db_name").GetString());
    }

    [Fact]
    public void RetroArch_lpl_uses_a_zero_crc_placeholder_when_unknown()
    {
        var json = FrontendExport.BuildRetroArchLpl("X.lpl", new[]
        {
            new PlaylistItem { Path = "a.iso", Label = "A" },
        });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("00000000|crc", doc.RootElement.GetProperty("items")[0].GetProperty("crc32").GetString());
        // A name already ending in .lpl is not doubled.
        Assert.Equal("X.lpl", doc.RootElement.GetProperty("items")[0].GetProperty("db_name").GetString());
    }

    [Fact]
    public void RetroArch_lpl_keeps_special_characters_readable_not_escaped_unicode()
    {
        var json = FrontendExport.BuildRetroArchLpl("P", new[]
        {
            new PlaylistItem { Path = "Pokémon.chd", Label = "Pokémon & Friends" },
        });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Pokémon & Friends", doc.RootElement.GetProperty("items")[0].GetProperty("label").GetString());
    }

    // --- EmulationStation gamelist -------------------------------------------

    [Fact]
    public void Gamelist_is_valid_xml_with_a_game_per_item()
    {
        var items = new[]
        {
            new PlaylistItem { Path = "Cool Game.chd", Label = "Cool Game", System = "Sony - PlayStation" },
            new PlaylistItem { Path = @"sub\Other.chd", Label = "Other & Co" },
        };

        string xml = FrontendExport.BuildEmulationStationGamelist(items);
        var doc = XDocument.Parse(xml);   // must parse; also proves & is escaped

        Assert.Equal("gameList", doc.Root!.Name.LocalName);
        var games = doc.Root.Elements("game").ToList();
        Assert.Equal(2, games.Count);

        // A bare filename becomes "./name"; a path with a directory is kept as-is.
        Assert.Equal("./Cool Game.chd", games[0].Element("path")!.Value);
        Assert.Equal("Cool Game", games[0].Element("name")!.Value);
        Assert.Equal("Sony - PlayStation", games[0].Element("desc")!.Value);

        Assert.Equal(@"sub\Other.chd", games[1].Element("path")!.Value);
        Assert.Equal("Other & Co", games[1].Element("name")!.Value);
        Assert.Null(games[1].Element("desc"));   // no system → no desc
    }

    [Fact]
    public void Empty_input_still_produces_a_well_formed_gamelist()
    {
        var xml = FrontendExport.BuildEmulationStationGamelist(System.Array.Empty<PlaylistItem>());
        var doc = XDocument.Parse(xml);
        Assert.Empty(doc.Root!.Elements("game"));
    }
}
