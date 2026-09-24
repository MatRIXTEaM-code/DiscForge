// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Dumping;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// RedumperLogParser reads the observable line shapes of a redumper .log (version line, drive line, write
/// offset, the media-errors block and the dat block). Excerpts here are representative, not a full real log;
/// the point is that each section is picked up, that a later block supersedes an earlier one, and that a log
/// missing a section still parses the rest.
/// </summary>
public class RedumperLogTests
{
    private const string CleanLog = """
        [2026-09-20 14:02:11]
        redumper (build: b752)

        arguments: cd --drive=E: --image-name=game

        drive: PLEXTOR - CD-R PX-760A (revision level: 1.07, vendor specific: 10/18/06 15:00)
        current profile: CD-ROM

        disc write offset: +2

        media errors:
          SCSI: 0
          C2: 0
          Q: 0

        dat:
        <rom name="game (Track 1).bin" size="524463984" crc="1a2b3c4d" md5="0123456789abcdef0123456789abcdef" sha1="0123456789abcdef0123456789abcdef01234567" />
        <rom name="game (Track 2).bin" size="3528000" crc="deadbeef" md5="fedcba9876543210fedcba9876543210" sha1="fedcba9876543210fedcba9876543210fedcba98" />

        CUE [game.cue]:
        """;

    [Fact]
    public void Sniffs_redumper_log_but_not_dic_log()
    {
        Assert.True(RedumperLogParser.LooksLikeRedumperLog(CleanLog));
        Assert.False(RedumperLogParser.LooksLikeRedumperLog("DiscImageCreator 20230518\nVendorId : PLEXTOR\n"));
    }

    [Fact]
    public void Parses_version_drive_offset_errors_and_dat()
    {
        var info = RedumperLogParser.ParseText(CleanLog);
        Assert.Equal("(build: b752)", info.RedumperVersion);
        Assert.Equal("PLEXTOR", info.DriveVendor);
        Assert.Equal("CD-R PX-760A", info.DriveProduct);
        Assert.Equal("1.07", info.DriveRevision);
        Assert.Equal("CD-ROM", info.DiscType);
        Assert.Equal("+2", info.WriteOffset);
        Assert.Equal(0, info.ScsiErrors);
        Assert.Equal(0, info.C2Errors);
        Assert.Equal(0, info.QErrors);
        Assert.True(info.LooksClean);
        Assert.Equal(2, info.Roms.Count);
        Assert.Equal("game (Track 1).bin", info.Roms[0].Name);
        Assert.Equal(524463984, info.Roms[0].Size);
        Assert.Equal("1a2b3c4d", info.Roms[0].Crc32);
        Assert.Equal("fedcba9876543210fedcba9876543210fedcba98", info.Roms[1].Sha1);
    }

    [Fact]
    public void Last_error_block_wins_and_nonzero_is_not_clean()
    {
        const string log = """
            [2026-09-20 14:02:11]
            redumper v2025.03.29 build_481
            initial dump media errors:
              SCSI: 12
              C2: 40
              Q: 3
            media errors:
              SCSI: 0
              C2: 7
              Q: 0
            """;
        var info = RedumperLogParser.ParseText(log);
        Assert.Equal("v2025.03.29 build_481", info.RedumperVersion);
        Assert.Equal(0, info.ScsiErrors);
        Assert.Equal(7, info.C2Errors);
        Assert.False(info.LooksClean);
        Assert.Contains("C2=7", info.Summary());
    }

    [Fact]
    public void Missing_sections_still_parse()
    {
        var info = RedumperLogParser.ParseText("[date]\nredumper (build: b700)\n");
        Assert.Null(info.DriveVendor);
        Assert.Null(info.C2Errors);
        Assert.Empty(info.Roms);
        Assert.Contains("no error summary", info.Summary());
    }
}
