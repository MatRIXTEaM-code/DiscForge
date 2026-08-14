// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Vcd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Video CD / Super Video CD control files hand-assembled to their documented
/// layout — NOT produced by <see cref="VcdControl.WriteInfo"/> / WriteEntries. The
/// existing VCD tests round-trip DiscForge's own writer↔reader; this pins the
/// <b>reader</b> to the real byte offsets (ASCII magic, big-endian volume/entry
/// counts, BCD track/MSF) so a shared writer+reader offset bug would surface here.
/// </summary>
public class VcdSpecFixtureTests
{
    private static byte Bcd(int v) => (byte)(((v / 10) << 4) | (v % 10));

    [Fact]
    public void A_hand_assembled_info_reads_from_the_documented_offsets()
    {
        var b = new byte[2048];
        Encoding.ASCII.GetBytes("VIDEO_CD").CopyTo(b, 0x00);
        b[0x08] = 2;                                  // version
        b[0x09] = 1;                                  // system profile tag
        Encoding.ASCII.GetBytes("DISCFORGE VCD".PadRight(16)).CopyTo(b, 0x0A);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x1A), 3);   // volume count (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x1C), 2);   // volume number (big-endian)

        var info = VcdControl.ReadInfo(b);
        Assert.Equal(VcdKind.Vcd, info.Kind);
        Assert.Equal(2, info.Version);
        Assert.Equal("DISCFORGE VCD", info.AlbumId);
        Assert.Equal(3, info.VolumeCount);
        Assert.Equal(2, info.VolumeNumber);
    }

    [Fact]
    public void The_svcd_magic_is_recognised()
    {
        var b = new byte[2048];
        Encoding.ASCII.GetBytes("SUPERVCD").CopyTo(b, 0x00);
        b[0x08] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x1A), 1);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x1C), 1);
        Assert.Equal(VcdKind.Svcd, VcdControl.ReadInfo(b).Kind);
    }

    [Fact]
    public void A_hand_assembled_entries_table_reads_bcd_track_and_msf()
    {
        var b = new byte[2048];
        Encoding.ASCII.GetBytes("ENTRYVCD").CopyTo(b, 0x00);
        b[0x08] = 2;
        b[0x09] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x0A), 2);   // entry count (big-endian)

        // Entry 1: track 1 at 00:02:00. Entry 2: track 12 at 03:20:37 (BCD throughout).
        int p = 0x0C;
        b[p] = Bcd(1); b[p + 1] = Bcd(0); b[p + 2] = Bcd(2); b[p + 3] = Bcd(0);
        p += 4;
        b[p] = Bcd(12); b[p + 1] = Bcd(3); b[p + 2] = Bcd(20); b[p + 3] = Bcd(37);

        var entries = VcdControl.ReadEntries(b);
        Assert.Equal(VcdKind.Vcd, entries.Kind);
        Assert.Equal(2, entries.Entries.Count);

        Assert.Equal(1, entries.Entries[0].TrackNumber);
        Assert.Equal((0, 2, 0), (entries.Entries[0].Minute, entries.Entries[0].Second, entries.Entries[0].Frame));

        Assert.Equal(12, entries.Entries[1].TrackNumber);   // proves BCD, not raw hex (0x12 != 12)
        Assert.Equal((3, 20, 37), (entries.Entries[1].Minute, entries.Entries[1].Second, entries.Entries[1].Frame));
    }
}
