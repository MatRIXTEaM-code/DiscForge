// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Dumping;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// DicLogParser is a best-effort importer for DiscImageCreator's never-formally-specified .log format, so
/// these tests exercise it against representative excerpts of the fields DIC actually emits (version, drive
/// identity, per-track CRC32/MD5/SHA1, and both the "[NO ERROR]" and C2-error-LBA cases) rather than a full
/// real-world log. Every field is optional by design, so a log missing a section should parse the rest rather
/// than throw — that's checked here too.
/// </summary>
public class DicLogTests
{
    private const string CleanLog = """
        Microsoft Windows [Version 10.0.19045]

        DiscImageCreator 20230518

        Command line: DiscImageCreator.exe cd F 20230518 game.bin 8 /c2 20 /ss

        VendorId : PLEXTOR
        ProductId : DVDR PX-W5224A
        ProductRevisionLevel : 1.07

        Disc Type : CD-ROM XA

        [NO ERROR]

        TotalErrors [Number of Sector(s)] 0

        CRC32 hash:
        	Track 01     : ABCD1234

        MD5 hash:
        	Track 01     : 0123456789abcdef0123456789abcdef

        SHA1 hash:
        	Track 01     : 0123456789abcdef0123456789abcdef01234567
        """;

    private const string ErrorLog = """
        DiscImageCreator 20230518

        VendorId : PLEXTOR
        ProductId : DVDR PX-W5224A

        Disc Type : CD-ROM

        [C2 Error(s)]
        LBA[  12345, 0x00003039]
        LBA[  12346, 0x0000303a]
        LBA[  12345, 0x00003039]

        Number of C2 error(s): 2

        TotalErrors [Number of Sector(s)] 2

        CRC32 hash:
        	Track 01     : DEADBEEF

        MD5 hash:
        	Track 01     : deadbeefdeadbeefdeadbeefdeadbeef

        SHA1 hash:
        	Track 01     : deadbeefdeadbeefdeadbeefdeadbeefdeadbeef
        """;

    [Fact]
    public void ParsesVersionDriveAndMedia()
    {
        var info = DicLogParser.ParseText(CleanLog);
        Assert.Equal("20230518", info.DicVersion);
        Assert.Equal("PLEXTOR", info.DriveVendor);
        Assert.Contains("PX-W5224A", info.DriveProduct);
        Assert.Equal("CD-ROM XA", info.MediaType);
    }

    [Fact]
    public void CleanLogHasNoC2Errors()
    {
        var info = DicLogParser.ParseText(CleanLog);
        Assert.True(info.LooksClean);
        Assert.Empty(info.C2ErrorLbas);
        Assert.Equal(0, info.TotalErrors);
    }

    [Fact]
    public void ParsesPerTrackHashes()
    {
        var info = DicLogParser.ParseText(CleanLog);
        var t1 = Assert.Single(info.Tracks);
        Assert.Equal(1, t1.Track);
        Assert.Equal("ABCD1234", t1.Crc32);
        Assert.Equal("0123456789abcdef0123456789abcdef", t1.Md5);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", t1.Sha1);
    }

    [Fact]
    public void ExtractsAndDeduplicatesC2ErrorLbas()
    {
        var info = DicLogParser.ParseText(ErrorLog);
        Assert.False(info.LooksClean);
        Assert.Equal(new long[] { 12345, 12346 }, info.C2ErrorLbas);
        Assert.Equal(2, info.TotalErrors);
    }

    [Fact]
    public void MissingSectionsLeaveFieldsNullInsteadOfThrowing()
    {
        var info = DicLogParser.ParseText("DiscImageCreator 20230518\nsome unrelated text\n");
        Assert.Equal("20230518", info.DicVersion);
        Assert.Null(info.DriveVendor);
        Assert.Null(info.MediaType);
        Assert.Empty(info.Tracks);
        Assert.True(info.LooksClean);
    }

    [Fact]
    public void EmptyTextParsesToAllNullsRatherThanThrowing()
    {
        var info = DicLogParser.ParseText("");
        Assert.Null(info.DicVersion);
        Assert.Empty(info.Tracks);
        Assert.Empty(info.C2ErrorLbas);
    }
}
