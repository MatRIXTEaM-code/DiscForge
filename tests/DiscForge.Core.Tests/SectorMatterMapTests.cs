using System.Text;
using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class SectorMatterMapTests
{
    private const int B = 2048;

    [Fact]
    public void Zeros_are_classified_as_padding()
    {
        var map = SectorMatterMap.Analyze(new byte[B * 4]);
        Assert.All(map.Blocks, b => Assert.Equal(MatterClass.Zero, b.Class));
    }

    [Fact]
    public void Ascii_text_is_classified_as_text()
    {
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps. ", 200)));
        var block = new byte[B];
        System.Array.Copy(text, block, B);
        var map = SectorMatterMap.Analyze(block);
        Assert.Equal(MatterClass.Text, map.Blocks[0].Class);
    }

    [Fact]
    public void Random_bytes_are_high_entropy()
    {
        var rnd = new byte[B];
        new System.Random(2).NextBytes(rnd);
        var map = SectorMatterMap.Analyze(rnd);
        Assert.Equal(MatterClass.HighEntropy, map.Blocks[0].Class);
        Assert.True(map.Blocks[0].Entropy > 7.5);
    }

    [Fact]
    public void Structured_binary_is_neither_text_nor_high_entropy()
    {
        // Repeating small-range values: low-ish entropy, not printable text.
        var b = new byte[B];
        for (int i = 0; i < B; i++) b[i] = (byte)(i % 16);
        var map = SectorMatterMap.Analyze(b);
        Assert.Equal(MatterClass.Structured, map.Blocks[0].Class);
    }

    [Fact]
    public void A_mixed_image_tallies_each_class()
    {
        var img = new byte[B * 3];
        // block 0 zeros; block 1 text; block 2 random
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 200)));
        System.Array.Copy(text, 0, img, B, B);
        var rng = new System.Random(3);
        for (int i = 2 * B; i < 3 * B; i++) img[i] = (byte)rng.Next(1, 256);

        var map = SectorMatterMap.Analyze(img);
        Assert.Equal(MatterClass.Zero, map.Blocks[0].Class);
        Assert.Equal(MatterClass.Text, map.Blocks[1].Class);
        Assert.Equal(MatterClass.HighEntropy, map.Blocks[2].Class);
        Assert.Equal(B, map.Bytes[MatterClass.Zero]);
    }

    [Fact]
    public void Render_svg_is_well_formed_with_a_legend()
    {
        var map = SectorMatterMap.Analyze(new byte[B * 10]);
        var svg = SectorMatterMap.RenderSvg(map, "Matter — test");
        Assert.StartsWith("<svg", svg);
        Assert.Contains("</svg>", svg);
        Assert.Contains("Zero", svg);
    }
}
