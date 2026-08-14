using DiscForge.Core.Cdi;
using DiscForge.Core.Create;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class IsoReaderTests
{
    [Fact]
    public void Mapped_user_data_stream_matches_the_extractor_byte_for_byte()
    {
        // CdiUserDataStream maps cooked user bytes on the fly so a DVD-sized
        // track can be browsed without extracting it. It must agree exactly with
        // CdiExtractor, which is the authority on sector cooking.
        var files = new[] { new IsoBuilder.FileEntry("DATA.BIN", Pattern(9000, 3)) };
        var cdi = new MemoryStream();
        CdiCreator.CreateDataImage("VOL", files, CdiVersion.V35, cdi);
        cdi.Position = 0;

        var image = CdiParser.Parse(cdi);
        var track = image.AllTracks.Single();

        using var extracted = new MemoryStream();
        CdiExtractor.ExtractUserData(cdi, track, extracted);

        using var mapped = new CdiUserDataStream(cdi, track);
        using var viaStream = new MemoryStream();
        mapped.CopyTo(viaStream);

        Assert.Equal(extracted.Length, mapped.Length);
        Assert.Equal(extracted.ToArray(), viaStream.ToArray());
    }

    [Fact]
    public void Files_can_be_browsed_straight_out_of_a_cdi()
    {
        var payload = Pattern(3000, 5);
        var files = new[] { new IsoBuilder.FileEntry("My File.txt", payload) };
        var cdi = new MemoryStream();
        CdiCreator.CreateDataImage("MYDISC", files, CdiVersion.V35, cdi);
        cdi.Position = 0;

        var image = CdiParser.Parse(cdi);
        var track = image.AllTracks.Single();

        using var iso = new CdiUserDataStream(cdi, track);
        var dir = IsoReader.Read(iso);

        var entry = Assert.Single(dir.Files);
        Assert.Equal("My File.txt", entry.Name);

        using var got = new MemoryStream();
        IsoReader.ExtractFile(iso, entry, got);
        Assert.Equal(payload, got.ToArray());
    }

    private static byte[] Pattern(int len, byte seed)
    {
        var d = new byte[len];
        for (int i = 0; i < len; i++) d[i] = (byte)((i * 13 + seed) & 0xFF);
        return d;
    }

    private static MemoryStream BuildImage(IReadOnlyList<IsoBuilder.Node> tree, string vol = "TESTVOL",
                                           bool joliet = true, bool rockRidge = false)
    {
        var layout = IsoBuilder.Plan(vol, tree, joliet, boot: null, rockRidge: rockRidge);
        var ms = new MemoryStream();
        layout.WriteTo(ms);
        ms.Position = 0;
        return ms;
    }

    // --- round trip: what we write, we must read back -------------------------

    [Fact]
    public void Reads_back_joliet_long_names_written_by_the_builder()
    {
        var readme = Pattern(1200, 1);
        var save = Pattern(400, 2);
        var tree = new[]
        {
            IsoBuilder.Node.File("Read Me First.txt", readme),
            IsoBuilder.Node.Dir("Saved Games", new[]
            {
                IsoBuilder.Node.File("Level 3 - Boss.sav", save),
                IsoBuilder.Node.Dir("Backups", new[]
                {
                    IsoBuilder.Node.File("auto-2026.sav", Pattern(64, 3)),
                }),
            }),
        };

        using var iso = BuildImage(tree, "My Game Disc");
        var dir = IsoReader.Read(iso);

        Assert.True(dir.Joliet);
        var paths = dir.Entries.Select(e => e.Path).OrderBy(p => p).ToArray();
        Assert.Contains("/Read Me First.txt", paths);
        Assert.Contains("/Saved Games", paths);
        Assert.Contains("/Saved Games/Level 3 - Boss.sav", paths);
        Assert.Contains("/Saved Games/Backups/auto-2026.sav", paths);
    }

    [Fact]
    public void Extracted_file_bytes_match_what_was_written()
    {
        var payload = Pattern(5000, 7);
        var tree = new[] { IsoBuilder.Node.File("My Data.bin", payload) };

        using var iso = BuildImage(tree);
        var dir = IsoReader.Read(iso);
        var entry = dir.Files.Single(f => f.Name == "My Data.bin");

        using var extracted = new MemoryStream();
        IsoReader.ExtractFile(iso, entry, extracted);

        Assert.Equal((uint)payload.Length, entry.Size);
        Assert.Equal(payload, extracted.ToArray());
    }

    [Fact]
    public void Reads_rock_ridge_posix_names()
    {
        var tree = new[]
        {
            IsoBuilder.Node.File("my-archive.tar.gz", Pattern(300, 4)),
            IsoBuilder.Node.Dir("src-files", new[] { IsoBuilder.Node.File("main.c", Pattern(100, 5)) }),
        };

        using var iso = BuildImage(tree, "RRDISC", joliet: false, rockRidge: true);
        var dir = IsoReader.Read(iso);

        Assert.True(dir.RockRidge);
        var paths = dir.Entries.Select(e => e.Path).ToArray();
        Assert.Contains("/my-archive.tar.gz", paths);
        Assert.Contains("/src-files/main.c", paths);
    }

    [Fact]
    public void Iso9660_view_shows_the_8_3_fallback_names()
    {
        var tree = new[] { IsoBuilder.Node.File("My Long Name.txt", Pattern(64, 6)) };

        using var iso = BuildImage(tree);
        var dir = IsoReader.Read(iso, IsoReader.NamePreference.Iso9660);

        Assert.False(dir.Joliet);
        // The ";1" version suffix must be stripped for display.
        var f = Assert.Single(dir.Files);
        Assert.DoesNotContain(";", f.Name);
        Assert.Equal("MY_LONG_.TXT", f.Name);
    }

    [Fact]
    public void Volume_id_is_read_from_the_active_hierarchy()
    {
        var tree = new[] { IsoBuilder.Node.File("a.bin", Pattern(16, 8)) };

        using var iso = BuildImage(tree, "My Game Disc");
        Assert.Equal("My Game Disc", IsoReader.Read(iso).VolumeId);          // Joliet, mixed case
        Assert.Equal("MY GAME DISC", IsoReader.Read(iso, IsoReader.NamePreference.Iso9660).VolumeId);
    }

    [Fact]
    public void Empty_file_is_listed_with_zero_size()
    {
        var tree = new[] { IsoBuilder.Node.File("empty.dat", Array.Empty<byte>()) };

        using var iso = BuildImage(tree);
        var dir = IsoReader.Read(iso);

        var f = Assert.Single(dir.Files);
        Assert.Equal(0u, f.Size);
    }

    [Fact]
    public void Directory_with_many_entries_crosses_sector_boundaries_correctly()
    {
        // A zero-length record means "skip to the next sector", not "stop".
        // With enough entries the directory spans sectors and a naive reader
        // silently loses everything past the first boundary.
        var files = Enumerable.Range(0, 120)
            .Select(i => IsoBuilder.Node.File($"file-number-{i:D3}.dat", Pattern(8, (byte)i)))
            .ToArray();

        using var iso = BuildImage(files);
        var dir = IsoReader.Read(iso);

        Assert.Equal(120, dir.Files.Count());
        Assert.Contains(dir.Files, f => f.Name == "file-number-119.dat");
    }

    [Fact]
    public void Sector_range_is_reported_for_each_file()
    {
        // Lets a caller work out whether a bad sector actually hit a file.
        var tree = new[] { IsoBuilder.Node.File("big.bin", Pattern(10_000, 9)) };

        using var iso = BuildImage(tree);
        var f = IsoReader.Read(iso).Files.Single();

        Assert.Equal(5u, f.SectorCount);                 // 10000 bytes -> 5 sectors
        Assert.Equal(f.Extent + 4, f.LastSector);
    }

    [Fact]
    public void Total_bytes_sums_only_files()
    {
        var tree = new[]
        {
            IsoBuilder.Node.File("a.bin", Pattern(1000, 1)),
            IsoBuilder.Node.Dir("d", new[] { IsoBuilder.Node.File("b.bin", Pattern(2000, 2)) }),
        };

        using var iso = BuildImage(tree);
        Assert.Equal(3000, IsoReader.Read(iso).TotalBytes);
    }

    // --- rejection ------------------------------------------------------------

    [Fact]
    public void Non_iso_data_is_rejected_clearly()
    {
        using var junk = new MemoryStream(new byte[64 * 1024]);
        var ex = Assert.Throws<IsoFormatException>(() => IsoReader.Read(junk));
        Assert.Contains("ISO 9660", ex.Message);
    }

    [Fact]
    public void Requesting_joliet_from_an_image_without_it_is_refused()
    {
        var tree = new[] { IsoBuilder.Node.File("a.bin", Pattern(16, 1)) };
        using var iso = BuildImage(tree, joliet: false);

        Assert.Throws<IsoFormatException>(() => IsoReader.Read(iso, IsoReader.NamePreference.Joliet));
    }

    [Fact]
    public void Extracting_a_directory_is_refused()
    {
        var tree = new[] { IsoBuilder.Node.Dir("d", new[] { IsoBuilder.Node.File("a.bin", Pattern(16, 1)) }) };
        using var iso = BuildImage(tree);
        var dir = IsoReader.Read(iso).Directories.First();

        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => IsoReader.ExtractFile(iso, dir, output));
    }
}
