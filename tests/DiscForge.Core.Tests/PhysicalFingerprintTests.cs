using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class PhysicalFingerprintTests
{
    private const int Bands = 64;
    private const int PerBand = 10;   // 640 positional samples → 64 bands

    // Build a positional scan with C1 spikes in the given bands (defects at those radii).
    private static List<ScanSample> Scan(params (int band, int c1)[] defects)
    {
        var scan = new List<ScanSample>(Bands * PerBand);
        var byBand = new int[Bands];
        foreach (var (band, c1) in defects) byBand[band] = c1;
        for (int b = 0; b < Bands; b++)
            for (int i = 0; i < PerBand; i++)
                scan.Add(new ScanSample(byBand[b], 0, 0));
        return scan;
    }

    private static PhysicalFingerprint Print(string id, List<ScanSample> scan)
        => PhysicalFingerprinter.Compute(id, scan, Bands);

    [Fact]
    public void The_same_disc_read_twice_matches_itself()
    {
        var scan = Scan((5, 200), (12, 150), (30, 300), (48, 120), (55, 90));
        var a = Print("disc", scan);
        var b = Print("disc", scan);

        var m = PhysicalFingerprinter.Compare(a, b);
        Assert.True(m.Distinctive);
        Assert.True(m.SamePhysicalCopy);
        Assert.True(m.Similarity > 0.95);
    }

    [Fact]
    public void The_same_copy_after_extra_rot_still_matches()
    {
        var young = Print("disc", Scan((5, 200), (12, 150), (30, 300), (48, 120), (55, 90)));
        // Same defects, higher, plus two NEW rot bands appearing over time.
        var aged = Print("disc", Scan((5, 260), (12, 190), (30, 340), (48, 160), (55, 120), (20, 80), (40, 70)));

        var m = PhysicalFingerprinter.Compare(young, aged);
        Assert.True(m.Distinctive);
        Assert.True(m.SamePhysicalCopy);
    }

    [Fact]
    public void A_different_copy_of_the_same_title_does_not_match()
    {
        var a = Print("copy A", Scan((5, 200), (12, 150), (30, 300), (48, 120), (55, 90)));
        var b = Print("copy B", Scan((2, 180), (18, 220), (34, 140), (60, 260), (44, 110)));   // defects elsewhere

        var m = PhysicalFingerprinter.Compare(a, b);
        Assert.True(m.Distinctive);
        Assert.False(m.SamePhysicalCopy);
        Assert.True(m.Similarity < 0.75);
    }

    [Fact]
    public void Two_pristine_discs_are_not_falsely_matched()
    {
        var a = Print("clean A", Scan());   // no defects
        var b = Print("clean B", Scan());

        Assert.True(a.Distinctiveness < 0.5);
        var m = PhysicalFingerprinter.Compare(a, b);
        Assert.False(m.Distinctive);
        Assert.False(m.SamePhysicalCopy);
        Assert.Contains("too clean", m.Assessment);
    }

    [Fact]
    public void C2_and_cu_errors_weigh_more_than_c1()
    {
        var c1Only = PhysicalFingerprinter.Compute("a", new List<ScanSample> { new(10, 0, 0) }, 1);
        var withCu = PhysicalFingerprinter.Compute("b", new List<ScanSample> { new(10, 0, 1) }, 1);
        Assert.True(withCu.TotalErrors > c1Only.TotalErrors);
    }

    [Fact]
    public void Distinctiveness_grows_with_the_number_of_defect_bands()
    {
        var few = Print("few", Scan((5, 200)));
        var many = Print("many", Scan((5, 200), (12, 150), (30, 300), (48, 120), (55, 90), (20, 80), (40, 70), (60, 100)));
        Assert.True(many.Distinctiveness > few.Distinctiveness);
    }

    [Fact]
    public void Mismatched_band_counts_are_rejected()
    {
        var a = PhysicalFingerprinter.Compute("a", Scan((5, 100)), 64);
        var b = PhysicalFingerprinter.Compute("b", Scan((5, 100)), 32);
        bool threw = false;
        try { PhysicalFingerprinter.Compare(a, b); }
        catch (System.ArgumentException) { threw = true; }
        Assert.True(threw);
    }
}
