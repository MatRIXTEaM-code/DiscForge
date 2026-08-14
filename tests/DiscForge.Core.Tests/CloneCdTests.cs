// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Convert;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for reading CloneCD images: parsing the .ccd control file and pulling a
/// track's raw sectors out of the accompanying .img / .sub sidecars. The .ccd is
/// written by hand so the layout under test is explicit — two tracks at LBA 0 and
/// 10, lead-out at LBA 25.
/// </summary>
public class CloneCdTests
{
    // Data track 1 at LBA 0 (10 sectors), audio track 2 at LBA 10 (15 sectors),
    // lead-out at LBA 25.
    private const string Ccd =
        "[CloneCD]\r\nVersion=3\r\n\r\n" +
        "[Disc]\r\nTocEntries=5\r\nSessions=1\r\n\r\n" +
        "[Session 1]\r\nPreGapMode=2\r\nPreGapSubC=0\r\n\r\n" +
        "[Entry 0]\r\nSession=1\r\nPoint=0xa0\r\nADR=0x01\r\nControl=0x04\r\nPMin=1\r\nPSec=0\r\nPFrame=0\r\nPLBA=0\r\n\r\n" +
        "[Entry 1]\r\nSession=1\r\nPoint=0xa1\r\nADR=0x01\r\nControl=0x04\r\nPMin=2\r\nPSec=0\r\nPFrame=0\r\nPLBA=0\r\n\r\n" +
        "[Entry 2]\r\nSession=1\r\nPoint=0xa2\r\nADR=0x01\r\nControl=0x04\r\nPMin=0\r\nPSec=0\r\nPFrame=0\r\nPLBA=25\r\n\r\n" +
        "[Entry 3]\r\nSession=1\r\nPoint=0x01\r\nADR=0x01\r\nControl=0x04\r\nPMin=0\r\nPSec=0\r\nPFrame=0\r\nPLBA=0\r\n\r\n" +
        "[Entry 4]\r\nSession=1\r\nPoint=0x02\r\nADR=0x01\r\nControl=0x00\r\nPMin=0\r\nPSec=0\r\nPFrame=0\r\nPLBA=10\r\n\r\n";

    private static byte[] BuildImg(int sectors, int sectorBytes)
    {
        // Every byte of the sector at LBA k is (byte)k, so extracted data is
        // trivially identifiable by which track it came from.
        var img = new byte[sectors * sectorBytes];
        for (int lba = 0; lba < sectors; lba++)
            for (int i = 0; i < sectorBytes; i++)
                img[lba * sectorBytes + i] = (byte)lba;
        return img;
    }

    // ---- parsing -------------------------------------------------------------

    [Fact]
    public void Parses_sessions_entries_and_tracks()
    {
        var toc = CloneCdReader.Parse(Ccd);

        Assert.Equal(3, toc.Version);
        Assert.Equal(1, toc.SessionCount);
        Assert.Equal(5, toc.Entries.Count);
        Assert.Equal(2, toc.Tracks.Count);

        Assert.Equal(1, toc.FirstTrack);
        Assert.Equal(2, toc.LastTrack);
        Assert.Equal(25, toc.LeadOutLba);

        Assert.Equal(0, toc.Tracks[0].StartLba);
        Assert.True(toc.Tracks[0].IsData);
        Assert.Equal(10, toc.Tracks[1].StartLba);
        Assert.False(toc.Tracks[1].IsData);   // audio
    }

    [Fact]
    public void Track_sector_count_spans_to_next_track_or_lead_out()
    {
        var toc = CloneCdReader.Parse(Ccd);
        Assert.Equal(10, CloneCdReader.TrackSectorCount(toc, toc.Tracks[0]));
        Assert.Equal(15, CloneCdReader.TrackSectorCount(toc, toc.Tracks[1]));
    }

    [Fact]
    public void Not_a_ccd_is_rejected()
    {
        Assert.Throws<CloneCdReader.CcdFormatException>(() => CloneCdReader.Parse("just some text"));
    }

    // ---- .img extraction -----------------------------------------------------

    [Fact]
    public void Extracts_a_tracks_raw_sectors_from_the_img()
    {
        var toc = CloneCdReader.Parse(Ccd);
        var img = BuildImg(25, CloneCdReader.ImgSectorBytes);

        using var src = new MemoryStream(img);
        using var track2 = new MemoryStream();
        long n = CloneCdReader.ExtractTrack(toc, toc.Tracks[1], src, track2);

        Assert.Equal(15L * CloneCdReader.ImgSectorBytes, n);
        var bytes = track2.ToArray();
        // Track 2 starts at LBA 10; its first sector is all 0x0A, last is 0x18 (24).
        Assert.Equal(10, bytes[0]);
        Assert.Equal(24, bytes[^1]);
        // And it equals the slice of the .img it was cut from.
        var expected = img.AsSpan(10 * CloneCdReader.ImgSectorBytes,
                                  15 * CloneCdReader.ImgSectorBytes).ToArray();
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Extracts_the_first_track_from_lba_zero()
    {
        var toc = CloneCdReader.Parse(Ccd);
        var img = BuildImg(25, CloneCdReader.ImgSectorBytes);

        using var src = new MemoryStream(img);
        using var track1 = new MemoryStream();
        long n = CloneCdReader.ExtractTrack(toc, toc.Tracks[0], src, track1);

        Assert.Equal(10L * CloneCdReader.ImgSectorBytes, n);
        Assert.Equal(0, track1.ToArray()[0]);
    }

    [Fact]
    public void A_short_img_is_rejected_not_misread()
    {
        var toc = CloneCdReader.Parse(Ccd);
        // Only 5 sectors, but the TOC needs 25.
        var img = BuildImg(5, CloneCdReader.ImgSectorBytes);

        using var src = new MemoryStream(img);
        using var dst = new MemoryStream();
        Assert.Throws<CloneCdReader.CcdFormatException>(
            () => CloneCdReader.ExtractTrack(toc, toc.Tracks[1], src, dst));
    }

    // ---- .sub reading --------------------------------------------------------

    [Fact]
    public void Reads_a_tracks_raw_subchannel_from_the_sub()
    {
        var toc = CloneCdReader.Parse(Ccd);
        var sub = BuildImg(25, CloneCdReader.SubSectorBytes);

        using var src = new MemoryStream(sub);
        using var dst = new MemoryStream();
        long n = CloneCdReader.ReadSubchannel(toc, toc.Tracks[1], src, dst);

        Assert.Equal(15L * CloneCdReader.SubSectorBytes, n);
        var expected = sub.AsSpan(10 * CloneCdReader.SubSectorBytes,
                                  15 * CloneCdReader.SubSectorBytes).ToArray();
        Assert.Equal(expected, dst.ToArray());
    }
}
