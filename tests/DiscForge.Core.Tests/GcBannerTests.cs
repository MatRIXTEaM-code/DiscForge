// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.GameCube;
using Xunit;

namespace DiscForge.Core.Tests;

public class GcBannerTests
{
    private const int ImageOffset = 0x20;
    private const int CommentOffset = 0x1820;
    private const int CommentSize = 0x140;

    private static void Put(byte[] b, int o, string s, int len)
    {
        var bytes = Encoding.Latin1.GetBytes(s);
        for (int i = 0; i < len; i++) b[o + i] = i < bytes.Length ? bytes[i] : (byte)0;
    }

    private static byte[] BuildBanner(string magic, int languages, string shortName, string longName, string maker, string desc)
    {
        int size = CommentOffset + languages * CommentSize;
        var b = new byte[size];
        Encoding.ASCII.GetBytes(magic).CopyTo(b, 0);
        for (int i = 0; i < languages; i++)
        {
            int o = CommentOffset + i * CommentSize;
            Put(b, o + 0x00, shortName, 32);
            Put(b, o + 0x20, maker, 32);
            Put(b, o + 0x40, longName, 64);
            Put(b, o + 0x80, maker, 64);
            Put(b, o + 0xC0, desc, 128);
        }
        // Fill the icon with an opaque RGB555 magenta texel everywhere (top bit set).
        ushort magenta = 0x8000 | (0x1F << 10) | 0x1F;   // R=max, B=max
        for (int p = 0; p < 0x1800; p += 2) { b[ImageOffset + p] = (byte)(magenta >> 8); b[ImageOffset + p + 1] = (byte)magenta; }
        return b;
    }

    [Fact]
    public void Parses_a_bnr1_banner()
    {
        var b = BuildBanner("BNR1", 1, "SUPER GAME", "Super Game Deluxe", "DiscForge Studios", "The best test game.");
        var banner = GcBannerReader.Parse(b);
        Assert.Equal("BNR1", banner.Magic);
        Assert.Single(banner.Comments);
        Assert.Equal("Super Game Deluxe", banner.Primary.Title);
        Assert.Equal("DiscForge Studios", banner.Primary.Maker);
        Assert.Equal("The best test game.", banner.Primary.Description);
    }

    [Fact]
    public void Parses_a_bnr2_six_language_banner()
    {
        var b = BuildBanner("BNR2", 6, "JEU", "Jeu Deluxe", "Studio", "desc");
        var banner = GcBannerReader.Parse(b);
        Assert.Equal("BNR2", banner.Magic);
        Assert.Equal(6, banner.Comments.Count);
    }

    [Fact]
    public void Falls_back_to_short_name_when_long_is_empty()
    {
        var b = BuildBanner("BNR1", 1, "ONLY SHORT", "", "Maker", "");
        var banner = GcBannerReader.Parse(b);
        Assert.Equal("ONLY SHORT", banner.Primary.Title);
    }

    [Fact]
    public void Decodes_the_icon_to_rgba()
    {
        var b = BuildBanner("BNR1", 1, "G", "G", "M", "D");
        var rgba = GcBannerReader.DecodeIconRgba(b);
        Assert.Equal(GcBanner.ImageWidth * GcBanner.ImageHeight * 4, rgba.Length);
        // Every texel was opaque magenta: R=255, G=0, B=255, A=255.
        Assert.Equal(255, rgba[0]);   // R
        Assert.Equal(0, rgba[1]);     // G
        Assert.Equal(255, rgba[2]);   // B
        Assert.Equal(255, rgba[3]);   // A
    }

    [Fact]
    public void Rejects_a_non_banner()
    {
        Assert.False(GcBannerReader.IsBanner(Encoding.ASCII.GetBytes("NOPE")));
        Assert.Throws<GameCubeFormatException>(() => GcBannerReader.Parse(new byte[100]));
    }

    [Theory]
    [InlineData("GALE", "USA (NTSC-U)")]
    [InlineData("GM4P", "Europe (PAL)")]
    [InlineData("GZLJ", "Japan (NTSC-J)")]
    [InlineData("GXXD", "Germany (PAL)")]
    public void Decodes_region_from_the_game_code(string code, string expected)
    {
        Assert.Equal(expected, GameCubeRegion.Decode(code));
    }
}
