using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class DiscPhylogenyTests
{
    private static readonly byte[] Engine = Fill('E', 6000);
    private static readonly byte[] Common = Fill('C', 5000);
    private static byte[] Fill(char c, int n) => Encoding.ASCII.GetBytes(new string(c, n));

    private static byte[] Iso(string vol, params (string, byte[])[] files) =>
        IsoBuilder.Build(vol, files.Select(f => new IsoBuilder.FileEntry(f.Item1, f.Item2)).ToList(), joliet: false).Image;

    private static DiscProfile Profile(string name, byte[] iso) => DiscClustering.Profile(name, iso);

    // Two very close variants (differ in one small file) and one distant title.
    private static DiscProfile Rev1() => Profile("rev1", Iso("GAME",
        ("ENGINE.BIN", Engine), ("COMMON.BIN", Common), ("PATCH.BIN", Fill('1', 500))));
    private static DiscProfile Rev2() => Profile("rev2", Iso("GAME",
        ("ENGINE.BIN", Engine), ("COMMON.BIN", Common), ("PATCH.BIN", Fill('2', 500))));
    private static DiscProfile Other() => Profile("other", Iso("OTHER",
        ("SHIP.BIN", Fill('Z', 9000)), ("MAP.BIN", Fill('M', 4000))));

    [Fact]
    public void The_two_close_variants_are_siblings()
    {
        var root = DiscPhylogeny.Build(new[] { Rev1(), Rev2(), Other() });

        // The deepest internal node should group exactly rev1 + rev2.
        PhyloNode Deepest(PhyloNode n) =>
            n.IsLeaf ? n : n.Children.Where(c => !c.IsLeaf).DefaultIfEmpty(n).OrderBy(c => c.Height).First() is { IsLeaf: false } d && d != n
                ? Deepest(d) : n;

        var closest = FindClosestPair(root);
        Assert.Contains("rev1", closest);
        Assert.Contains("rev2", closest);
        Assert.DoesNotContain("other", closest);
    }

    // The lowest-height internal node's leaf set.
    private static IReadOnlyList<string> FindClosestPair(PhyloNode root)
    {
        PhyloNode? best = null;
        void Walk(PhyloNode n)
        {
            if (n.IsLeaf) return;
            if (best is null || n.Height < best.Height) best = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(root);
        return best!.Leaves().ToList();
    }

    [Fact]
    public void A_single_disc_is_a_leaf_root()
    {
        var root = DiscPhylogeny.Build(new[] { Rev1() });
        Assert.True(root.IsLeaf);
        Assert.Equal("rev1", root.Name);
    }

    [Fact]
    public void The_tree_contains_every_disc_as_a_leaf()
    {
        var root = DiscPhylogeny.Build(new[] { Rev1(), Rev2(), Other() });
        var leaves = root.Leaves().ToHashSet();
        Assert.Equal(new[] { "other", "rev1", "rev2" }.ToHashSet(), leaves);
    }

    [Fact]
    public void Newick_and_tree_render_are_produced()
    {
        var root = DiscPhylogeny.Build(new[] { Rev1(), Rev2(), Other() });
        var newick = DiscPhylogeny.ToNewick(root);
        Assert.StartsWith("(", newick);
        Assert.EndsWith(";", newick);
        Assert.Contains("rev1", newick);

        var tree = DiscPhylogeny.RenderTree(root);
        Assert.Contains("diverge", tree);
        Assert.Contains("- rev1", tree);
    }
}
