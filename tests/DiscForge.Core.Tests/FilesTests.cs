// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Files;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Checksums against published vectors (so they interoperate with md5sum and
/// friends) and split/join proven by round-trip, plus the failure modes that
/// justify the manifest: a corrupt part and a missing part must be caught
/// and named, not discovered later as an unreadable burn.
/// </summary>
public class FilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    // ---- checksums ---------------------------------------------------------

    [Fact]
    public void Checksums_MatchPublishedVectors()
    {
        using var abc = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        var sums = ImageChecksums.Compute(abc);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", sums.Md5);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", sums.Sha1);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            sums.Sha256);
        Assert.Equal(3, sums.Length);

        using var nine = new MemoryStream(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal("cbf43926", ImageChecksums.Compute(nine).Crc32);
    }

    [Theory]
    [InlineData("sha256")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc32")]
    public void Sidecar_WriteThenFind_RoundTrips(string algorithm)
    {
        var file = Path.Combine(_dir, "img.bin");
        File.WriteAllBytes(file, Encoding.ASCII.GetBytes("abc"));
        var sums = ImageChecksums.ComputeFile(file);

        ImageChecksums.WriteSidecar(file, sums, algorithm);
        var found = ImageChecksums.FindSidecar(file);

        Assert.NotNull(found);
        Assert.Equal(algorithm, found!.Algorithm);
        Assert.Equal(ImageChecksums.ValueFor(sums, algorithm), found.ExpectedHex);
    }

    [Fact]
    public void Sidecar_PrefersStrongestWhenSeveralExist()
    {
        var file = Path.Combine(_dir, "img.bin");
        File.WriteAllBytes(file, Encoding.ASCII.GetBytes("abc"));
        var sums = ImageChecksums.ComputeFile(file);
        ImageChecksums.WriteSidecar(file, sums, "crc32");
        ImageChecksums.WriteSidecar(file, sums, "sha256");

        Assert.Equal("sha256", ImageChecksums.FindSidecar(file)!.Algorithm);
    }

    // ---- split / join ------------------------------------------------------

    private (string src, byte[] data) MakeImage(int bytes)
    {
        var data = new byte[bytes];
        new Random(8).NextBytes(data);
        var src = Path.Combine(_dir, "disc.img");
        File.WriteAllBytes(src, data);
        return (src, data);
    }

    [Fact]
    public void SplitJoin_RoundTripsByteIdentical()
    {
        var (src, data) = MakeImage(3 * 1024 * 1024 + 777);
        var split = ImageSplitter.Split(src, 1024 * 1024);

        Assert.Equal(4, split.Parts.Count);
        Assert.True(File.Exists(split.ManifestPath));
        Assert.Equal(data.LongLength, split.TotalBytes);

        var joined = Path.Combine(_dir, "joined.img");
        var result = ImageSplitter.Join(split.Parts[0], joined);

        Assert.True(result.Verified);
        Assert.Equal(4, result.Parts);
        Assert.Equal(data, File.ReadAllBytes(joined));
    }

    [Fact]
    public void Join_CorruptPart_IsNamedInTheError()
    {
        var (src, _) = MakeImage(3 * 1024 * 1024);
        var split = ImageSplitter.Split(src, 1024 * 1024);

        var bytes = File.ReadAllBytes(split.Parts[1]);
        bytes[1000] ^= 0xFF;
        File.WriteAllBytes(split.Parts[1], bytes);

        var ex = Assert.Throws<InvalidDataException>(() =>
            ImageSplitter.Join(split.Parts[0], Path.Combine(_dir, "out.img")));
        Assert.Contains(".002", ex.Message);
    }

    [Fact]
    public void Join_MissingPart_IsDetectedByCount()
    {
        var (src, _) = MakeImage(3 * 1024 * 1024);
        var split = ImageSplitter.Split(src, 1024 * 1024);
        File.Delete(split.Parts[2]);

        var ex = Assert.Throws<InvalidDataException>(() =>
            ImageSplitter.Join(split.Parts[0], Path.Combine(_dir, "out.img")));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Join_WithoutManifest_WorksButSaysUnverified()
    {
        var (src, data) = MakeImage(2 * 1024 * 1024 + 5);
        var split = ImageSplitter.Split(src, 1024 * 1024);
        File.Delete(split.ManifestPath);

        var joined = Path.Combine(_dir, "joined.img");
        var result = ImageSplitter.Join(split.Parts[0], joined);

        Assert.False(result.Verified);
        Assert.NotNull(result.Warning);
        Assert.Equal(data, File.ReadAllBytes(joined));
    }

    [Fact]
    public void Split_RefusesWhenNothingToSplit()
    {
        var (src, _) = MakeImage(1024 * 1024);
        Assert.Throws<InvalidDataException>(() => ImageSplitter.Split(src, 2 * 1024 * 1024));
    }

    [Theory]
    [InlineData("fat32", ImageSplitter.Fat32MaxBytes)]
    [InlineData("700m", 700L * 1024 * 1024)]
    [InlineData("4g", 4L * 1024 * 1024 * 1024)]
    [InlineData("123456789", 123456789)]
    public void PartSizes_Parse(string text, long expected)
        => Assert.Equal(expected, ImageSplitter.ParsePartSize(text));

    [Fact]
    public void PartSizes_RejectNonsense()
        => Assert.Throws<ArgumentException>(() => ImageSplitter.ParsePartSize("a lot"));
}
