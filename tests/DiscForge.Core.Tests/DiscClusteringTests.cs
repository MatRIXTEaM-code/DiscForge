using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class DiscClusteringTests
{
    // Shared file bodies — byte-identical across the "variants" of one title.
    private static readonly byte[] Engine = Encoding.ASCII.GetBytes(new string('E', 4000));
    private static readonly byte[] Common1 = Encoding.ASCII.GetBytes(new string('1', 3000));
    private static readonly byte[] Common2 = Encoding.ASCII.GetBytes(new string('2', 3000));
    private static readonly byte[] Common3 = Encoding.ASCII.GetBytes(new string('3', 3000));

    private static byte[] Iso(string vol, params (string name, byte[] data)[] files) =>
        IsoBuilder.Build(vol, files.Select(f => new IsoBuilder.FileEntry(f.name, f.data)).ToList(),
                         joliet: false).Image;

    // Three variants of one title: same engine + common data, one differing localisation file.
    private static byte[] VariantEn() => Iso("MYGAME",
        ("GAME.EXE", Engine), ("COMMON1.BIN", Common1), ("COMMON2.BIN", Common2),
        ("COMMON3.BIN", Common3), ("LANG_EN.BIN", Encoding.ASCII.GetBytes(new string('N', 2000))));
    private static byte[] VariantFr() => Iso("MYGAME",
        ("GAME.EXE", Engine), ("COMMON1.BIN", Common1), ("COMMON2.BIN", Common2),
        ("COMMON3.BIN", Common3), ("LANG_FR.BIN", Encoding.ASCII.GetBytes(new string('F', 2000))));
    private static byte[] VariantDe() => Iso("MYGAME",
        ("GAME.EXE", Engine), ("COMMON1.BIN", Common1), ("COMMON2.BIN", Common2),
        ("COMMON3.BIN", Common3), ("LANG_DE.BIN", Encoding.ASCII.GetBytes(new string('D', 2000))));

    // A wholly unrelated title: different files, different volume id.
    private static byte[] OtherGame() => Iso("SPACEBLAST",
        ("SHIP.EXE", Encoding.ASCII.GetBytes(new string('S', 5000))),
        ("LEVELS.DAT", Encoding.ASCII.GetBytes(new string('L', 4000))));

    [Fact]
    public void Two_variants_score_similar()
    {
        var a = DiscClustering.Profile("en.iso", VariantEn());
        var b = DiscClustering.Profile("fr.iso", VariantFr());

        var s = DiscClustering.Similarity(a, b);
        Assert.True(s.Score >= 0.5, $"score was {s.Score}");
        Assert.True(s.PathJaccard > 0.5);
        Assert.True(s.ContentJaccard > 0.5);
        Assert.True(s.VolumeIdRelated);
    }

    [Fact]
    public void An_unrelated_title_scores_low()
    {
        var a = DiscClustering.Profile("en.iso", VariantEn());
        var d = DiscClustering.Profile("other.iso", OtherGame());

        var s = DiscClustering.Similarity(a, d);
        Assert.True(s.Score < 0.2, $"score was {s.Score}");
        Assert.False(s.VolumeIdRelated);
    }

    [Fact]
    public void Identical_dumps_score_one()
    {
        var a = DiscClustering.Profile("copy-a.iso", VariantEn());
        var b = DiscClustering.Profile("copy-b.iso", VariantEn());

        var s = DiscClustering.Similarity(a, b);
        Assert.Equal(1.0, s.Score, 3);
    }

    [Fact]
    public void A_messy_folder_groups_the_variants_and_isolates_the_loner()
    {
        var discs = new[]
        {
            DiscClustering.Profile("game_en.iso", VariantEn()),
            DiscClustering.Profile("game_fr.iso", VariantFr()),
            DiscClustering.Profile("game_de.iso", VariantDe()),
            DiscClustering.Profile("spaceblast.iso", OtherGame()),
        };

        var r = DiscClustering.Cluster(discs);

        Assert.Equal(1, r.GroupCount);
        Assert.Equal(1, r.LonerCount);

        var group = r.Clusters.Single(c => !c.IsSingleton);
        Assert.Equal(3, group.Members.Count);
        Assert.Contains("game_en.iso", group.Members);
        Assert.Contains("game_fr.iso", group.Members);
        Assert.Contains("game_de.iso", group.Members);
        Assert.Equal("MYGAME", group.Label);
        Assert.True(group.Cohesion >= 0.5);

        var loner = r.Clusters.Single(c => c.IsSingleton);
        Assert.Equal("spaceblast.iso", loner.Members[0]);
    }

    [Fact]
    public void A_non_iso_image_profiles_empty_and_never_links()
    {
        var junk = new byte[64 * 2048];
        new Random(7).NextBytes(junk);

        var p = DiscClustering.Profile("junk.bin", junk);
        Assert.Equal(0, p.FileCount);

        var r = DiscClustering.Cluster(new[]
        {
            DiscClustering.Profile("game.iso", VariantEn()),
            p,
        });
        Assert.Equal(0, r.GroupCount);       // nothing links to empty
        Assert.Equal(2, r.LonerCount);
    }

    [Fact]
    public void Links_record_why_two_discs_were_grouped()
    {
        var discs = new[]
        {
            DiscClustering.Profile("en.iso", VariantEn()),
            DiscClustering.Profile("fr.iso", VariantFr()),
        };
        var r = DiscClustering.Cluster(discs);

        Assert.Single(r.Links);
        Assert.True(r.Links[0].Score >= r.Threshold);
    }
}
