using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DiscForge.Core.Dat;
using DiscForge.Core.Library;
using Xunit;

namespace DiscForge.Core.Tests;

public class CollectionReportHtmlTests
{
    private static LibraryEntry Entry(string file, LibraryStatus status, string? game = null, string? suggested = null) => new()
    {
        Path = "/src/" + file, FileName = file, Size = 1, Format = "ISO 9660",
        Crc32 = 1, Md5 = "m", Sha1 = "s", Status = status,
        Match = game is null ? null : new DatRom { Game = game, Name = game + ".iso", Size = 1 },
        SuggestedName = suggested,
    };

    private static LibraryReport Report() => new()
    {
        Root = "/games",
        DatName = "No-Intro SNES",
        Entries = new[]
        {
            Entry("good.iso", LibraryStatus.Verified, "Great Game (USA)"),
            Entry("weird name.iso", LibraryStatus.Misnamed, "Tidy Name (USA)", "Tidy Name (USA).iso"),
            Entry("mystery.bin", LibraryStatus.Unknown),
        },
        Missing = new[] { new DatRom { Game = "Rare & Wanted (USA)", Name = "Rare & Wanted (USA).iso", Size = 1 } },
    };

    [Fact]
    public void The_report_is_well_formed_html_and_names_the_dat()
    {
        var html = CollectionReportHtml.Build(Report());
        // Parse the <body> as XML to prove the markup is well-formed and entities are escaped.
        int bodyStart = html.IndexOf("<body>");
        var body = html.Substring(bodyStart, html.IndexOf("</body>") + "</body>".Length - bodyStart);
        var doc = XDocument.Parse(body);
        Assert.Equal("body", doc.Root!.Name.LocalName);
        Assert.Contains("No-Intro SNES", html);
    }

    [Fact]
    public void Counts_and_game_names_appear()
    {
        var html = CollectionReportHtml.Build(Report());
        Assert.Contains("Great Game (USA)", html);
        Assert.Contains("Tidy Name (USA).iso", html);      // suggested rename shown
        Assert.Contains("Missing from set", html);
        Assert.Contains("Verified", html);
    }

    [Fact]
    public void Special_characters_in_names_are_escaped()
    {
        var html = CollectionReportHtml.Build(Report());
        Assert.Contains("Rare &amp; Wanted (USA).iso", html);
        Assert.DoesNotContain("Rare & Wanted (USA).iso", html);   // raw ampersand must not leak
    }

    [Fact]
    public void It_is_a_self_contained_document_with_inline_styles()
    {
        var html = CollectionReportHtml.Build(Report());
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<style>", html);
        Assert.DoesNotContain("<link", html);   // no external stylesheet
    }
}
