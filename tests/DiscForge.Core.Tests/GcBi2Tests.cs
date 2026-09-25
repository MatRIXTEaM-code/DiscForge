// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.GameCube;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>Tests for the bi2.bin parser and the full header→apploader→DOL→FST boot-chain confirmation
/// — as opposed to <see cref="GcBootTests"/>, which only checks that the apploader/DOL each parse in
/// isolation, not that the chain as a whole is internally consistent.</summary>
public class GcBi2Tests
{
    private static void Be32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);

    /// <summary>A minimal but internally-consistent image: bi2 + a trivial (zero-size) apploader + a
    /// trivial DOL + an in-bounds FST. Every stage of the chain passes.</summary>
    private static byte[] BuildSaneImage()
    {
        var img = new byte[0x10000];
        // bi2.bin @ 0x440.
        Be32(img, 0x440 + 0x00, 0);       // debug monitor size
        Be32(img, 0x440 + 0x04, 0);       // simulated memory size
        img[0x440 + 0x18] = 1;            // country code: NTSC-U

        // Apploader @ 0x2440 — zero size/trailer, so it ends right where it starts.
        Encoding.ASCII.GetBytes("2001/11/18").CopyTo(img, 0x2440);

        // DOL @ 0x3000 — one tiny text section.
        int dol = 0x3000;
        Be32(img, dol + 0x00, 0x100); Be32(img, dol + 0x90, 0x40);
        Be32(img, dol + 0xE0, 0x81300000);

        // Header pointers.
        Be32(img, 0x420, (uint)dol);
        Be32(img, 0x424, 0x9000);   // FST offset
        Be32(img, 0x428, 0x40);     // FST size
        return img;
    }

    [Fact]
    public void ReadBi2_parses_the_country_code_and_debug_fields()
    {
        var img = BuildSaneImage();
        var bi2 = GcBoot.ReadBi2(new MemoryStream(img));
        Assert.Equal((byte)1, bi2.CountryCode);
        Assert.Equal("NTSC-U", bi2.CountryName);
        Assert.False(bi2.LooksLikeDebugBuild);
    }

    [Fact]
    public void ReadBi2_flags_a_nonzero_debug_monitor_size_as_a_debug_build()
    {
        var img = BuildSaneImage();
        Be32(img, 0x440 + 0x00, 0x100000);   // a dev-kit-sized debug monitor
        var bi2 = GcBoot.ReadBi2(new MemoryStream(img));
        Assert.True(bi2.LooksLikeDebugBuild);
    }

    [Fact]
    public void ReadBi2_rejects_a_too_short_image()
    {
        Assert.Throws<GameCubeFormatException>(() => GcBoot.ReadBi2(new MemoryStream(new byte[0x100])));
    }

    [Fact]
    public void CheckChain_finds_no_issues_on_a_consistent_boot_chain()
    {
        var img = BuildSaneImage();
        var issues = GcBoot.CheckChain(new MemoryStream(img), dolOffset: 0x3000, fstOffset: 0x9000, fstSize: 0x40);
        Assert.Empty(issues);
    }

    [Fact]
    public void CheckChain_flags_a_DOL_offset_outside_the_image()
    {
        var img = BuildSaneImage();
        var issues = GcBoot.CheckChain(new MemoryStream(img), dolOffset: 0x900000, fstOffset: 0x9000, fstSize: 0x40);
        Assert.Contains(issues, i => i.Stage == "DOL");
    }

    [Fact]
    public void CheckChain_flags_a_DOL_whose_sections_run_past_the_image()
    {
        var img = BuildSaneImage();
        // Blow up the DOL's declared section size so its computed end runs past the image.
        Be32(img, 0x3000 + 0x90, 0x7FFFFFFF);
        var issues = GcBoot.CheckChain(new MemoryStream(img), dolOffset: 0x3000, fstOffset: 0x9000, fstSize: 0x40);
        Assert.Contains(issues, i => i.Stage == "DOL" && i.Detail.Contains("past the end"));
    }

    [Fact]
    public void CheckChain_flags_an_FST_that_runs_past_the_image()
    {
        var img = BuildSaneImage();
        var issues = GcBoot.CheckChain(new MemoryStream(img), dolOffset: 0x3000, fstOffset: 0x9000, fstSize: 0xFFFFFFF);
        Assert.Contains(issues, i => i.Stage == "FST");
    }

    [Fact]
    public void CheckChain_flags_an_apploader_whose_declared_size_runs_past_the_image()
    {
        var img = BuildSaneImage();
        Be32(img, 0x2440 + 0x14, 0x7FFFFFFF);   // apploader size
        var issues = GcBoot.CheckChain(new MemoryStream(img), dolOffset: 0x3000, fstOffset: 0x9000, fstSize: 0x40);
        Assert.Contains(issues, i => i.Stage == "apploader");
    }

    [Fact]
    public void CheckChain_reports_a_missing_bi2_without_throwing()
    {
        var issues = GcBoot.CheckChain(new MemoryStream(new byte[0x100]), dolOffset: -1, fstOffset: -1, fstSize: 0);
        Assert.Contains(issues, i => i.Stage == "bi2.bin");
    }
}
