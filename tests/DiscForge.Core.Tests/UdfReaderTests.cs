using DiscForge.Core.Udf;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Driven by a real UDF volume produced by genisoimage (tests/fixtures/udf).
/// There is no isoinfo-equivalent for UDF, so the oracle is a committed volume
/// with known contents.
/// </summary>
public class UdfReaderTests
{
    private static string FixturePath()
    {
        // Walk up to the repo root, as the CDI fixtures do.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "udf", "udf_test.iso");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "udf_test.iso fixture not found — it should be committed under tests/fixtures/udf.");
    }

    private static FileStream OpenFixture() => File.OpenRead(FixturePath());

    private static byte[] ExpectedData()
    {
        var d = new byte[5000];
        for (int i = 0; i < d.Length; i++) d[i] = (byte)((i * 13 + 7) & 0xFF);
        return d;
    }

    // --- recognition ----------------------------------------------------------

    [Fact]
    public void Recognises_a_real_udf_volume()
    {
        using var iso = OpenFixture();
        Assert.True(UdfReader.IsUdf(iso));
    }

    [Fact]
    public void Does_not_claim_udf_on_empty_data()
    {
        using var junk = new MemoryStream(new byte[600 * 2048]);
        Assert.False(UdfReader.IsUdf(junk));
    }

    [Fact]
    public void Reading_non_udf_data_is_refused_with_a_useful_message()
    {
        using var junk = new MemoryStream(new byte[600 * 2048]);
        var ex = Assert.Throws<UdfFormatException>(() => UdfReader.Read(junk));
        Assert.Contains("Anchor Volume Descriptor Pointer", ex.Message);
        Assert.Contains("IsoReader", ex.Message);   // point at the right tool
    }

    // --- structure ------------------------------------------------------------

    [Fact]
    public void Finds_the_partition_and_root()
    {
        using var iso = OpenFixture();
        var volume = UdfReader.Read(iso);

        // Confirmed against the real image: partition at 257, root ICB block 2.
        Assert.Equal(257u, volume.PartitionStart);
        Assert.Equal(2u, volume.RootBlock);
    }

    [Fact]
    public void Reads_the_volume_label()
    {
        using var iso = OpenFixture();
        Assert.Equal("UDFTEST", UdfReader.Read(iso).VolumeId);
    }

    // --- listing --------------------------------------------------------------

    [Fact]
    public void Lists_the_whole_tree_including_nested_directories()
    {
        using var iso = OpenFixture();
        var volume = UdfReader.Read(iso);

        var paths = volume.Entries.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[]
        {
            "/data.bin",
            "/deep",
            "/deep/deeper",
            "/deep/deeper/tiny.txt",
            "/deep/inner.txt",
            "/readme.txt",
        }, paths);
    }

    [Fact]
    public void Distinguishes_files_from_directories()
    {
        using var iso = OpenFixture();
        var volume = UdfReader.Read(iso);

        Assert.Equal(4, volume.Files.Count());
        Assert.Equal(2, volume.Directories.Count());
        Assert.Contains(volume.Directories, d => d.Path == "/deep");
        Assert.Contains(volume.Directories, d => d.Path == "/deep/deeper");
    }

    [Fact]
    public void Reports_file_sizes()
    {
        using var iso = OpenFixture();
        var volume = UdfReader.Read(iso);
        var byPath = volume.Entries.ToDictionary(e => e.Path);

        Assert.Equal(16, byPath["/readme.txt"].Size);
        Assert.Equal(5000, byPath["/data.bin"].Size);
        Assert.Equal(21, byPath["/deep/inner.txt"].Size);
        Assert.Equal(1, byPath["/deep/deeper/tiny.txt"].Size);
        Assert.Equal(0, byPath["/deep"].Size);          // directories report 0
    }

    [Fact]
    public void Total_bytes_counts_files_only()
    {
        using var iso = OpenFixture();
        Assert.Equal(16 + 5000 + 21 + 1, UdfReader.Read(iso).TotalBytes);
    }

    [Fact]
    public void Parent_entries_are_not_listed()
    {
        // Every UDF directory contains a parent FID; it must not appear as a file.
        using var iso = OpenFixture();
        var volume = UdfReader.Read(iso);
        Assert.DoesNotContain(volume.Entries, e => e.Name is "" or "." or "..");
    }

    // --- extraction -----------------------------------------------------------

    [Fact]
    public void Extracts_a_text_file_exactly()
    {
        using var iso = OpenFixture();
        var volume = UdfReader.Read(iso);
        var entry = volume.Files.Single(f => f.Path == "/readme.txt");

        using var output = new MemoryStream();
        UdfReader.ExtractFile(iso, volume, entry, output);

        Assert.Equal("hello udf world\n", System.Text.Encoding.ASCII.GetString(output.ToArray()));
    }

    [Fact]
    public void Extracts_a_multi_sector_binary_exactly()
    {
        // 5000 bytes spans three sectors — proves extent handling and that the
        // read stops at informationLength rather than the extent's rounded size.
        using var iso = OpenFixture();
        var volume = UdfReader.Read(iso);
        var entry = volume.Files.Single(f => f.Path == "/data.bin");

        using var output = new MemoryStream();
        UdfReader.ExtractFile(iso, volume, entry, output);

        Assert.Equal(ExpectedData(), output.ToArray());
    }

    [Fact]
    public void Extracts_a_deeply_nested_file()
    {
        using var iso = OpenFixture();
        var volume = UdfReader.Read(iso);
        var entry = volume.Files.Single(f => f.Path == "/deep/inner.txt");

        using var output = new MemoryStream();
        UdfReader.ExtractFile(iso, volume, entry, output);

        Assert.Equal("nested file contents\n", System.Text.Encoding.ASCII.GetString(output.ToArray()));
    }

    [Fact]
    public void Extracts_a_one_byte_file()
    {
        using var iso = OpenFixture();
        var volume = UdfReader.Read(iso);
        var entry = volume.Files.Single(f => f.Path == "/deep/deeper/tiny.txt");

        using var output = new MemoryStream();
        UdfReader.ExtractFile(iso, volume, entry, output);

        Assert.Equal(new byte[] { (byte)'x' }, output.ToArray());
    }

    [Fact]
    public void Extracting_a_directory_is_refused()
    {
        using var iso = OpenFixture();
        var volume = UdfReader.Read(iso);
        var dir = volume.Directories.First();

        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => UdfReader.ExtractFile(iso, volume, dir, output));
    }
}
