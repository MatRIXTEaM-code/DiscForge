using DiscForge.Core.Audio;
using DiscForge.Core.Dumping;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

public class DumpingWizardTests
{
    // A varied PCM buffer so the alignment search has a clear minimum.
    private static byte[] Pcm(int samples)
    {
        var b = new byte[samples * 4];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)((i * 37 + 11) & 0xFF);
        return b;
    }

    [Fact]
    public void Detects_zero_offset_for_identical_pcm()
    {
        var pcm = Pcm(300);
        Assert.Equal(0, ReadOffsetDetect.DetectSampleOffset(pcm, pcm, 32));
    }

    [Fact]
    public void Detects_a_positive_shift()
    {
        var reference = Pcm(400);
        var rip = ReadOffset.Apply(reference, 5);   // rip[i] = reference[i+5]
        Assert.Equal(5, ReadOffsetDetect.DetectSampleOffset(reference, rip, 32));
    }

    [Fact]
    public void Detects_a_negative_shift()
    {
        var reference = Pcm(400);
        var rip = ReadOffset.Apply(reference, -7);
        Assert.Equal(-7, ReadOffsetDetect.DetectSampleOffset(reference, rip, 32));
    }

    [Fact]
    public void A_clean_dump_with_a_known_offset_scores_100_A()
    {
        var s = DumpConfidence.Score(new DumpQuality(1000, 1000, 0, 0, 0, OffsetKnown: true));
        Assert.Equal(100, s.Score);
        Assert.Equal('A', s.Grade);
    }

    [Fact]
    public void An_unknown_offset_costs_a_few_points_but_stays_an_A()
    {
        var s = DumpConfidence.Score(new DumpQuality(1000, 1000, 0, 0, 0, OffsetKnown: false));
        Assert.Equal(95, s.Score);
        Assert.Equal('A', s.Grade);
    }

    [Fact]
    public void Edc_failures_pull_the_grade_down()
    {
        var s = DumpConfidence.Score(new DumpQuality(1000, 1000, 1, 0, 0, OffsetKnown: true));
        Assert.Equal(85, s.Score);
        Assert.Equal('B', s.Grade);
    }

    [Fact]
    public void Any_unrecovered_sector_caps_the_grade_at_failing()
    {
        var s = DumpConfidence.Score(new DumpQuality(1000, 1000, 0, 0, 1, OffsetKnown: true));
        Assert.True(s.Score <= 50);
        Assert.Equal('F', s.Grade);
    }

    [Fact]
    public void Empty_image_scores_zero()
    {
        var s = DumpConfidence.Score(new DumpQuality(0, 0, 0, 0, 0, false));
        Assert.Equal(0, s.Score);
        Assert.Equal('F', s.Grade);
    }

    // A valid raw Mode 1 sector filled with `fill`.
    private static byte[] Mode1(byte fill)
    {
        var s = new byte[2352];
        s[0] = 0x00;
        for (int i = 1; i <= 10; i++) s[i] = 0xFF;
        s[11] = 0x00; s[12] = 0x00; s[13] = 0x02; s[14] = 0x00; s[15] = 0x01;
        for (int i = 16; i < 2064; i++) s[i] = fill;
        EdcEcc.FillMode1(s);
        return s;
    }

    [Fact]
    public void Scan_raw_counts_edc_checkable_and_failed_sectors()
    {
        var s0 = Mode1(0x10);
        var s1 = Mode1(0x20);
        var s2 = Mode1(0x30);
        s2[16 + 100] ^= 0xFF;                        // break sector 2's EDC

        var image = new byte[3 * 2352];
        System.Array.Copy(s0, 0, image, 0, 2352);
        System.Array.Copy(s1, 0, image, 2352, 2352);
        System.Array.Copy(s2, 0, image, 2 * 2352, 2352);

        var q = DumpConfidence.ScanRaw(image);
        Assert.Equal(3, q.TotalSectors);
        Assert.Equal(3, q.EdcCheckable);
        Assert.Equal(1, q.EdcFailed);
    }
}
