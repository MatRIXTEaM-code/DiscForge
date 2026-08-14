using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class PregapForensicsTests
{
    private static byte[] Silence(int sectors) => new byte[sectors * 2352];

    private static byte[] Music(int sectors)
    {
        var b = new byte[sectors * 2352];
        int samples = b.Length / 2;
        for (int i = 0; i < samples; i++)
        {
            short v = (short)(12000 * System.Math.Sin(2 * System.Math.PI * i / 40.0));
            b[i * 2] = (byte)v;
            b[i * 2 + 1] = (byte)(v >> 8);
        }
        return b;
    }

    [Fact]
    public void A_silent_pregap_hides_nothing()
    {
        var g = PregapForensics.AnalyzeGap(1, "track-1-pregap", Silence(150));
        Assert.False(g.ContainsAudio);
    }

    [Fact]
    public void A_hidden_track_before_track_1_is_detected()
    {
        var r = PregapForensics.Analyze(new[]
        {
            (1, "track-1-pregap", Music(150)),   // HTOA
            (2, "gap", Silence(2)),
        });
        Assert.True(r.HasHiddenAudio);
        Assert.Contains("HTOA", r.Summary());
        Assert.Contains(r.Gaps, g => g.Kind == "track-1-pregap" && g.ContainsAudio);
    }

    [Fact]
    public void An_between_track_easter_egg_is_flagged_without_htoa_wording()
    {
        var r = PregapForensics.Analyze(new[]
        {
            (1, "track-1-pregap", Silence(150)),
            (5, "gap", Music(3)),                // audio hidden in a later gap
        });
        Assert.True(r.HasHiddenAudio);
        Assert.DoesNotContain("HTOA", r.Summary());
    }

    [Fact]
    public void Near_silent_dither_is_not_mistaken_for_audio()
    {
        var b = new byte[150 * 2352];
        for (int i = 0; i + 1 < b.Length; i += 2) { b[i] = 1; b[i + 1] = 0; }   // +1 LSB dither
        var g = PregapForensics.AnalyzeGap(1, "track-1-pregap", b);
        Assert.False(g.ContainsAudio);
    }
}
