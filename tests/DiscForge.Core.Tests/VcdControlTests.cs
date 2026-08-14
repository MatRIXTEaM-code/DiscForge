// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Vcd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the Video CD / Super Video CD control-file layer (INFO + ENTRIES).
/// Like NRG before a real sample, these are validated by round trip — the writer
/// and reader must agree on the container — plus a few spec anchors: the ASCII
/// magic, the version byte, and that the entry-point addresses come back as the
/// BCD-coded MSF values they went in as (the classic place a binary-coded-decimal
/// field turns 30 into 48 if decoded as plain hex).
/// </summary>
public class VcdControlTests
{
    [Fact]
    public void An_info_header_round_trips()
    {
        var info = new VcdInfo
        {
            Kind = VcdKind.Vcd,
            Version = 2,
            AlbumId = "MY HOLIDAY 1999",
            VolumeCount = 3,
            VolumeNumber = 2,
        };

        var read = VcdControl.ReadInfo(VcdControl.WriteInfo(info));

        Assert.Equal(VcdKind.Vcd, read.Kind);
        Assert.Equal(2, read.Version);
        Assert.Equal("MY HOLIDAY 1999", read.AlbumId);
        Assert.Equal(3, read.VolumeCount);
        Assert.Equal(2, read.VolumeNumber);
    }

    [Fact]
    public void An_info_file_carries_the_expected_ascii_signature()
    {
        var vcd = VcdControl.WriteInfo(new VcdInfo { Kind = VcdKind.Vcd });
        var svcd = VcdControl.WriteInfo(new VcdInfo { Kind = VcdKind.Svcd });

        Assert.Equal("VIDEO_CD", Encoding.ASCII.GetString(vcd, 0, 8));
        Assert.Equal("SUPERVCD", Encoding.ASCII.GetString(svcd, 0, 8));
    }

    [Fact]
    public void An_svcd_info_is_recognised_as_svcd()
    {
        var bytes = VcdControl.WriteInfo(new VcdInfo { Kind = VcdKind.Svcd, AlbumId = "CONCERT" });
        Assert.Equal(VcdKind.Svcd, VcdControl.ReadInfo(bytes).Kind);
    }

    [Fact]
    public void A_non_vcd_info_is_refused()
    {
        var junk = new byte[VcdControl.SectorSize];
        Encoding.ASCII.GetBytes("NOTAVCD!").CopyTo(junk, 0);
        Assert.Throws<VcdFormatException>(() => VcdControl.ReadInfo(junk));
    }

    [Fact]
    public void An_entry_table_round_trips_with_its_msf_addresses()
    {
        var entries = new VcdEntries
        {
            Kind = VcdKind.Vcd,
            Version = 2,
            Entries = new[]
            {
                new VcdEntry { TrackNumber = 2, Minute = 0, Second = 4, Frame = 0 },
                new VcdEntry { TrackNumber = 3, Minute = 12, Second = 30, Frame = 74 },
                new VcdEntry { TrackNumber = 4, Minute = 59, Second = 59, Frame = 0 },
            },
        };

        var read = VcdControl.ReadEntries(VcdControl.WriteEntries(entries));

        Assert.Equal(3, read.Entries.Count);
        Assert.Equal((2, 0, 4, 0),
            (read.Entries[0].TrackNumber, read.Entries[0].Minute, read.Entries[0].Second, read.Entries[0].Frame));
        Assert.Equal((3, 12, 30, 74),
            (read.Entries[1].TrackNumber, read.Entries[1].Minute, read.Entries[1].Second, read.Entries[1].Frame));
        Assert.Equal((4, 59, 59, 0),
            (read.Entries[2].TrackNumber, read.Entries[2].Minute, read.Entries[2].Second, read.Entries[2].Frame));
    }

    [Fact]
    public void Msf_values_are_stored_as_bcd_not_raw_hex()
    {
        // Second 30 as BCD is 0x30; as raw hex it would be 0x1E. Reading it back as
        // 30 (not 48) proves the BCD coding on both sides.
        var entries = new VcdEntries
        {
            Kind = VcdKind.Vcd,
            Entries = new[] { new VcdEntry { TrackNumber = 10, Minute = 22, Second = 30, Frame = 44 } },
        };
        var bytes = VcdControl.WriteEntries(entries);

        // Entry begins at 0x0C: track, min, sec, frame — each a BCD byte.
        Assert.Equal(0x10, bytes[0x0C]);   // track 10
        Assert.Equal(0x22, bytes[0x0D]);   // minute 22
        Assert.Equal(0x30, bytes[0x0E]);   // second 30
        Assert.Equal(0x44, bytes[0x0F]);   // frame 44

        var read = VcdControl.ReadEntries(bytes);
        Assert.Equal(30, read.Entries[0].Second);
    }

    [Fact]
    public void The_entry_count_is_big_endian()
    {
        // 300 entries (0x012C) would read as 0x2C01 = 11265 little-endian.
        var many = Enumerable.Range(0, 300)
            .Select(i => new VcdEntry { TrackNumber = 2, Minute = i / 60, Second = i % 60, Frame = 0 })
            .ToList();
        var bytes = VcdControl.WriteEntries(new VcdEntries { Kind = VcdKind.Vcd, Entries = many });

        Assert.Equal(0x01, bytes[0x0A]);
        Assert.Equal(0x2C, bytes[0x0B]);
        Assert.Equal(300, VcdControl.ReadEntries(bytes).Entries.Count);
    }

    [Fact]
    public void An_svcd_entry_table_uses_the_svcd_magic()
    {
        var bytes = VcdControl.WriteEntries(new VcdEntries
        {
            Kind = VcdKind.Svcd,
            Entries = new[] { new VcdEntry { TrackNumber = 2, Minute = 0, Second = 0, Frame = 0 } },
        });
        Assert.Equal("ENTRYSVD", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Equal(VcdKind.Svcd, VcdControl.ReadEntries(bytes).Kind);
    }

    [Fact]
    public void An_out_of_range_frame_is_refused()
    {
        var entries = new VcdEntries
        {
            Kind = VcdKind.Vcd,
            Entries = new[] { new VcdEntry { TrackNumber = 2, Minute = 0, Second = 0, Frame = 75 } },
        };
        Assert.Throws<ArgumentException>(() => VcdControl.WriteEntries(entries));
    }

    [Fact]
    public void An_empty_entry_table_is_refused()
    {
        Assert.Throws<ArgumentException>(() => VcdControl.WriteEntries(
            new VcdEntries { Kind = VcdKind.Vcd, Entries = Array.Empty<VcdEntry>() }));
    }
}
