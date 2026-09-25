// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

public class C2ErrorMapTests
{
    /// <summary>Build a C2 block flagging exactly the given sector byte indexes.</summary>
    private static byte[] Block(bool msbFirst, params int[] badBytes)
    {
        var c2 = new byte[C2ErrorMap.C2Bytes];
        foreach (int i in badBytes)
        {
            int bit = msbFirst ? 7 - (i & 7) : i & 7;
            c2[i >> 3] |= (byte)(1 << bit);
        }
        return c2;
    }

    [Fact]
    public void An_empty_block_reports_a_clean_sector()
    {
        var map = C2ErrorMap.Parse(new byte[C2ErrorMap.C2Bytes]);
        Assert.True(map.Clean);
        Assert.Equal(0, map.BadByteCount);
    }

    [Fact]
    public void Msb_first_bit_zero_flags_sector_byte_zero()
    {
        // The ordering that matters: MMC says bit 7 of the first C2 byte refers
        // to sector byte 0. Getting this backwards would flag the wrong bytes
        // and make recovery actively harmful.
        var c2 = new byte[C2ErrorMap.C2Bytes];
        c2[0] = 0x80;

        var map = C2ErrorMap.Parse(c2, msbFirst: true);

        Assert.True(map[0]);
        Assert.False(map[1]);
        Assert.False(map[7]);
        Assert.Equal(1, map.BadByteCount);
    }

    [Fact]
    public void Lsb_first_reverses_the_order_within_each_byte()
    {
        var c2 = new byte[C2ErrorMap.C2Bytes];
        c2[0] = 0x80;

        var map = C2ErrorMap.Parse(c2, msbFirst: false);

        Assert.False(map[0]);
        Assert.True(map[7]);
    }

    [Fact]
    public void Arbitrary_bytes_round_trip_through_the_block()
    {
        int[] bad = { 0, 1, 15, 16, 100, 2047, 2351 };
        var map = C2ErrorMap.Parse(Block(msbFirst: true, bad));

        Assert.Equal(bad.Length, map.BadByteCount);
        foreach (int i in bad) Assert.True(map[i], $"byte {i} should be flagged");
        Assert.False(map[2]);
        Assert.False(map[2350]);
    }

    [Fact]
    public void A_fully_set_block_is_recognised_as_total_failure()
    {
        var c2 = new byte[C2ErrorMap.C2Bytes];
        Array.Fill(c2, (byte)0xFF);

        var map = C2ErrorMap.Parse(c2);

        Assert.True(map.Total);
        Assert.Equal(C2ErrorMap.SectorBytes, map.BadByteCount);
    }

    [Fact]
    public void Contiguous_damage_is_reported_as_runs()
    {
        // A scratch produces runs; the count says more than the total does.
        var map = C2ErrorMap.Parse(Block(true, 100, 101, 102, 103, 500, 501));

        var runs = map.BadRuns();

        Assert.Equal(2, runs.Count);
        Assert.Equal((100, 4), runs[0]);
        Assert.Equal((500, 2), runs[1]);
    }

    [Fact]
    public void Damage_outside_the_user_data_is_distinguished()
    {
        // Sync and ECC damage doesn't cost payload — worth knowing before a
        // sector is called unrecoverable.
        var syncOnly = C2ErrorMap.Parse(Block(true, 0, 1, 2));
        Assert.False(syncOnly.AffectsMode1UserData());

        var eccOnly = C2ErrorMap.Parse(Block(true, 2100, 2200));
        Assert.False(eccOnly.AffectsMode1UserData());

        var payload = C2ErrorMap.Parse(Block(true, 1000));
        Assert.True(payload.AffectsMode1UserData());
    }

    [Fact]
    public void Mode_2_user_data_starts_eight_bytes_later()
    {
        // Byte 20 is sub-header in Mode 2 Form 1 but payload in Mode 1.
        var map = C2ErrorMap.Parse(Block(true, 20));

        Assert.True(map.AffectsMode1UserData());
        Assert.False(map.AffectsMode2Form1UserData());
    }

    [Fact]
    public void A_short_block_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => C2ErrorMap.Parse(new byte[100]));
    }
}

public class C2SectorVoterTests
{
    private static byte[] Sector(byte fill)
    {
        var s = new byte[C2ErrorMap.SectorBytes];
        Array.Fill(s, fill);
        return s;
    }

