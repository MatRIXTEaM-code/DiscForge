// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Rom;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the Famicom Disk System reader: a raw disk side and the same side behind an fwNES header both
/// parse to the same identity and file table; the file blocks are walked (0x03 header then 0x04 data) so
/// each file's name/type/address/size is recovered; and a buffer without the "*NINTENDO-HVC*" stamp is
/// rejected rather than misread.
/// </summary>
public class FdsImageTests
{
    private static byte[] Side()
    {
        var b = new byte[Fds.SideBytes];
        b[0] = 0x01;
        "*NINTENDO-HVC*"u8.CopyTo(b.AsSpan(1));
        b[0x0F] = 0x49;
        "ZEL"u8.CopyTo(b.AsSpan(0x10));
        int pos = 56;
        b[pos] = 0x02; b[pos + 1] = 2; pos += 2;

        void File(int i, string name, int addr, int size, byte type)
        {
            b[pos] = 0x03; b[pos + 1] = (byte)i; b[pos + 2] = (byte)i;
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(b, pos + 3);
            b[pos + 11] = (byte)(addr & 0xFF); b[pos + 12] = (byte)(addr >> 8);
            b[pos + 13] = (byte)(size & 0xFF); b[pos + 14] = (byte)(size >> 8);
            b[pos + 15] = type;
            pos += 16;
            b[pos] = 0x04; pos += 1 + size;
        }
        File(0, "KYODAKU-", 0x2000, 32, 0);
        File(1, "BG-CHR--", 0x0000, 16, 1);
        return b;
    }

    private static byte[] FwNes(byte[] side)
    {
        var wrapped = new byte[16 + side.Length];
        "FDS"u8.CopyTo(wrapped); wrapped[3] = 0x1A; wrapped[4] = 1;
        side.CopyTo(wrapped, 16);
        return wrapped;
    }

    [Fact]
    public void A_raw_side_parses_its_identity_and_file_table()
    {
        var img = Fds.Read(Side());
        Assert.False(img.HadFwNesHeader);
        Assert.Equal(1, img.SideCount);

        var s = img.Sides[0];
        Assert.Equal("ZEL", s.GameName);
        Assert.Equal("0x49", s.MakerCode);
        Assert.Equal(2, s.FileCount);
        Assert.Equal(2, s.Files.Count);

        Assert.Equal("KYODAKU-", s.Files[0].Name);
        Assert.Equal("PRG", s.Files[0].Kind);
        Assert.Equal(0x2000, s.Files[0].Address);
        Assert.Equal(32, s.Files[0].Size);

        Assert.Equal("CHR", s.Files[1].Kind);
        Assert.Equal(16, s.Files[1].Size);
    }

    [Fact]
    public void The_fwNES_header_is_detected_and_skipped()
    {
        var img = Fds.Read(FwNes(Side()));
        Assert.True(img.HadFwNesHeader);
        Assert.Equal("ZEL", img.Sides[0].GameName);
        Assert.Equal(2, img.Sides[0].Files.Count);
    }

    [Fact]
    public void IsFds_accepts_both_forms_and_rejects_others()
    {
        Assert.True(Fds.IsFds(Side()));
        Assert.True(Fds.IsFds(FwNes(Side())));
        Assert.False(Fds.IsFds(new byte[100]));
    }

    [Fact]
    public void A_buffer_without_the_stamp_is_rejected()
    {
        Assert.Throws<FdsFormatException>(() => Fds.Read(new byte[Fds.SideBytes]));
    }
}
