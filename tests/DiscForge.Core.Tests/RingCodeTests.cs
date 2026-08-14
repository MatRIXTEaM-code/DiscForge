using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class RingCodeTests
{
    [Fact]
    public void A_runout_string_splits_into_matrix_and_both_sid_codes()
    {
        var ring = RingCodeParser.Parse("SLES-12345 A2 IFPI L553 IFPI 94D7");

        Assert.Equal("SLES-12345 A2", ring.Matrix);
        Assert.NotNull(ring.MasteringSid);
        Assert.Equal("L553", ring.MasteringSid!.Code);
        Assert.True(ring.MasteringSid.IsMastering);
        Assert.NotNull(ring.MouldSid);
        Assert.Equal("94D7", ring.MouldSid!.Code);
        Assert.False(ring.MouldSid.IsMastering);
    }

    [Fact]
    public void A_mastering_sid_starts_with_L_a_mould_sid_does_not()
    {
        Assert.True(RingCodeParser.ParseSid("IFPI L123")!.IsMastering);
        Assert.False(RingCodeParser.ParseSid("IFPI 94D7")!.IsMastering);
        Assert.True(RingCodeParser.ParseSid("L123")!.IsMastering);   // bare token
    }

    [Fact]
    public void Sid_validity_is_checked_per_kind()
    {
        Assert.True(RingCodeParser.IsValidSid("L553", mastering: true));
        Assert.False(RingCodeParser.IsValidSid("94D7", mastering: true));    // no leading L
        Assert.True(RingCodeParser.IsValidSid("94D7", mastering: false));
        Assert.False(RingCodeParser.IsValidSid("L!", mastering: true));      // too short / bad chars
    }

    [Fact]
    public void A_ring_with_no_ifpi_codes_still_captures_the_matrix()
    {
        var ring = RingCodeParser.Parse("MY-GAME-001 MASTERED BY XYZ");
        Assert.Equal("MY-GAME-001 MASTERED BY XYZ", ring.Matrix);
        Assert.Null(ring.MasteringSid);
        Assert.Null(ring.MouldSid);
        Assert.True(ring.HasAny);
    }

    [Fact]
    public void Discs_from_the_same_plant_group_by_mould_sid()
    {
        var records = new[]
        {
            Record("aaa", "GAME EU", "IFPI L553 IFPI 94D7"),
            Record("bbb", "GAME US", "IFPI L888 IFPI 94D7"),   // same plant (94D7), different master
            Record("ccc", "OTHER",  "IFPI L100 IFPI 55A1"),    // a different plant
        };

        var plants = RingCodeParser.GroupByPlant(records);
        var shared = Assert.Single(plants, g => g.Key == "94D7");
        Assert.Equal(2, shared.Members.Count);
        Assert.Contains("GAME EU", shared.Members);
        Assert.Contains("GAME US", shared.Members);
    }

    [Fact]
    public void Discs_from_the_same_master_group_by_mastering_sid()
    {
        var records = new[]
        {
            Record("aaa", "PRESS 1", "IFPI L553 IFPI 94D7"),
            Record("bbb", "PRESS 2", "IFPI L553 IFPI 55A1"),   // same master (L553), different plant
        };

        var masters = RingCodeParser.GroupByMaster(records);
        var shared = Assert.Single(masters, g => g.Key == "L553");
        Assert.Equal(2, shared.Members.Count);
    }

    [Fact]
    public void A_record_links_a_ring_code_to_a_genome()
    {
        var rec = Record("genome-7a48e62c", "Ridge Racer", "SLES-00518 IFPI L041 IFPI 2U31");
        Assert.Equal("genome-7a48e62c", rec.GenomeId);
        Assert.Equal("2U31", rec.Ring.MouldSid!.Code);
        Assert.Equal("SLES-00518", rec.Ring.Matrix);
    }

    private static RingCodeRecord Record(string genome, string vol, string runout) => new()
    {
        GenomeId = genome,
        VolumeId = vol,
        Ring = RingCodeParser.Parse(runout),
    };
}
