using DiscForge.Core.Hfs;
using Xunit;

namespace DiscForge.Core.Tests;

public class HfsReaderTests
{
    private static string FindFixtures()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return AppContext.BaseDirectory;
    }

    private static byte[]? LoadFixture()
    {
        var path = Path.Combine(FindFixtures(), "hfs", "classic.hfs");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    [Fact]
    public void Reads_the_volume_name_and_the_whole_tree()
    {
        var image = LoadFixture();
        if (image is null) return;   // fixture optional in some checkouts

        var vol = HfsReader.Read(image);
        Assert.Equal("HFSTEST", vol.VolumeName);

        // README.TXT at root, DOCS folder, and two files inside it.
        Assert.Contains(vol.Directories, d => d.Path == "/DOCS");
        Assert.Contains(vol.Files, f => f.Path == "/README.TXT" && f.DataSize == 23);
        Assert.Contains(vol.Files, f => f.Path == "/DOCS/DATA.BIN" && f.DataSize == 4096);
        Assert.Contains(vol.Files, f => f.Path == "/DOCS/NOTES.TXT" && f.DataSize == 11);

        // The root folder is the volume itself, not a listed subdirectory.
        Assert.DoesNotContain(vol.Directories, d => d.Path == "/" + vol.VolumeName);
        Assert.Single(vol.Directories);   // only DOCS
    }

    [Fact]
    public void Nested_paths_are_reconstructed_from_the_catalog()
    {
        var image = LoadFixture();
        if (image is null) return;

        var vol = HfsReader.Read(image);
        var nested = vol.Files.First(f => f.Name == "DATA.BIN");
        Assert.Equal("/DOCS/DATA.BIN", nested.Path);
    }

    [Fact]
    public void Is_hfs_detects_the_signature()
    {
        var image = LoadFixture();
        if (image is null) return;
        Assert.True(HfsReader.IsHfs(image));
        Assert.False(HfsReader.IsHfs(new byte[4096]));
    }

    [Fact]
    public void A_non_hfs_image_is_rejected()
    {
        bool threw = false;
        try { HfsReader.Read(new byte[8192]); }
        catch (HfsFormatException) { threw = true; }
        Assert.True(threw);
    }
}
