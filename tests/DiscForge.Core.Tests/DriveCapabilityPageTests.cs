// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Media;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Bit-position tests for MODE SENSE page 2Ah.
///
/// The C2 test earns its keep: the flag was first implemented at bit 6 and that
/// was wrong — it is bit 4. A drive that supports C2 error pointers would have
/// been reported as not supporting them, and since C2 is the prerequisite for
/// any error recovery beyond the drive's own, the whole feature would have
/// looked impossible on hardware that could do it perfectly well.
/// </summary>
public class DriveCapabilityPageTests
{
    /// <summary>
    /// A MODE SENSE(10) response: 8-byte header (with a block descriptor length
    /// of zero, as MMC devices return), then the page itself.
    /// </summary>
    private static byte[] Response(byte read2 = 0, byte write3 = 0, byte b4 = 0,
                                   byte b5 = 0, byte b6 = 0,
                                   int bufferKb = 0, int maxRead = 0, int currentRead = 0,
                                   byte pageLength = 0x1C)
    {
        var r = new byte[8 + 2 + pageLength];
        r[0] = 0x00; r[1] = (byte)(r.Length - 2);   // mode data length
        r[6] = 0x00; r[7] = 0x00;                   // block descriptor length: none

        int p = 8;
        r[p + 0] = 0x2A;
        r[p + 1] = pageLength;
        r[p + 2] = read2;
        r[p + 3] = write3;
        r[p + 4] = b4;
        r[p + 5] = b5;
        r[p + 6] = b6;

        r[p + 8] = (byte)(maxRead >> 8); r[p + 9] = (byte)maxRead;
        r[p + 12] = (byte)(bufferKb >> 8); r[p + 13] = (byte)bufferKb;
        r[p + 14] = (byte)(currentRead >> 8); r[p + 15] = (byte)currentRead;
        return r;
    }

    [Fact]
    public void C2_pointers_are_bit_4_of_byte_5()
    {
        // 0x10 set: supported. This is the bit that was wrong once.
        var withC2 = DriveCapabilityPageParser.Parse(Response(b5: 0x10));
        Assert.True(withC2!.C2Pointers);

        var withoutC2 = DriveCapabilityPageParser.Parse(Response(b5: 0x00));
        Assert.False(withoutC2!.C2Pointers);
    }

    [Fact]
    public void C2_is_not_confused_with_its_neighbours()
    {
        // Setting only bit 6 (UPC) must NOT read as C2, and vice versa. The
        // original bug was exactly this confusion.
        var upcOnly = DriveCapabilityPageParser.Parse(Response(b5: 0x40));
        Assert.False(upcOnly!.C2Pointers);
        Assert.True(upcOnly.ReadsUpc);

        var c2Only = DriveCapabilityPageParser.Parse(Response(b5: 0x10));
        Assert.True(c2Only!.C2Pointers);
        Assert.False(c2Only.ReadsUpc);
    }

    [Fact]
    public void Byte_5_flags_decode_independently()
    {
        var all = DriveCapabilityPageParser.Parse(Response(b5: 0xFF))!;
        Assert.True(all.CddaCommands);
        Assert.True(all.CddaAccurateStream);
        Assert.True(all.SubchannelRw);
        Assert.True(all.SubchannelRwCorrected);
        Assert.True(all.C2Pointers);
        Assert.True(all.ReadsIsrc);
        Assert.True(all.ReadsUpc);
        Assert.True(all.ReadsBarcode);

        var none = DriveCapabilityPageParser.Parse(Response(b5: 0x00))!;
        Assert.False(none.CddaCommands);
        Assert.False(none.CddaAccurateStream);
        Assert.False(none.C2Pointers);
        Assert.False(none.ReadsIsrc);
    }

    [Fact]
    public void Accurate_stream_is_bit_1()
    {
        // Without this, ripping audio needs jitter correction; with it, the
        // drive returns audio from where it was asked. Worth being sure of.
        var page = DriveCapabilityPageParser.Parse(Response(b5: 0x02))!;
        Assert.True(page.CddaAccurateStream);
        Assert.False(page.CddaCommands);
    }

    [Fact]
    public void Read_and_write_capability_bytes_decode()
    {
        var page = DriveCapabilityPageParser.Parse(
            Response(read2: 0x03, write3: 0x03))!;

        Assert.True(page.ReadsCdR);
        Assert.True(page.ReadsCdRw);
        Assert.False(page.ReadsDvdRom);
        Assert.True(page.WritesCdR);
        Assert.True(page.WritesCdRw);
        Assert.False(page.WritesDvdR);
    }

    [Fact]
    public void Mode_2_form_support_decodes_from_byte_4()
    {
        var page = DriveCapabilityPageParser.Parse(Response(b4: 0x30))!;
        Assert.True(page.Mode2Form1);
        Assert.True(page.Mode2Form2);
        Assert.False(page.MultiSession);
    }

    [Fact]
    public void Speeds_and_buffer_are_read_as_big_endian_words()
    {
        var page = DriveCapabilityPageParser.Parse(
            Response(bufferKb: 2048, maxRead: 4234, currentRead: 1764))!;

        Assert.Equal(2048, page.BufferSizeKb);
        Assert.Equal(4234, page.MaxReadSpeedKbs);
        Assert.Equal(1764, page.CurrentReadSpeedKbs);
    }

    [Fact]
    public void Speed_conversion_matches_the_cd_reference_rate()
    {
        // 176.4 KB/s is 1x for CD; 4234 KB/s is a 24x drive.
        Assert.Equal(1.0, DriveCapabilityPage.ToCdX(176), 1);
        Assert.Equal(24.0, DriveCapabilityPage.ToCdX(4234), 0);
    }

    [Fact]
    public void Loading_mechanism_comes_from_the_top_bits_of_byte_6()
    {
        var tray = DriveCapabilityPageParser.Parse(Response(b6: 0x20))!;
        Assert.Equal(LoadingMechanism.Tray, tray.Loading);

        var caddy = DriveCapabilityPageParser.Parse(Response(b6: 0x00))!;
        Assert.Equal(LoadingMechanism.Caddy, caddy.Loading);
    }

    [Fact]
    public void Block_descriptors_shift_where_the_page_starts()
    {
        // MMC devices normally return none, but the header declares the length
        // and the page offset must be computed from it rather than assumed.
        var r = Response(b5: 0x10);
        var withDescriptor = new byte[r.Length + 8];
        r.AsSpan(0, 8).CopyTo(withDescriptor);
        withDescriptor[6] = 0x00; withDescriptor[7] = 0x08;      // 8-byte descriptor
        r.AsSpan(8).CopyTo(withDescriptor.AsSpan(16));

        var page = DriveCapabilityPageParser.Parse(withDescriptor);
        Assert.NotNull(page);
        Assert.True(page!.C2Pointers);
    }

    [Fact]
    public void A_response_for_a_different_page_is_rejected()
    {
        var r = Response(b5: 0x10);
        r[8] = 0x01;                       // not page 2Ah
        Assert.Null(DriveCapabilityPageParser.Parse(r));
    }

    [Fact]
    public void A_truncated_response_is_rejected_rather_than_misread()
    {
        Assert.Null(DriveCapabilityPageParser.Parse(new byte[10]));
    }
}