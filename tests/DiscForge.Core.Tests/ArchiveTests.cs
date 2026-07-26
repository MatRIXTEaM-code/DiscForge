using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using DiscForge.Core.Archive;
using Xunit;

namespace DiscForge.Core.Tests;

public class TorrentZipTests
{
    private static ZipEntry E(string name, string data) => new(name, Encoding.ASCII.GetBytes(data));

    [Fact]
    public void The_same_input_yields_byte_identical_output()
    {
        var a = TorrentZip.Create(new[] { E("b.bin", "hello"), E("a.bin", "world") });
        var b = TorrentZip.Create(new[] { E("b.bin", "hello"), E("a.bin", "world") });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Entries_are_sorted_case_insensitively_regardless_of_input_order()
    {
        var zip = TorrentZip.Create(new[] { E("Zebra.rom", "z"), E("apple.rom", "a"), E("Mango.rom", "m") });
        using var ms = new MemoryStream(zip);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.Equal(new[] { "apple.rom", "Mango.rom", "Zebra.rom" }, archive.Entries.Select(e => e.FullName).ToArray());
    }

    [Fact]
    public void Output_is_a_readable_zip_with_intact_contents()
    {
        var zip = TorrentZip.Create(new[] { E("readme.txt", "the quick brown fox jumps over the lazy dog") });
        using var ms = new MemoryStream(zip);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        using var r = new StreamReader(archive.GetEntry("readme.txt")!.Open());
        Assert.Equal("the quick brown fox jumps over the lazy dog", r.ReadToEnd());
    }

    [Fact]
    public void The_torrentzipped_comment_matches_the_central_directory_crc()
    {
        var zip = TorrentZip.Create(new[] { E("a.bin", "aaaa"), E("b.bin", "bbbb") });
        Assert.True(TorrentZip.IsTorrentZipStructured(zip));

        // A normal .NET zip is not TorrentZip-structured.
        using var ms = new MemoryStream();
        using (var ar = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(ar.CreateEntry("x").Open())) w.Write("x");
        Assert.False(TorrentZip.IsTorrentZipStructured(ms.ToArray()));
    }

    [Fact]
    public void An_empty_archive_is_still_valid_and_structured()
    {
        var zip = TorrentZip.Create(System.Array.Empty<ZipEntry>());
        Assert.True(TorrentZip.IsTorrentZipStructured(zip));
        using var ms = new MemoryStream(zip);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.Empty(archive.Entries);
    }
}

public class HashSidecarTests
{
    [Fact]
    public void Sfv_lines_are_name_then_uppercase_crc()
    {
        var text = HashSidecar.BuildSfv(new[] { new HashLine("Game (USA).bin", "abcd1234") });
        Assert.Contains("Game (USA).bin ABCD1234", text);
        Assert.StartsWith(";", text);   // comment header
    }

    [Fact]
    public void Md5_lines_are_lowercase_hash_star_name()
    {
        var text = HashSidecar.BuildHashList(new[] { new HashLine("a.iso", "D41D8CD98F00B204E9800998ECF8427E") });
        Assert.Contains("d41d8cd98f00b204e9800998ecf8427e *a.iso", text);
    }

    [Fact]
    public void Sfv_round_trips_including_names_with_spaces()
    {
        var lines = new[] { new HashLine("Cool Game (USA).bin", "AABBCCDD"), new HashLine("Other.bin", "11223344") };
        var parsed = HashSidecar.Parse(SidecarKind.Sfv, HashSidecar.BuildSfv(lines));

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Cool Game (USA).bin", parsed[0].Name);
        Assert.Equal("AABBCCDD", parsed[0].Hash);
    }

    [Fact]
    public void Md5_round_trips_and_strips_the_binary_asterisk()
    {
        var lines = new[] { new HashLine("a b.iso", "d41d8cd98f00b204e9800998ecf8427e") };
        var parsed = HashSidecar.Parse(SidecarKind.Md5, HashSidecar.BuildHashList(lines));
        Assert.Single(parsed);
        Assert.Equal("a b.iso", parsed[0].Name);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", parsed[0].Hash);
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored_on_parse()
    {
        string sfv = "; a comment\n\n# another\nfile.bin DEADBEEF\n";
        var parsed = HashSidecar.Parse(SidecarKind.Sfv, sfv);
        Assert.Single(parsed);
        Assert.Equal("file.bin", parsed[0].Name);
    }
}
