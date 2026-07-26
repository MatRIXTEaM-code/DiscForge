// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Dat;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for Logiqx DAT parsing and dump verification (the redump-DAT check). A
/// small DAT is parsed and files are checked against it: a size+CRC+SHA-1 match is
/// verified, a CRC match with a mismatched SHA-1 is flagged rather than trusted,
/// and an unknown file is reported as not catalogued.
/// </summary>
public class DatFileTests
{
    private const string Dat = """
        <?xml version="1.0"?>
        <datafile>
          <header><name>Test System - Datfile</name></header>
          <game name="Cool Game (USA)">
            <rom name="Cool Game (USA) (Track 1).bin" size="1000" crc="AABBCCDD" md5="11112222333344445555666677778888" sha1="0123456789abcdef0123456789abcdef01234567"/>
            <rom name="Cool Game (USA).cue" size="88" crc="12345678"/>
          </game>
          <game name="Other Game (Europe)">
            <rom name="Other Game (Europe).bin" size="2000" crc="DEADBEEF" sha1="ffffffffffffffffffffffffffffffffffffffff"/>
          </game>
        </datafile>
        """;

    [Fact]
    public void The_dat_parses_its_name_and_roms()
    {
        var dat = DatFile.ParseText(Dat);
        Assert.Equal("Test System - Datfile", dat.Name);
        Assert.Equal(3, dat.Count);
    }

    [Fact]
    public void A_matching_size_crc_and_sha1_is_verified()
    {
        var dat = DatFile.ParseText(Dat);
        var m = dat.Verify(1000, "aabbccdd", "0123456789abcdef0123456789abcdef01234567");

        Assert.True(m.Verified);
        Assert.Equal("Cool Game (USA)", m.Rom!.Game);
        Assert.Contains("Track 1", m.Rom.Name);
    }

    [Fact]
    public void Case_is_ignored_for_hashes()
    {
        var dat = DatFile.ParseText(Dat);
        Assert.True(dat.Verify(2000, "DEADBEEF", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF").Verified);
    }

    [Fact]
    public void A_crc_match_with_a_wrong_sha1_is_flagged_not_verified()
    {
        var dat = DatFile.ParseText(Dat);
        var m = dat.Verify(1000, "aabbccdd", "ffffffffffffffffffffffffffffffffffffffff");

        Assert.False(m.Verified);
        Assert.Contains("CRC-32 matches", m.Reason);
    }

    [Fact]
    public void A_cue_with_no_hashes_verifies_by_crc_and_size()
    {
        var dat = DatFile.ParseText(Dat);
        var m = dat.Verify(88, "12345678");
        Assert.True(m.Verified);
        Assert.EndsWith(".cue", m.Rom!.Name);
    }

    [Fact]
    public void An_unknown_file_is_not_found()
    {
        var dat = DatFile.ParseText(Dat);
        Assert.False(dat.Verify(999, "00000000", "aaaa").Verified);
    }
}
