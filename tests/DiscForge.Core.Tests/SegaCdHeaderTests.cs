// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.SegaCd;
using Xunit;

namespace DiscForge.Core.Tests;

public class SegaCdHeaderTests
{
    private static byte[] BuildHeader(
        string system = "SEGADISCSYSTEM  ",
        string console = "SEGA MEGA DRIVE ",
        string copyright = "(C)SEGA 1993.MAR",
        string domestic = "SONIC CD",
        string intl = "SONIC THE HEDGEHOG CD",
        string product = "GM MK-4407 -00",
        ushort checksum = 0xABCD,
        string io = "J",
        string region = "JUE")
    {
        var b = new byte[0x200];
        void Put(int off, string s, int len)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            for (int i = 0; i < len; i++) b[off + i] = i < bytes.Length ? bytes[i] : (byte)' ';
        }
        Put(0x000, system, 16);
        Put(0x100, console, 16);
        Put(0x110, copyright, 16);
        Put(0x120, domestic, 48);
        Put(0x150, intl, 48);
        Put(0x180, product, 14);
        b[0x18E] = (byte)(checksum >> 8); b[0x18F] = (byte)checksum;
        Put(0x190, io, 16);
        Put(0x1F0, region, 16);
        return b;
    }

    [Fact]
    public void Parses_a_full_header()
    {
        var h = SegaCdDisc.Parse(BuildHeader());
        Assert.Equal("SEGADISCSYSTEM", h.SystemId);
        Assert.Equal("SEGA MEGA DRIVE", h.ConsoleName);
        Assert.Equal("(C)SEGA 1993.MAR", h.Copyright);
        Assert.Equal("SONIC CD", h.DomesticTitle);
        Assert.Equal("SONIC THE HEDGEHOG CD", h.InternationalTitle);
        Assert.Equal("GM MK-4407 -00", h.ProductCode);
        Assert.Equal(0xABCD, h.Checksum);
        Assert.Equal("SONIC THE HEDGEHOG CD", h.Title);   // prefers international
    }

    [Fact]
    public void Decodes_letter_style_regions()
    {
        var h = SegaCdDisc.Parse(BuildHeader(region: "JUE"));
        Assert.Equal(new[] { "Japan", "USA", "Europe" }, h.Regions);

        Assert.Equal(new[] { "USA" }, SegaCdDisc.DecodeRegion("U"));
        Assert.Equal(new[] { "Japan", "Europe" }, SegaCdDisc.DecodeRegion("JE"));
    }

    [Fact]
    public void Decodes_hex_bitfield_regions()
    {
        Assert.Equal(new[] { "Japan" }, SegaCdDisc.DecodeRegion("1"));
        Assert.Equal(new[] { "USA" }, SegaCdDisc.DecodeRegion("4"));
        Assert.Equal(new[] { "Japan", "USA", "Europe" }, SegaCdDisc.DecodeRegion("F"));   // 1|4|8
    }

    [Fact]
    public void Accepts_the_bootdisc_signature()
    {
        var h = SegaCdDisc.Parse(BuildHeader(system: "SEGABOOTDISC    "));
        Assert.Equal("SEGABOOTDISC", h.SystemId);
        Assert.True(SegaCdDisc.IsBootSector(BuildHeader(system: "SEGABOOTDISC    ")));
    }

    [Fact]
    public void Rejects_a_non_sega_sector()
    {
        var notSega = new byte[0x200];
        Encoding.ASCII.GetBytes("CD001 NOT SEGA  ").CopyTo(notSega, 0);
        Assert.False(SegaCdDisc.IsBootSector(notSega));
        Assert.Throws<SegaCdFormatException>(() => SegaCdDisc.Parse(notSega));
    }

    [Fact]
    public void Too_few_bytes_is_rejected()
    {
        Assert.Throws<SegaCdFormatException>(() => SegaCdDisc.Parse(new byte[0x100]));
    }

    [Fact]
    public void Identify_reads_a_bin_cue_pair()
    {
        string dir = Path.Combine(Path.GetTempPath(), "segacd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A cooked Mode1/2048 track: header at user offset 0.
            var bin = new byte[2048 * 4];
            BuildHeader(domestic: "TEST GAME").CopyTo(bin, 0);
            string binPath = Path.Combine(dir, "game.bin");
            File.WriteAllBytes(binPath, bin);
            string cuePath = Path.Combine(dir, "game.cue");
            File.WriteAllText(cuePath,
                "FILE \"game.bin\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n");

            var h = SegaCdDisc.Identify(cuePath);
            Assert.NotNull(h);
            Assert.Equal("TEST GAME", h!.DomesticTitle);
        }
        finally { Directory.Delete(dir, true); }
    }
}
