using DiscForge.Core.Forensics;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

public class TwinSectorScanTests
{
    private const int SS = 2352;

    // Write sync + a BCD header address (for the given declared LBA) + mode into a sector.
    private static void WriteHeader(byte[] img, int pos, int declaredLba, byte mode = 1)
    {
        int o = pos * SS;
        img[o] = 0x00;
        for (int i = 1; i <= 10; i++) img[o + i] = 0xFF;
        img[o + 11] = 0x00;
        int abs = declaredLba + 150;
        img[o + 12] = Bcd.From(abs / (60 * 75));
        img[o + 13] = Bcd.From(abs / 75 % 60);
        img[o + 14] = Bcd.From(abs % 75);
        img[o + 15] = mode;
        for (int i = 16; i < SS; i++) img[o + i] = (byte)((pos * 131 + i) & 0xFF);
    }

    // A normal contiguous data image: sector i declares LBA i + baseLba.
    private static byte[] Contiguous(int sectors, int baseLba = 0)
    {
        var img = new byte[sectors * SS];
        for (int i = 0; i < sectors; i++) WriteHeader(img, i, i + baseLba);
        return img;
    }

    [Fact]
    public void A_contiguous_image_has_no_anomalies()
    {
        var r = TwinSectorScan.Analyze(Contiguous(200));
        Assert.Equal(200, r.SectorsScanned);
        Assert.Equal(0, r.TwinSectors);
        Assert.Equal(0, r.MisaddressedSectors);
        Assert.False(r.LooksProtected);
    }

    [Fact]
    public void A_globally_shifted_image_is_not_flagged()
    {
        // Every sector's address is offset by 1000 — a legitimately shifted dump, not tampering.
        var r = TwinSectorScan.Analyze(Contiguous(200, baseLba: 1000));
        Assert.Equal(0, r.TwinSectors);
        Assert.Equal(0, r.MisaddressedSectors);
        Assert.False(r.LooksProtected);
    }

    [Fact]
    public void Twin_sectors_sharing_an_address_are_detected()
    {
        var img = Contiguous(200);
        WriteHeader(img, 150, 10);   // sector 150 also claims LBA 10 — a twin of sector 10
        var r = TwinSectorScan.Analyze(img);

        Assert.True(r.TwinSectors >= 2);       // both sector 10 and sector 150 claim LBA 10
        Assert.True(r.LooksProtected);
        Assert.Contains(r.Samples, a => a.Kind == "twin" && a.DeclaredLba == 10);
    }

    [Fact]
    public void Re_addressed_sectors_breaking_the_progression_are_flagged()
    {
        var img = Contiguous(200);
        WriteHeader(img, 50, 5050);   // jumps off the contiguous run
        WriteHeader(img, 60, 5060);
        var r = TwinSectorScan.Analyze(img);

        Assert.Equal(2, r.MisaddressedSectors);
        Assert.True(r.LooksProtected);
    }

    [Fact]
    public void A_single_stray_address_is_treated_as_noise()
    {
        var img = Contiguous(200);
        WriteHeader(img, 77, 9999);   // one lone oddity
        var r = TwinSectorScan.Analyze(img);

        Assert.Equal(1, r.MisaddressedSectors);
        Assert.False(r.LooksProtected);   // one alone isn't enough to call protection
    }

    [Fact]
    public void Audio_sectors_without_sync_are_skipped()
    {
        var img = Contiguous(100);
        // Overwrite 10 sectors with sync-less "audio" content.
        for (int p = 100; p < 110; p++) { }        // (image only has 100 data sectors)
        var withAudio = new byte[110 * SS];
        System.Array.Copy(img, withAudio, img.Length);
        for (int p = 100; p < 110; p++)
            for (int i = 0; i < SS; i++) withAudio[p * SS + i] = (byte)(p + i);   // no sync mark

        var r = TwinSectorScan.Analyze(withAudio);
        Assert.Equal(100, r.SectorsScanned);       // only the 100 data sectors counted
    }
}
