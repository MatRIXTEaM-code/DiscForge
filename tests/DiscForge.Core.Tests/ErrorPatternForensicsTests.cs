using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class ErrorPatternForensicsTests
{
    // Build a per-sector failing flag array of `total` sectors with the given bad LBAs set.
    private static bool[] Bad(int total, params int[] lbas)
    {
        var b = new bool[total];
        foreach (var l in lbas) b[l] = true;
        return b;
    }

    [Fact]
    public void A_clean_read_has_no_lesions()
    {
        var r = ErrorPatternForensics.Classify(new bool[10000]);
        Assert.Equal(0, r.BadSectors);
        Assert.Equal(ErrorPatternKind.None, r.Verdict);
        Assert.Empty(r.Lesions);
    }

    [Fact]
    public void A_solid_burst_is_read_as_a_scratch()
    {
        // 40 adjacent failing sectors — a radial scratch.
        var lbas = Enumerable.Range(5000, 40).ToArray();
        var r = ErrorPatternForensics.Classify(Bad(20000, lbas));

        Assert.Equal(ErrorPatternKind.Scratch, r.Verdict);
        Assert.False(r.LooksDeliberate);
        Assert.Equal(40, r.LongestRun);
        Assert.Single(r.Lesions);
        Assert.Equal(ErrorPatternKind.Scratch, r.Lesions[0].Kind);
        Assert.Contains("recover", r.Recommendation, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tightly_periodic_comb_is_read_as_a_deliberate_pattern()
    {
        // A failing sector every 50 sectors — a periodic layout damage never produces.
        var lbas = new List<int>();
        for (int i = 0; i < 30; i++) lbas.Add(10000 + i * 50);
        var r = ErrorPatternForensics.Classify(Bad(30000, lbas.ToArray()));

        Assert.Equal(ErrorPatternKind.DeliberatePattern, r.Verdict);
        Assert.True(r.LooksDeliberate);
        Assert.Contains(r.Lesions, l => l.Kind == ErrorPatternKind.DeliberatePattern);
        Assert.Contains("preserve", r.Recommendation, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_wide_pitch_comb_across_the_disc_is_still_caught()
    {
        // Period 4000 — far wider than the lesion link gap, so each failing sector lands in its
        // own cluster. The global-comb re-test must still recognise the regularity.
        var lbas = new List<int>();
        for (int i = 0; i < 12; i++) lbas.Add(2000 + i * 4000);
        var r = ErrorPatternForensics.Classify(Bad(60000, lbas.ToArray()));

        Assert.Equal(ErrorPatternKind.DeliberatePattern, r.Verdict);
        Assert.True(r.LooksDeliberate);
    }

    [Fact]
    public void Irregular_scatter_is_read_as_surface_rot()
    {
        // Scattered, non-periodic single failures — pinhole rot, not a scratch or a pattern.
        var lbas = new[] { 120, 900, 2311, 2312, 5000, 9999, 14003, 14980, 21050 };
        var r = ErrorPatternForensics.Classify(Bad(30000, lbas));

        Assert.Equal(ErrorPatternKind.SurfaceRot, r.Verdict);
        Assert.False(r.LooksDeliberate);
        Assert.Contains("rot", r.Recommendation, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_scratch_plus_a_comb_is_reported_as_mixed()
    {
        var lbas = new List<int>();
        lbas.AddRange(Enumerable.Range(3000, 60));          // a scratch
        for (int i = 0; i < 30; i++) lbas.Add(20000 + i * 40);   // a protection comb elsewhere
        var r = ErrorPatternForensics.Classify(Bad(40000, lbas.ToArray()));

        Assert.Equal(ErrorPatternKind.Mixed, r.Verdict);
        Assert.True(r.LooksDeliberate);
        Assert.Contains(r.Lesions, l => l.Kind == ErrorPatternKind.Scratch);
        Assert.Contains(r.Lesions, l => l.Kind == ErrorPatternKind.DeliberatePattern);
    }

    [Fact]
    public void Health_array_damaged_and_unrecovered_count_as_failing()
    {
        var health = new SectorHealth[200];
        for (int i = 0; i < 200; i++) health[i] = SectorHealth.Good;
        health[50] = SectorHealth.Damaged;
        health[51] = SectorHealth.Unrecovered;
        health[52] = SectorHealth.Damaged;
        health[100] = SectorHealth.EccRepaired;   // recovered — NOT currently failing
        health[101] = SectorHealth.NoEcc;         // audio — not failing

        var r = ErrorPatternForensics.Classify(health);
        Assert.Equal(3, r.BadSectors);
        Assert.Equal(50, r.Lesions[0].Start);
        Assert.Equal(52, r.Lesions[0].End);
    }

    [Fact]
    public void From_bad_sectors_dedupes_and_sorts()
    {
        var r = ErrorPatternForensics.FromBadSectors(new[] { 40, 10, 10, 41, 42, -3 }, 1000);
        Assert.Equal(4, r.BadSectors);          // 10,40,41,42 (dupes and negatives dropped)
        Assert.Equal(10, r.Lesions[0].Start);
    }

    [Fact]
    public void Two_separated_scratches_yield_two_lesions()
    {
        var lbas = new List<int>();
        lbas.AddRange(Enumerable.Range(1000, 20));
        lbas.AddRange(Enumerable.Range(8000, 25));
        var r = ErrorPatternForensics.Classify(Bad(20000, lbas.ToArray()));

        Assert.Equal(2, r.Lesions.Count);
        Assert.All(r.Lesions, l => Assert.Equal(ErrorPatternKind.Scratch, l.Kind));
        Assert.Equal(ErrorPatternKind.Scratch, r.Verdict);
    }
}
