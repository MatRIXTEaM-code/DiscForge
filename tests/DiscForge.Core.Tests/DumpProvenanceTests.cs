using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class DumpProvenanceTests
{
    [Fact]
    public void A_clonecd_set_is_recognised()
    {
        var r = DumpProvenance.Infer(new[] { "Game.ccd", "Game.img", "Game.sub" });
        Assert.Equal("CloneCD", r.Best!.Tool);
        Assert.Equal(ProtectionConfidence.Confirmed, r.Best.Confidence);
    }

    [Fact]
    public void An_alcohol_pair_is_recognised()
    {
        var r = DumpProvenance.Infer(new[] { "Game.mds", "Game.mdf" });
        Assert.Equal("Alcohol 120%", r.Best!.Tool);
    }

    [Fact]
    public void A_redump_set_beats_plain_bin_cue()
    {
        var r = DumpProvenance.Infer(new[] { "Game.cue", "Game.bin", "Game (submission-info).txt" });
        Assert.Equal("Redump (DiscImageCreator)", r.Best!.Tool);
    }

    [Fact]
    public void Plain_bin_cue_is_only_a_possible_generic_guess()
    {
        var r = DumpProvenance.Infer(new[] { "Game.cue", "Game.bin" });
        Assert.Equal("generic bin/cue", r.Best!.Tool);
        Assert.Equal(ProtectionConfidence.Possible, r.Best.Confidence);
    }

    [Fact]
    public void Sector_geometry_is_described()
    {
        Assert.Contains("cooked", DumpProvenance.Infer(new[] { "x.iso" }, 2048).GeometryNote);
        Assert.Contains("sub-channel", DumpProvenance.Infer(new[] { "x.img" }, 2448).GeometryNote);
        Assert.Contains("raw rip (main", DumpProvenance.Infer(new[] { "x.bin" }, 2352).GeometryNote);
    }

    [Fact]
    public void An_unknown_fileset_reports_unclear()
    {
        var r = DumpProvenance.Infer(new[] { "notes.txt" });
        Assert.Null(r.Best);
        Assert.Contains("unclear", r.Summary());
    }
}
