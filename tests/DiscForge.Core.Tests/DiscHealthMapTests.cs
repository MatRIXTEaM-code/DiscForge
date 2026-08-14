using DiscForge.Core.Forensics;
using DiscForge.Core.Raw;
using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

public class DiscHealthMapTests
{
    private static byte[] GoodMode1(int seed)
    {
        var s = new byte[2352];
        s[0] = 0x00;
        for (int i = 1; i <= 10; i++) s[i] = 0xFF;
        s[11] = 0x00;
        s[12] = 0x00; s[13] = 0x02; s[14] = 0x00; s[15] = 0x01;
        var rng = new Random(seed);
        for (int i = 16; i < 2064; i++) s[i] = (byte)rng.Next(256);
        EdcEcc.FillMode1(s);
        return s;
    }

    // A raw block with no sync — an audio sector, which carries no EDC.
    private static byte[] Audio(int seed)
    {
        var s = new byte[2352];
        new Random(seed).NextBytes(s);
        s[0] = 0x7F;   // ensure the sync mark is absent
        return s;
    }

    [Fact]
    public void Scan_classifies_good_damaged_and_audio_sectors()
    {
        var good = GoodMode1(1);
        var damaged = GoodMode1(2);
        damaged[900] ^= 0xFF;                       // break its EDC
        var audio = Audio(3);

        var img = new byte[3 * 2352];
        good.CopyTo(img, 0);
        damaged.CopyTo(img, 2352);
        audio.CopyTo(img, 2 * 2352);

        var health = DiscHealthMap.Scan(img);

        Assert.Equal(SectorHealth.Good, health[0]);
        Assert.Equal(SectorHealth.Damaged, health[1]);
        Assert.Equal(SectorHealth.NoEcc, health[2]);
    }

    [Fact]
    public void RenderSvg_is_a_well_formed_document_with_the_damaged_colour_and_legend()
    {
        var health = new[] { SectorHealth.Good, SectorHealth.Damaged, SectorHealth.NoEcc };

        string svg = DiscHealthMap.RenderSvg(health, "Test Disc");

        Assert.StartsWith("<svg", svg);
        Assert.EndsWith("</svg>\n", svg);
        Assert.Contains("Test Disc", svg);
        Assert.Contains("#c62828", svg);            // damaged red is present
        Assert.Contains("damaged 1", svg);          // legend counts the one damaged sector
        Assert.Contains("intact 1", svg);
    }

    [Fact]
    public void Large_images_are_aggregated_so_damage_never_hides()
    {
        // 100k sectors, all good but one damaged deep in the middle. Aggregation must
        // keep the damaged cell visible (worst-of-block), not average it away.
        int n = 100_000;
        var health = new SectorHealth[n];
        Array.Fill(health, SectorHealth.Good);
        health[54_321] = SectorHealth.Unrecovered;

        string svg = DiscHealthMap.RenderSvg(health, "Big", maxCells: 4096);

        Assert.Contains("#b71c1c", svg);            // the single unrecovered cell survives
        Assert.Contains("each cell =", svg);        // aggregation note present
        Assert.Contains("unrecovered 1", svg);      // total still counts exactly one
    }

    [Fact]
    public void Provenance_maps_to_health_colours()
    {
        var prov = new byte[]
        {
            (byte)SectorProvenance.Agreed,
            (byte)SectorProvenance.EccRepairedCopy,
            (byte)SectorProvenance.VoteVerified,
            (byte)SectorProvenance.Unrecovered,
        };

        var health = DiscHealthMap.FromProvenance(prov);

        Assert.Equal(SectorHealth.Good, health[0]);
        Assert.Equal(SectorHealth.EccRepaired, health[1]);
        Assert.Equal(SectorHealth.Voted, health[2]);
        Assert.Equal(SectorHealth.Unrecovered, health[3]);
    }
}
