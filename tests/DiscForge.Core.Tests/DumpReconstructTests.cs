using DiscForge.Core.Raw;
using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

public class DumpReconstructTests
{
    private static byte[] Sector(int seed)
    {
        var s = new byte[2352];
        s[0] = 0x00;
        for (int i = 1; i <= 10; i++) s[i] = 0xFF;
        s[11] = 0x00;
        s[12] = 0x00; s[13] = 0x02; s[14] = 0x00; s[15] = 0x01;   // Mode 1
        var rng = new Random(seed);
        for (int i = 16; i < 2064; i++) s[i] = (byte)rng.Next(256);
        EdcEcc.FillMode1(s);
        return s;
    }

    private static byte[] Image(int sectors, int seed)
    {
        var img = new byte[sectors * 2352];
        for (int s = 0; s < sectors; s++)
            Sector(seed * 100 + s).CopyTo(img, s * 2352);
        return img;
    }

    [Fact]
    public void A_clean_single_copy_is_all_agreed()
    {
        var img = Image(4, 1);

        var r = DumpReconstruct.Reconstruct(new[] { img });

        Assert.True(r.Report.FullyRecovered);
        Assert.Equal(4, r.Report.Agreed);
        Assert.Equal(4, r.Report.PerSector.Length);
        Assert.All(r.Report.PerSector, code => Assert.Equal((byte)SectorProvenance.Agreed, code));
        Assert.Equal(img, r.Image);
    }

    [Fact]
    public void A_single_read_with_one_flipped_byte_is_ECC_repaired()
    {
        var pristine = Image(3, 2);
        var damaged = (byte[])pristine.Clone();
        damaged[2352 + 700] ^= 0x5A;   // one wrong byte in sector 1

        var r = DumpReconstruct.Reconstruct(new[] { damaged });

        Assert.True(r.Report.FullyRecovered);
        Assert.Equal(1, r.Report.EccRepairedCopy);
        Assert.True(r.Report.EccBytesCorrected >= 1);
        Assert.Equal(pristine, r.Image);          // recovered byte-for-byte from parity
        Assert.Equal((byte)SectorProvenance.EccRepairedCopy, r.Report.PerSector[1]);
    }

    [Fact]
    public void A_good_second_copy_supplies_a_sector_the_first_lost()
    {
        var pristine = Image(3, 3);
        var bad = (byte[])pristine.Clone();
        // Wreck sector 2 in the first copy far beyond what its own ECC can fix.
        var rng = new Random(9);
        for (int i = 16; i < 1200; i++) bad[2 * 2352 + i] ^= (byte)rng.Next(1, 256);

        var r = DumpReconstruct.Reconstruct(new[] { bad, pristine });

        Assert.True(r.Report.FullyRecovered);
        Assert.Equal(1, r.Report.EdcVerifiedCopy);
        Assert.Equal(pristine, r.Image);
        Assert.Equal((byte)SectorProvenance.EdcVerifiedCopy, r.Report.PerSector[2]);
    }

    [Fact]
    public void Three_copies_vote_a_sector_none_of_them_had_whole()
    {
        var pristine = Image(2, 4);
        var a = (byte[])pristine.Clone();
        var b = (byte[])pristine.Clone();
        var c = (byte[])pristine.Clone();
        // Each copy has a different single wrong byte in sector 1, so a per-byte
        // majority reconstructs the original. ECC is disabled so the vote path is tested.
        a[2352 + 100] ^= 0x11;
        b[2352 + 200] ^= 0x22;
        c[2352 + 300] ^= 0x33;

        var r = DumpReconstruct.Reconstruct(new[] { a, b, c }, useEcc: false);

        Assert.True(r.Report.FullyRecovered);
        Assert.Equal(1, r.Report.VoteVerified);
        Assert.Equal(pristine, r.Image);
        Assert.Equal((byte)SectorProvenance.VoteVerified, r.Report.PerSector[1]);
    }

    [Fact]
    public void Damage_beyond_every_route_is_reported_unrecovered()
    {
        var pristine = Image(2, 5);
        var wrecked = (byte[])pristine.Clone();
        var rng = new Random(13);
        for (int i = 16; i < 1500; i++) wrecked[i] ^= (byte)rng.Next(1, 256);   // sector 0, past ECC

        var r = DumpReconstruct.Reconstruct(new[] { wrecked });

        Assert.False(r.Report.FullyRecovered);
        Assert.Equal(1, r.Report.Unrecovered);
        Assert.Contains(0, r.Report.UnrecoveredSectors);
        Assert.Equal((byte)SectorProvenance.Unrecovered, r.Report.PerSector[0]);
    }

    [Fact]
    public void Mismatched_copy_lengths_are_rejected()
    {
        bool threw = false;
        try { DumpReconstruct.Reconstruct(new[] { new byte[2352], new byte[4704] }); }
        catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }
}
