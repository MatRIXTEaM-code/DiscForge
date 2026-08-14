// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Floppy;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// PC-98 D88 floppy reader — proven by round-trip against a spec-shaped image: a 688-byte
/// header (name, media type, track offset table) and one track of two 256-byte sectors. The
/// reader recovers the disk metadata and the track/sector geometry.
/// </summary>
public class D88ReaderTests
{
    private static byte[] BuildD88()
    {
        int sectorData = 256;                       // N = 1 → 128 << 1
        int track0 = D88Reader.HeaderSize;          // 0x2B0
        int trackBytes = 2 * (16 + sectorData);
        long diskSize = track0 + trackBytes;

        var b = new byte[diskSize];
        Encoding.ASCII.GetBytes("PC98 DISK").CopyTo(b, 0);
        b[0x1A] = 0x10;                              // write protected
        b[0x1B] = 0x20;                             // 2HD
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x1C, 4), (uint)diskSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x20, 4), (uint)track0);   // track 0 offset

        int p = track0;
        for (int r = 1; r <= 2; r++)
        {
            b[p + 0] = 0;                            // C
            b[p + 1] = 0;                            // H
            b[p + 2] = (byte)r;                      // R
            b[p + 3] = 1;                            // N (256 bytes)
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 4, 2), 2);          // sectors in track
            b[p + 6] = 0x00;                        // double density
            b[p + 7] = 0x00;                        // not deleted
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 0x0E, 2), (ushort)sectorData);
            p += 16 + sectorData;
        }
        return b;
    }

    [Fact]
    public void Reads_the_disk_header()
    {
        var d = D88Reader.Parse(BuildD88());
        Assert.Equal("PC98 DISK", d.Name);
        Assert.True(d.WriteProtected);
        Assert.Equal("2HD", d.DiskTypeName);
        Assert.False(d.MoreDisksFollow);
    }

    [Fact]
    public void Reads_the_track_and_sector_geometry()
    {
        var d = D88Reader.Parse(BuildD88());
        Assert.Equal(1, d.TrackCount);
        Assert.Equal(2, d.SectorCount);
        var (track, sectors) = d.Tracks[0];
        Assert.Equal(0, track);
        Assert.Equal(1, sectors[0].Record);
        Assert.Equal(256, sectors[0].SizeBytes);
        Assert.Equal(2, sectors[1].Record);
    }

    [Fact]
    public void Rejects_non_d88_data()
    {
        Assert.False(D88Reader.IsD88(new byte[D88Reader.HeaderSize]));       // media type 0 but size 0 → invalid
        Assert.Throws<InvalidDataException>(() => D88Reader.Parse(new byte[10]));
    }
}