    private static C2ErrorMap MapWithBad(params int[] bad)
    {
        var c2 = new byte[C2ErrorMap.C2Bytes];
        foreach (int i in bad) c2[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return C2ErrorMap.Parse(c2);
    }

    [Fact]
    public void A_single_clean_read_passes_straight_through()
    {
        var voter = new C2SectorVoter();
        voter.Add(Sector(0xAA), C2ErrorMap.None());

        var r = voter.Vote();

        Assert.True(r.Complete);
        Assert.All(r.Sector, b => Assert.Equal(0xAA, b));
    }

    [Fact]
    public void A_byte_flagged_in_one_read_is_taken_from_another()
    {
        // The whole point: read 1 disowns byte 100, read 2 doesn't. The good
        // byte wins even though read 1 came first.
        var a = Sector(0x11); a[100] = 0xDE;
        var b = Sector(0x11); b[100] = 0xAD;

        var voter = new C2SectorVoter();
        voter.Add(a, MapWithBad(100));
        voter.Add(b, C2ErrorMap.None());

        var r = voter.Vote();

        Assert.True(r.Complete);
        Assert.Equal(0xAD, r.Sector[100]);
    }

    [Fact]
    public void Damage_that_moves_between_reads_is_fully_recovered()
    {
        // Neither read is complete on its own; together they cover the sector.
        // This is the case that justifies the whole mechanism.
        var a = Sector(0x22); a[10] = 0xFF; a[11] = 0xFF;
        var b = Sector(0x22); b[500] = 0xFF;

        var voter = new C2SectorVoter();
        voter.Add(a, MapWithBad(10, 11));
        voter.Add(b, MapWithBad(500));

        Assert.True(voter.FullyCovered());
        var r = voter.Vote();

        Assert.True(r.Complete);
        Assert.Equal(0x22, r.Sector[10]);
        Assert.Equal(0x22, r.Sector[11]);
        Assert.Equal(0x22, r.Sector[500]);
    }

    [Fact]
    public void A_byte_no_read_vouches_for_is_reported_uncertain()
    {
        var a = Sector(0x33);
        var b = Sector(0x33);

        var voter = new C2SectorVoter();
        voter.Add(a, MapWithBad(700));
        voter.Add(b, MapWithBad(700));

        Assert.False(voter.FullyCovered());
        var r = voter.Vote();

        Assert.False(r.Complete);
        Assert.Equal(new[] { 700 }, r.UncertainBytes);
    }

    [Fact]
    public void Disagreement_among_unflagged_reads_goes_to_the_majority()
    {
        // Two reads say 0x01, one says 0x02, none flagged it — so at least one
        // C2 report was wrong. Majority is the best available answer.
        var a = Sector(0); a[50] = 0x01;
        var b = Sector(0); b[50] = 0x01;
        var c = Sector(0); c[50] = 0x02;

        var voter = new C2SectorVoter();
        voter.Add(a, C2ErrorMap.None());
        voter.Add(b, C2ErrorMap.None());
        voter.Add(c, C2ErrorMap.None());

        var r = voter.Vote();

        Assert.Equal(0x01, r.Sector[50]);
        Assert.True(r.Complete);
        Assert.True(r.BytesFromVoting > 0);
    }

    [Fact]
    public void A_tied_vote_is_recorded_as_uncertain_rather_than_guessed()
    {
        var a = Sector(0); a[50] = 0x01;
        var b = Sector(0); b[50] = 0x02;

        var voter = new C2SectorVoter();
        voter.Add(a, C2ErrorMap.None());
        voter.Add(b, C2ErrorMap.None());

        var r = voter.Vote();

        Assert.Contains(50, r.UncertainBytes);
        Assert.False(r.Complete);
    }

    [Fact]
    public void Full_coverage_is_detected_so_reading_can_stop()
    {
        var voter = new C2SectorVoter();
        voter.Add(Sector(1), MapWithBad(0));
        Assert.False(voter.FullyCovered());

        voter.Add(Sector(1), MapWithBad(1));
        Assert.True(voter.FullyCovered());
    }

    [Fact]
    public void Voting_with_no_reads_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => new C2SectorVoter().Vote());
    }

    [Fact]
    public void A_wrong_sized_sector_is_refused()
    {
        var voter = new C2SectorVoter();
        Assert.Throws<ArgumentException>(() => voter.Add(new byte[2048], C2ErrorMap.None()));
    }
}