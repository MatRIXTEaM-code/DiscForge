using System.Text;
using DiscForge.Core.Iso;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

public class CollectionArchiveTests
{
    private static readonly byte[] Engine = Fill('E', 8000);
    private static readonly byte[] Common = Fill('C', 6000);
    private static readonly byte[] Shared2 = Fill('S', 5000);

    private static byte[] Fill(char c, int n) => Encoding.ASCII.GetBytes(new string(c, n));

    private static byte[] Iso(string vol, params (string, byte[])[] files) =>
        IsoBuilder.Build(vol, files.Select(f => new IsoBuilder.FileEntry(f.Item1, f.Item2)).ToList(),
                         joliet: false).Image;

    private static byte[] VariantEn() => Iso("GAME",
        ("ENGINE.BIN", Engine), ("COMMON.BIN", Common), ("SHARED2.BIN", Shared2),
        ("LANG.BIN", Fill('N', 3000)));
    private static byte[] VariantFr() => Iso("GAME",
        ("ENGINE.BIN", Engine), ("COMMON.BIN", Common), ("SHARED2.BIN", Shared2),
        ("LANG.BIN", Fill('F', 3000)));
    private static byte[] Unrelated() => Iso("OTHER",
        ("SHIP.BIN", Fill('Z', 9000)), ("MAP.BIN", Fill('M', 4000)));

    [Fact]
    public void Every_disc_rebuilds_byte_for_byte()
    {
        var en = VariantEn();
        var fr = VariantFr();
        var other = Unrelated();
        var archive = CollectionArchiver.Build(new[] { ("en.iso", en), ("fr.iso", fr), ("other.iso", other) });

        Assert.True(CollectionArchiver.VerifyAll(archive));
        Assert.Equal(en, CollectionArchiver.Reconstruct(archive, "en.iso"));
        Assert.Equal(fr, CollectionArchiver.Reconstruct(archive, "fr.iso"));
        Assert.Equal(other, CollectionArchiver.Reconstruct(archive, "other.iso"));
    }

    [Fact]
    public void Shared_files_are_stored_once()
    {
        var archive = CollectionArchiver.Build(new[] { ("en.iso", VariantEn()), ("fr.iso", VariantFr()) });
        var report = CollectionArchiver.Analyze(archive);

        // Two variants each reference 4 files (8 refs), but ENGINE/COMMON/SHARED2 are shared,
        // so unique blobs are far fewer than total references.
        Assert.True(report.Stats.TotalFileRefs >= 8);
        Assert.True(report.Stats.UniqueBlobs < report.Stats.TotalFileRefs);
        Assert.True(report.Stats.DedupRatio > 1.0);
        Assert.True(report.Stats.SavedBytes > 0);
    }

    [Fact]
    public void The_relationship_graph_links_variants_not_the_loner()
    {
        var archive = CollectionArchiver.Build(new[]
        {
            ("en.iso", VariantEn()), ("fr.iso", VariantFr()), ("other.iso", Unrelated()),
        });
        var report = CollectionArchiver.Analyze(archive);

        Assert.Contains(report.Graph, e =>
            (e.A == "en.iso" && e.B == "fr.iso") || (e.A == "fr.iso" && e.B == "en.iso"));
        Assert.DoesNotContain(report.Graph, e => e.A == "other.iso" || e.B == "other.iso");
    }

    [Fact]
    public void The_archive_survives_a_json_round_trip()
    {
        var en = VariantEn();
        var archive = CollectionArchiver.Build(new[] { ("en.iso", en), ("fr.iso", VariantFr()) });

        var back = CollectionArchiver.FromJson(CollectionArchiver.ToJson(archive));
        Assert.True(CollectionArchiver.VerifyAll(back));
        Assert.Equal(en, CollectionArchiver.Reconstruct(back, "en.iso"));
    }

    [Fact]
    public void Unreadable_inputs_are_skipped()
    {
        var junk = new byte[64 * 2048];
        new System.Random(3).NextBytes(junk);
        var archive = CollectionArchiver.Build(new[] { ("good.iso", VariantEn()), ("junk.bin", junk) });

        Assert.Single(archive.Discs);
        Assert.Contains("junk.bin", archive.Skipped);
        Assert.True(CollectionArchiver.VerifyAll(archive));
    }

    [Fact]
    public void Reconstructing_an_unknown_disc_throws()
    {
        var archive = CollectionArchiver.Build(new[] { ("en.iso", VariantEn()) });
        bool threw = false;
        try { CollectionArchiver.Reconstruct(archive, "nope.iso"); }
        catch (System.ArgumentException) { threw = true; }
        Assert.True(threw);
    }
}
