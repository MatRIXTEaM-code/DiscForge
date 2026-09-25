// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;
using DiscForge.Core.Patch;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// VcdiffPatch against real patches made by xdelta3 3.0.11 (tests/fixtures/xdelta — see the README
/// there for the exact commands). The source and target are regenerated here from a fixed LCG so the
/// fixtures stay a few KB: source = 200,000 LCG bytes (seed 1); target = source with bytes 1000–1099
/// set to 0xAA, "HELLO"×200 inserted at 50,000, bytes 120,000–120,999 (of that result) removed, and
/// 3,000 LCG bytes (seed 2) appended.
/// </summary>
public class VcdiffPatchTests
{
    private const string TargetSha1 = "1bfdd91ac0bec3c00d5e64d0fbade20f8b13cef0";

    private static byte[] Lcg(int seed, int n)
    {
        var o = new byte[n];
        uint x = (uint)seed;
        for (int i = 0; i < n; i++)
        {
            x = (x * 1103515245u + 12345u) & 0x7fffffff;
            o[i] = (byte)((x >> 16) & 0xFF);
        }
        return o;
    }

    private static byte[] Source() => Lcg(1, 200000);

    private static byte[] Target()
    {
        var t = new List<byte>(Source());
        for (int i = 1000; i < 1100; i++) t[i] = 0xAA;
        t.InsertRange(50000, Enumerable.Repeat("HELLO"u8.ToArray(), 200).SelectMany(b => b));
        t.RemoveRange(120000, 1000);
        t.AddRange(Lcg(2, 3000));
        return t.ToArray();
    }

    private static string FixtureDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "xdelta");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("tests/fixtures/xdelta not found.");
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(FixtureDir(), name));

    [Fact]
    public void Regenerated_target_matches_the_fixture_description()
    {
        Assert.Equal(TargetSha1, System.Convert.ToHexString(SHA1.HashData(Target())).ToLowerInvariant());
    }

    [Theory]
    [InlineData("none.xd")]   // -S none: header still names LZMA, no section compressed
    [InlineData("lzma.xd")]   // -S lzma: sections are .xz streams
    [InlineData("multi.xd")]  // -W 16384: 13 windows, some sections compressed, some not
    public void Applies_real_xdelta3_patches(string name)
    {
        var patch = Fixture(name);
        var info = VcdiffPatch.Inspect(patch);
        Assert.True(info.Supported);
        Assert.Equal(203000, info.TargetSize);
        Assert.True(info.HasChecksums);
        Assert.Equal(TargetSha1, System.Convert.ToHexString(SHA1.HashData(VcdiffPatch.Apply(patch, Source()))).ToLowerInvariant());
    }

    [Fact]
    public void Multi_window_patch_reports_its_windows()
    {
        var info = VcdiffPatch.Inspect(Fixture("multi.xd"));
        Assert.Equal(13, info.WindowCount);
        Assert.Contains("tgt.bin", info.ApplicationHeader);
    }

    [Fact]
    public void Patch_without_a_source_rebuilds_target_on_its_own()
    {
        var expected = System.Text.Encoding.ASCII.GetBytes(
            string.Concat(Enumerable.Repeat("DiscForge xdelta fixture ", 4000)))[..100000];
        Assert.Equal(expected, VcdiffPatch.Apply(Fixture("nosrc.xd"), Array.Empty<byte>()));
    }

    [Fact]
    public void Lzma_compressed_sections_decode_through_real_lzma_chunks()
    {
        // words-lzma.xd: a no-source patch of 6,000 words of text, so every section is genuinely
        // LZMA-compressed (not stored), exercising the LZMA2 model rather than just its framing.
        var patch = Fixture("words-lzma.xd");
        var info = VcdiffPatch.Inspect(patch);
        Assert.True(info.UsesSecondaryCompression);
        Assert.Equal(2, info.SecondaryCompressor);
        var output = VcdiffPatch.Apply(patch, Array.Empty<byte>());
        Assert.Equal("11f9de8b24524a7130c2d9814d269c487acaeb6d", System.Convert.ToHexString(SHA1.HashData(output)).ToLowerInvariant());
    }

    [Fact]
    public void Wrong_source_is_caught_by_the_window_checksum()
    {
        var wrong = Source();
        wrong[10] ^= 0xFF;
        var ex = Assert.Throws<VcdiffFormatException>(() => VcdiffPatch.Apply(Fixture("none.xd"), wrong));
        Assert.Contains("Adler-32", ex.Message);
        // …and can be overridden, as with the other patch formats' --force.
        Assert.Equal(203000, VcdiffPatch.Apply(Fixture("none.xd"), wrong, verifyChecksums: false).Length);
    }

    [Fact]
    public void Djw_compressed_patch_is_declined_with_a_pointer_to_xdelta3()
    {
        var patch = Fixture("djw.xd");
        var info = VcdiffPatch.Inspect(patch);
        Assert.Equal(1, info.SecondaryCompressor);
        Assert.True(info.UsesSecondaryCompression);
        Assert.False(info.Supported);
        var ex = Assert.Throws<VcdiffFormatException>(() => VcdiffPatch.Apply(patch, Array.Empty<byte>()));
        Assert.Contains("DJW", ex.Message);
    }

    [Fact]
    public void Not_a_vcdiff_file_is_rejected()
    {
        Assert.Throws<VcdiffFormatException>(() => VcdiffPatch.Inspect("PATCH"u8.ToArray()));
    }

    [Fact]
    public void Adler32_matches_the_reference_value()
    {
        // Adler-32 of "Wikipedia" is 0x11E60398 (the worked example in the algorithm's description).
        Assert.Equal(0x11E60398u, VcdiffPatch.Adler32("Wikipedia"u8));
    }
}
