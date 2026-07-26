// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// CHD cdfl (FLAC) extraction, validated against a real chdman all-FLAC image of
/// the same track. The FLAC decoder (frame/subframe parsing, Rice residuals,
/// stereo decorrelation) plus the hunk walk must self-verify against the CHD's
/// stored SHA-1 and reproduce the identical track bytes. The sample is a repo
/// asset (FLAC of this synthetic track is large), located via the source path so
/// it resolves wherever the tests are built.
/// </summary>
public class ChdFlacTests
{
    private static string AssetPath([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "assets", "test-cdfl.chd");

    [Fact]
    public void RealCdflChd_ExtractsVerifiedAndByteExact()
    {
        var chd = File.ReadAllBytes(AssetPath());
        var r = ChdExtractor.ExtractCd(chd);
        Assert.True(r.Verified);
        Assert.Equal(1, r.Tracks);
        Assert.Equal(2822400, r.Bin.Length);
        string sha = System.Convert.ToHexString(SHA256.HashData(r.Bin)).ToLowerInvariant();
        Assert.Equal("ba42d5132f62d7f39fe8ce186f32e71de3c06653154905fc0123052abde59ebd", sha);
    }

    [Fact]
    public void Codec_is_cdfl()
    {
        Assert.Equal("cdfl", ChdReader.Read(File.ReadAllBytes(AssetPath())).Compressors[0]);
    }
}
