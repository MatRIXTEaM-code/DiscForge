// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.GameCube;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>Tests for the GameCube-specific red/blue/green inner-ring codes, distinct from the generic
/// IFPI mastering/mould SID codes in Forensics.RingCodeParser.</summary>
public class GcRingCodeTests
{
    [Fact]
    public void Red_code_decodes_the_documented_Wind_Waker_example()
    {
        // Community-sourced example: C03B2606 -> 2003-02-26 (C=USA-letter flag, 03=2003, B=February, 26=day).
        var ring = GcRingCodeParser.Parse("C03B2606", null, null);
        Assert.Equal(new DateOnly(2003, 2, 26), ring.ManufactureDate);
        Assert.True(ring.ManufacturedInUsa);
        Assert.Equal("06", ring.RedTrailer);
    }

    [Fact]
    public void Red_code_with_a_digit_flag_reads_as_manufactured_in_Japan()
    {
        var ring = GcRingCodeParser.Parse("103A1501", null, null);
        Assert.False(ring.ManufacturedInUsa);
        Assert.Equal(new DateOnly(2003, 1, 15), ring.ManufactureDate);
    }

    [Fact]
    public void Blue_code_decodes_the_console_game_code_disc_and_revision()
    {
        var ring = GcRingCodeParser.Parse(null, "DOL-GALE-0-00 USA", null);
        Assert.Equal("DOL", ring.ConsoleCode);
        Assert.Equal("GALE", ring.GameCode);
        Assert.Equal(0, ring.DiscNumber);
        Assert.Equal(0, ring.RomRevision);
        Assert.Equal("USA", ring.ManufacturingRegionName);
    }

    [Fact]
    public void Blue_code_without_a_trailing_region_still_parses()
    {
        var ring = GcRingCodeParser.Parse(null, "DOL-GALJ-0-01", null);
        Assert.Equal("GALJ", ring.GameCode);
        Assert.Equal(1, ring.RomRevision);
        Assert.Null(ring.ManufacturingRegionName);
    }

    [Fact]
    public void Green_code_S0_reads_as_standard_and_anything_else_as_an_anomaly()
    {
        Assert.True(GcRingCodeParser.Parse(null, null, "S0").GreenIsStandard);
        Assert.False(GcRingCodeParser.Parse(null, null, "S1").GreenIsStandard);
    }

    [Fact]
    public void A_malformed_ring_is_left_unparsed_rather_than_guessed_at()
    {
        var ring = GcRingCodeParser.Parse("not a code", "also not one", null);
        Assert.Null(ring.ManufactureDate);
        Assert.Null(ring.GameCode);
        Assert.True(ring.HasAny);   // raw text was still supplied
    }

    [Fact]
    public void CrossCheck_confirms_a_matching_game_code_disc_number_and_revision()
    {
        var ring = GcRingCodeParser.Parse(null, "DOL-GALE-0-02 USA", "S0");
        var result = GcRingCodeCheck.CrossCheck(ring, discHeaderGameCode: "GALE", expectedDiscNumber: 0, expectedRomRevision: 2);
        Assert.True(result.GameCodeMatches);
        Assert.True(result.DiscNumberMatches);
        Assert.True(result.RevisionMatches);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void CrossCheck_flags_a_game_code_mismatch_against_the_disc_header()
    {
        var ring = GcRingCodeParser.Parse(null, "DOL-GALE-0-00 USA", null);
        var result = GcRingCodeCheck.CrossCheck(ring, discHeaderGameCode: "GALJ");
        Assert.False(result.GameCodeMatches);
        Assert.Contains(result.Issues, i => i.Contains("GALE") && i.Contains("GALJ"));
    }

    [Fact]
    public void CrossCheck_flags_a_revision_mismatch_against_a_caller_supplied_expectation()
    {
        var ring = GcRingCodeParser.Parse(null, "DOL-GALE-0-00 USA", null);
        var result = GcRingCodeCheck.CrossCheck(ring, discHeaderGameCode: "GALE", expectedRomRevision: 1);
        Assert.False(result.RevisionMatches);
        Assert.Contains(result.Issues, i => i.Contains("revision"));
    }

    [Fact]
    public void CrossCheck_flags_a_non_standard_green_code_as_an_anomaly()
    {
        var ring = GcRingCodeParser.Parse(null, "DOL-GALE-0-00 USA", "S1");
        var result = GcRingCodeCheck.CrossCheck(ring, discHeaderGameCode: "GALE");
        Assert.Contains(result.Issues, i => i.Contains("S1"));
    }
}
