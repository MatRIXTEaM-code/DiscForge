using DiscForge.Core.Cdi;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Trailer/locator tests. These cover the fully-implemented part of the parser.
/// Descriptor-walk tests arrive with the validation corpus (CDI_FORMAT.md §7):
/// we'll generate known images with cdi4dc and assert full structural parses.
/// </summary>
public class CdiTrailerTests
{
    [Fact]
    public void Truncated_image_says_so_rather_than_just_rejecting_it()
    {
        // A CDI's trailer is written last. A rip that dies part-way leaves an
        // all-zero magic — the commonest real-world cause of "not a CDI", and
        // worth naming so people don't hunt a format problem that isn't there.
        using var ms = new MemoryStream(new byte[4096]);
        var ex = Assert.Throws<CdiFormatException>(() => CdiParser.Parse(ms));
        Assert.Contains("truncated or incomplete", ex.Message);
    }

    private static MemoryStream FileWithTrailer(int totalLength, uint magic, uint locator)
    {
        var bytes = new byte[totalLength];
        BitConverter.GetBytes(magic).CopyTo(bytes, totalLength - 8);
        BitConverter.GetBytes(locator).CopyTo(bytes, totalLength - 4);
        return new MemoryStream(bytes);
    }

    [Theory]
    [InlineData(0x80000004u, CdiVersion.V2)]
    [InlineData(0x80000005u, CdiVersion.V3)]
    [InlineData(0x80000006u, CdiVersion.V35)]
    public void Recognises_version_magic(uint magic, CdiVersion expected)
    {
        using var s = FileWithTrailer(1000, magic, locator: 100);
        var trailer = CdiParser.ReadTrailer(s);
        Assert.Equal(expected, trailer.Version);
    }

    [Fact]
    public void V3_locator_is_absolute_offset()
    {
        using var s = FileWithTrailer(1000, 0x80000005u, locator: 640);
        var trailer = CdiParser.ReadTrailer(s);
        Assert.Equal(640, trailer.DescriptorOffset);
    }

    [Fact]
    public void V35_locator_is_length_from_eof()
    {
        // The classic v3.5 gotcha: locator 360 on a 1000-byte file means the
        // descriptor starts at 640, NOT at 360.
        using var s = FileWithTrailer(1000, 0x80000006u, locator: 360);
        var trailer = CdiParser.ReadTrailer(s);
        Assert.Equal(1000 - 360, trailer.DescriptorOffset);
    }

    [Fact]
    public void Rejects_unknown_magic()
    {
        using var s = FileWithTrailer(1000, 0xDEADBEEFu, locator: 100);
        var ex = Assert.Throws<CdiFormatException>(() => CdiParser.ReadTrailer(s));
        Assert.Contains("Not a CDI image", ex.Message);
    }

    [Fact]
    public void Rejects_locator_pointing_outside_file()
    {
        // Truncated-download simulation: descriptor offset beyond EOF.
        using var s = FileWithTrailer(1000, 0x80000005u, locator: 5000);
        Assert.Throws<CdiFormatException>(() => CdiParser.ReadTrailer(s));
    }

    [Fact]
    public void Rejects_tiny_files()
    {
        using var s = new MemoryStream(new byte[4]);
        Assert.Throws<CdiFormatException>(() => CdiParser.ReadTrailer(s));
    }
}
