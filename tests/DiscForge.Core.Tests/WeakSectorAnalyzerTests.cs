using DiscForge.Core.Forensics;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

public class WeakSectorAnalyzerTests
{
    private static void WriteSync(byte[] img, int sector)
    {
        int o = sector * 2352;
        img[o] = 0x00;
        for (int i = 1; i <= 10; i++) img[o + i] = 0xFF;
        img[o + 11] = 0x00;
    }

    // A "normal" data sector: sync + random content (scrambles to random → healthy channel).
    private static void WriteNormal(byte[] img, int sector, int seed)
    {
        WriteSync(img, sector);
        var rng = new System.Random(seed);
        int o = sector * 2352;
        for (int i = 12; i < 2352; i++) img[o + i] = (byte)rng.Next(256);
    }

    // A weak sector: content equal to the scramble sequence, so scrambling makes it (near) all-zero,
    // producing a low-transition channel stream.
    private static void WriteWeak(byte[] img, int sector)
    {
        int o = sector * 2352;
        var sec = new byte[2352];
        WriteSync(sec, 0);                          // sync + zero payload
        CdScrambler.ScrambleInPlace(sec);           // payload becomes the scramble sequence
        System.Array.Copy(sec, 0, img, o, 2352);
    }

    [Fact]
    public void A_normal_sector_has_a_healthy_transition_density()
    {
        var img = new byte[2352];
        WriteNormal(img, 0, 1);
        var m = WeakSectorAnalyzer.Measure(0, img);
        Assert.True(m.TransitionDensity > 0.2, $"td {m.TransitionDensity}");
    }

    [Fact]
    public void A_weak_sector_collapses_the_transition_density()
    {
        var normalImg = new byte[2352]; WriteNormal(normalImg, 0, 2);
        var weakImg = new byte[2352]; WriteWeak(weakImg, 0);

        var normal = WeakSectorAnalyzer.Measure(0, normalImg);
        var weak = WeakSectorAnalyzer.Measure(0, weakImg);

        Assert.True(weak.TransitionDensity < normal.TransitionDensity * 0.6,
            $"weak {weak.TransitionDensity} vs normal {normal.TransitionDensity}");
    }

    [Fact]
    public void The_scan_flags_the_weak_sector_among_normal_ones()
    {
        int n = 20;
        var img = new byte[n * 2352];
        for (int s = 0; s < n; s++) WriteNormal(img, s, 100 + s);
        WriteWeak(img, 7);                          // sector 7 is the planted weak one

        var r = WeakSectorAnalyzer.Analyze(img);
        Assert.True(r.AnyWeak);
        Assert.Contains(r.Weak, w => w.Lba == 7);
        Assert.DoesNotContain(r.Weak, w => w.Lba == 3);   // a normal sector isn't flagged
    }

    [Fact]
    public void An_image_with_no_sync_sectors_analyzes_nothing()
    {
        var r = WeakSectorAnalyzer.Analyze(new byte[2352 * 3]);   // all zero → no sync
        Assert.Equal(0, r.SectorsAnalyzed);
        Assert.False(r.AnyWeak);
    }
}
