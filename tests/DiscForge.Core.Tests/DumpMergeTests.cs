using System;
using System.Collections.Generic;
using DiscForge.Core.Raw;
using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

public class DumpMergeTests
{
    // A valid raw Mode 1 sector filled with `fill` in the user area.
    private static byte[] Mode1(byte fill)
    {
        var s = new byte[2352];
        s[0] = 0x00;
        for (int i = 1; i <= 10; i++) s[i] = 0xFF;
        s[11] = 0x00;
        s[12] = 0x00; s[13] = 0x02; s[14] = 0x00;   // MSF (BCD), arbitrary
        s[15] = 0x01;                               // mode 1
        for (int i = 16; i < 2064; i++) s[i] = fill;
        EdcEcc.FillMode1(s);
        return s;
    }

    // A bare "audio" sector: no sync mark, so no EDC to check.
    private static byte[] Audio(byte fill)
    {
        var s = new byte[2352];
        for (int i = 0; i < s.Length; i++) s[i] = fill;
        s[0] = 0x12;   // deliberately not the 00 FF..FF 00 sync
        return s;
    }

    private static byte[] Concat(params byte[][] sectors)
    {
        var outp = new byte[sectors.Length * 2352];
        for (int i = 0; i < sectors.Length; i++) Array.Copy(sectors[i], 0, outp, i * 2352, 2352);
        return outp;
    }

    private static byte[] Corrupt(byte[] sector, int userByte, byte value)
    {
        var c = (byte[])sector.Clone();
        c[16 + userByte] = value;   // change a user byte -> breaks EDC
        return c;
    }

    [Fact]
    public void Identical_copies_pass_through_unchanged()
    {
        var img = Concat(Mode1(0x10), Mode1(0x20));
        var r = DumpMerge.Merge(new List<byte[]> { (byte[])img.Clone(), (byte[])img.Clone() });
        Assert.Equal(img, r.Image);
        Assert.Equal(2, r.Report.Identical);
        Assert.True(r.Report.FullyRecovered);
        Assert.Equal(0, r.Report.Repaired);
    }

    [Fact]
    public void A_good_copy_is_used_when_another_is_corrupt()
    {
        var s0 = Mode1(0x11);
        var s1 = Mode1(0x22);
        var good = Concat(s0, s1);
        var bad = Concat(s0, Corrupt(s1, 100, 0xFF));   // sector 1 broken in this copy

        var r = DumpMerge.Merge(new List<byte[]> { bad, good });
        Assert.Equal(good, r.Image);                    // recovered to the correct image
        Assert.Equal(1, r.Report.EdcRecovered);
        Assert.Equal(1, r.Report.Identical);            // sector 0 agreed
        Assert.True(r.Report.FullyRecovered);
    }

    [Fact]
    public void A_sector_no_single_copy_has_whole_is_rebuilt_by_voting()
    {
        var correct = Mode1(0x33);
        // Three copies, each with a DIFFERENT single user byte wrong. The per-byte
        // majority reconstructs the original, which then passes its EDC.
        var a = Concat(Corrupt(correct, 10, 0x01));
        var b = Concat(Corrupt(correct, 20, 0x02));
        var c = Concat(Corrupt(correct, 30, 0x03));

        var r = DumpMerge.Merge(new List<byte[]> { a, b, c });
        Assert.Equal(Concat(correct), r.Image);
        Assert.Equal(1, r.Report.VoteVerified);
        Assert.True(r.Report.FullyRecovered);
    }

    [Fact]
    public void Audio_sectors_are_majority_voted_as_best_effort()
    {
        var a = Concat(Audio(0xAA));
        var b = Concat(Audio(0xAA));
        var c = Concat(Audio(0xAA));
        // Flip one byte in copy c only; the 2/3 majority keeps 0xAA.
        c[500] = 0x99;

        var r = DumpMerge.Merge(new List<byte[]> { a, b, c });
        Assert.Equal(0xAA, r.Image[500]);
        Assert.Equal(1, r.Report.VoteBestEffort);
        Assert.True(r.Report.FullyRecovered);           // best-effort still counts as recovered
    }

    [Fact]
    public void A_data_sector_corrupt_in_all_copies_is_reported_unrecovered()
    {
        var correct = Mode1(0x44);
        // All three copies corrupt the SAME byte to three different values: no
        // majority, and the vote can't pass the EDC.
        var a = Concat(Corrupt(correct, 50, 0x01));
        var b = Concat(Corrupt(correct, 50, 0x02));
        var c = Concat(Corrupt(correct, 50, 0x03));

        var r = DumpMerge.Merge(new List<byte[]> { a, b, c });
        Assert.Equal(1, r.Report.Unrecovered);
        Assert.False(r.Report.FullyRecovered);
        Assert.Contains(0, r.Report.UnrecoveredSectors);
    }

    [Fact]
    public void Mismatched_image_lengths_are_rejected()
    {
        bool threw = false;
        try
        {
            DumpMerge.Merge(new List<byte[]> { new byte[2352], new byte[2352 * 2] });
        }
        catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void A_single_copy_is_returned_unchanged()
    {
        var img = Concat(Mode1(0x55));
        var r = DumpMerge.Merge(new List<byte[]> { img });
        Assert.Equal(img, r.Image);
        Assert.Equal(1, r.Report.Identical);
    }
}
