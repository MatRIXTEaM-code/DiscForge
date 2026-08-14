using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class IsoLintTests
{
    private static byte[] GoodIso() =>
        IsoBuilder.Build("LINTVOL", new List<IsoBuilder.FileEntry>
        {
            new("READ.ME", Encoding.ASCII.GetBytes("hi")),
            new("DATA.BIN", new byte[3000]),
        }, joliet: false).Image;

    [Fact]
    public void A_well_formed_iso_passes()
    {
        var r = IsoLint.Check(GoodIso());
        Assert.True(r.Ok, IsoLint.Render(r));
        Assert.Equal(0, r.Errors);
    }

    [Fact]
    public void A_broken_magic_is_an_error()
    {
        var iso = GoodIso();
        iso[16 * 2048 + 1] = (byte)'X';           // corrupt "CD001"
        var r = IsoLint.Check(iso);
        Assert.False(r.Ok);
        Assert.Contains(r.Findings, x => x.Where == "PVD" && x.Message.Contains("CD001"));
    }

    [Fact]
    public void A_both_endian_block_size_mismatch_is_caught()
    {
        var iso = GoodIso();
        iso[16 * 2048 + 130] ^= 0xFF;             // corrupt the big-endian half of the block size
        var r = IsoLint.Check(iso);
        Assert.Contains(r.Findings, x => x.Message.Contains("block size both-endian mismatch"));
    }

    [Fact]
    public void A_truncated_volume_is_a_warning()
    {
        var iso = GoodIso();
        // Inflate the recorded volume space size well beyond the image (both endian halves).
        int o = 16 * 2048 + 80;
        iso[o] = 0xFF; iso[o + 1] = 0xFF; iso[o + 2] = 0xFF; iso[o + 3] = 0x7F;   // LE
        iso[o + 4] = 0x7F; iso[o + 5] = 0xFF; iso[o + 6] = 0xFF; iso[o + 7] = 0xFF; // BE
        var r = IsoLint.Check(iso);
        Assert.Contains(r.Findings, x => x.Severity == LintSeverity.Warning && x.Message.Contains("truncated"));
    }

    [Fact]
    public void A_tiny_image_is_rejected()
    {
        var r = IsoLint.Check(new byte[4 * 2048]);
        Assert.False(r.Ok);
        Assert.Contains(r.Findings, x => x.Message.Contains("too small"));
    }
}
