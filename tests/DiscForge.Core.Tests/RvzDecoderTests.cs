// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;
using DiscForge.Core.GameCube;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// RVZ → GameCube ISO reconstruction, checked against hand-built zstd RVZ containers whose ISO is
/// known. Covers the whole container walk: header, raw-data + group tables (zstd-decompressed),
/// zstd group decompression, multi-group offset math, and the RVZ-packed unpack (data runs copied,
/// junk runs zero-filled). The single-group case is bit-exact; the packed case is data-exact with
/// junk zeroed (the documented behaviour until the Nintendo LFG lands).
/// </summary>
public class RvzDecoderTests
{
    // A 256 KiB GameCube ISO in one zstd group (not packed) → bit-exact.
    private const string SingleGroupRvz =
        "UlZaAQEAAAABAAAAAAAA3AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAUAAAAAAAQAAEdERkUwMQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAVEVTVCBHQU1FIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAEkAAAAFgAAAAEAAAAAAAABOgAAABUAAAAAAAAAACi1L/0gGG0AACgAAAQAAQIAgGADYAEotS/9IAxhAAAAAABUgAABOQAAAAAAKLUv/aAAAAQADAkAJBFHREZFMDEAVEVTVCBHQU1FIACAh46VnKOqsbi/xs3U2+Lp8Pf+BQwTGiEoLzY9REtSWWBnbnV8g4qRmJ+mrbS7wsnQ197l7PP6AQgPFh0kKzI5QEdOVVxjanF4f4aNlJuiqbC3vsXM09rh6O/2/QQLEhkgJy41PENKUVhfZm10e4KJkJeepayzusHIz9bd5Ovy+QAHDhUcIyoxOD9GTVRbYmlwd36FjJOaoaivtr3Ey9LZ4Ofu9fwDChEYHyYtNDtCSVBXXmVsc3qBiI+WnaSrsrnAx87V3OPq8fj/Bg0UGyIpMDc+RUxTWmFob3Z9hIuSmaCnrrW8w8rR2N/m7fT7AgkQFx4lLDM6QUhPVl1ka3J5AwAAff4DK58CJr8AI00AAAgAAQD8/zkQAg==";

    // A 1 MiB ISO in four zstd groups; group 2 is RVZ-packed (half data, half junk) → junk zeroed.
    private const string PackedRvz =
        "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";

    [Fact]
    public void ReconstructsSingleZstdGroup_BitExact()
    {
        var (report, hash) = Decode(SingleGroupRvz);
        Assert.Equal(262144, report.IsoBytes);
        Assert.Equal(1, report.Groups);
        Assert.Equal(0, report.JunkBytesZeroFilled);
        Assert.True(report.BitExact);
        Assert.Equal("1ce67611d1072555499ce3b20613facd4bd5677207ef6189368b65f5ff1d438d", hash);
    }

    [Fact]
    public void ReconstructsMultiGroupWithPacked_JunkZeroed()
    {
        var (report, hash) = Decode(PackedRvz);
        Assert.Equal(1048576, report.IsoBytes);
        Assert.Equal(4, report.Groups);
        Assert.Equal(131072, report.JunkBytesZeroFilled);   // the 128 KiB junk half of group 2
        Assert.True(report.DataExact);
        Assert.False(report.BitExact);
        Assert.Equal("1db1855731269c112b68c3149036884993675ba3ac5596bb88c0db662968936f", hash);
    }

    [Fact]
    public void Unencrypted_prefix_matches_the_full_decode()
    {
        // The prefix decoder (used to read Wii structure without touching encrypted data) must
        // reconstruct exactly the same bytes as a full decode over the range it covers.
        var rvz = System.Convert.FromBase64String(SingleGroupRvz);
        using var ms = new MemoryStream();
        RvzDecoder.Decode(rvz, ms);
        var full = ms.ToArray();

        const int limit = 0x10000;
        var prefix = RvzDecoder.DecodeUnencryptedPrefix(rvz, limit);
        Assert.Equal(limit, prefix.Length);
        Assert.Equal(full.AsSpan(0, limit).ToArray(), prefix);
    }

    // Real WIA files written by Wiimms ISO Tools 3.05a (`wit copy small.iso --raw --wia=lzma|lzma2`),
    // from a synthetic 1 MiB GameCube ISO — see tests/fixtures/README.md. These check the LZMA and
    // LZMA2 group codecs AND the container's real layout: the first raw-data region starts at 0x80
    // (after the disc header kept in the disc struct) and is stored from its 0x8000-aligned start.
    [Theory]
    [InlineData("small-lzma.wia")]
    [InlineData("small-lzma2.wia")]
    public void Reconstructs_real_wit_written_lzma_wia_bit_exact(string name)
    {
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (int i = 0; i < 8 && dir is not null && path is null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "wia", name);
            if (File.Exists(candidate)) path = candidate;
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(path);
        using var ms = new MemoryStream();
        var report = RvzDecoder.Decode(File.ReadAllBytes(path!), ms);
        Assert.Equal(1048576, report.IsoBytes);
        Assert.True(report.BitExact);
        Assert.Equal("506e20181c1bf7fa3de4605ce69df22d056a1a23671b6235e73056cdaed72ff3",
            System.Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant());
    }

    private static (RvzDecoder.DecodeReport, string) Decode(string base64Rvz)
    {
        var rvz = System.Convert.FromBase64String(base64Rvz);
        using var ms = new MemoryStream();
        var report = RvzDecoder.Decode(rvz, ms);
        string hash = System.Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
        return (report, hash);
    }
}
