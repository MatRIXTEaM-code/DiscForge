// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;
using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// CHD extraction of a DATA track (Mode 1/2352) — the case that exercises sector-ECC
/// regeneration. This is a real chdman CD CHD of 40 valid MODE1 sectors (correct
/// sync + EDC + ECC): chdman strips the ECC and flags those frames for compression,
/// so the extractor must decode the map, decompress the hunks, and regenerate sync +
/// ECC to match the CHD's stored SHA-1 and the original track byte-for-byte.
/// </summary>
public class ChdDataTrackTests
{
    private static byte[] Sample() => System.Convert.FromBase64String(Chd);

    [Fact]
    public void DataTrackChd_RegeneratesEcc_AndIsByteExact()
    {
        var r = ChdExtractor.ExtractCd(Sample());
        Assert.True(r.Verified);
        Assert.Equal(1, r.Tracks);
        Assert.Equal(40 * 2352, r.Bin.Length);
        string sha = System.Convert.ToHexString(SHA256.HashData(r.Bin)).ToLowerInvariant();
        Assert.Equal("463d9ec49107779489ddcd729c22e62ac3dd627b22740845923783b455291747", sha);
    }

    [Fact]
    public void DataTrack_isModeOne()
    {
        var t = Assert.Single(ChdReader.Read(Sample()).Tracks);
        Assert.Contains("MODE1", t.Type);
        Assert.Equal(40, t.Frames);
    }

    private const string Chd =
        "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";
}
