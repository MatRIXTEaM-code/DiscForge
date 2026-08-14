// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Media;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for DVD Physical Format Information parsing and the dual-layer layer-break computation, built
/// from PFI blocks laid out byte for byte (ECMA-267 offsets) as both a bare block and a full READ DISC
/// STRUCTURE response with the 4-byte MMC header.
/// </summary>
public class DvdPhysicalLayoutTests
{
    private const uint DataStart = 0x30000;

    private static byte[] Pfi(int layers, bool otp, uint dataStart, uint dataEnd, uint layer0End, bool withHeader = false)
    {
        var p = new byte[16];
        p[0] = 0x01;                                              // book type 0 (DVD-ROM), version 1
        p[2] = (byte)((((layers - 1) & 3) << 5) | (otp ? 0x10 : 0) | 0x01);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), dataStart);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), dataEnd);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), layer0End);
        if (!withHeader) return p;
        var wh = new byte[4 + 16];
        wh[0] = 0x08;                                             // MMC 2-byte length + 2 reserved
        p.CopyTo(wh, 4);
        return wh;
    }

    [Fact]
    public void Computes_the_layer_break_of_a_dual_layer_OTP_disc()
    {
        uint lb = 2_000_000, total = 4_000_000;
        var d = DvdPhysicalFormat.Parse(Pfi(2, otp: true, DataStart, DataStart + total - 1, DataStart + lb - 1))!;

        Assert.Equal(2, d.Layers);
        Assert.Equal(DvdTrackPath.Opposite, d.TrackPath);
        Assert.Equal(2_000_000L, d.LayerBreak);
        Assert.Equal(4_000_000L, d.TotalDataSectors);
        Assert.Equal(2_000_000L, d.Layer1Sectors);
        Assert.True(d.IsConsistent());
    }

    [Fact]
    public void Reads_the_same_layout_behind_a_4_byte_MMC_response_header()
    {
        uint lb = 1_913_760, total = 4_267_015;
        var bare = DvdPhysicalFormat.Parse(Pfi(2, true, DataStart, DataStart + total - 1, DataStart + lb - 1, withHeader: false))!;
        var hdr = DvdPhysicalFormat.Parse(Pfi(2, true, DataStart, DataStart + total - 1, DataStart + lb - 1, withHeader: true))!;
        Assert.Equal(bare.LayerBreak, hdr.LayerBreak);
        Assert.Equal(1_913_760L, hdr.LayerBreak);
    }

    [Fact]
    public void A_single_layer_disc_has_no_layer_break()
    {
        var d = DvdPhysicalFormat.Parse(Pfi(1, otp: false, DataStart, DataStart + 2_000_000 - 1, 0))!;
        Assert.Equal(1, d.Layers);
        Assert.Null(d.LayerBreak);
        Assert.Equal(DvdTrackPath.Parallel, d.TrackPath);
    }

    [Fact]
    public void Cross_checks_the_dumped_image_sector_count()
    {
        uint total = 4_000_000;
        var d = DvdPhysicalFormat.Parse(Pfi(2, true, DataStart, DataStart + total - 1, DataStart + 2_000_000 - 1))!;
        Assert.True(d.IsConsistent(4_000_000));
        Assert.False(d.IsConsistent(3_999_999));
        Assert.Contains(d.Verify(3_999_999), w => w.Contains("under-sized"));
    }

    [Fact]
    public void A_dual_layer_disc_missing_its_layer0_end_is_flagged()
    {
        var d = DvdPhysicalFormat.Parse(Pfi(2, true, DataStart, DataStart + 4_000_000 - 1, layer0End: 0))!;
        Assert.Null(d.LayerBreak);
        Assert.Contains(d.Verify(), w => w.Contains("layer break is unknown"));
    }
}
