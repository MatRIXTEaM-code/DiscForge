// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Identify;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The retro-console disc identifiers: 3DO by its Opera volume-header signature
/// (0x01 then five 0x5A sync bytes), and PC-Engine CD / Neo-Geo CD by the plaintext
/// system string in their boot area. These pin the signatures at the track-user-data
/// offsets a raw or cooked image uses, and confirm unrelated data stays Unknown.
/// </summary>
public class RetroIdentifierTests
{
    [Fact]
    public void ThreeDo_is_recognised_by_its_opera_header_at_a_cooked_offset()
    {
        var img = new byte[0x200];
        img[0] = 0x01;
        for (int i = 1; i <= 5; i++) img[i] = 0x5A;   // "\x01ZZZZZ"
        Assert.Equal("3DO", FormatIdentifier.Identify(img).Name);
    }

    [Fact]
    public void ThreeDo_is_recognised_at_a_raw_mode1_offset()
    {
        var img = new byte[0x200];
        img[16] = 0x01;
        for (int i = 17; i <= 21; i++) img[i] = 0x5A;
        Assert.Equal("3DO", FormatIdentifier.Identify(img).Name);
    }

    [Fact]
    public void Pc_engine_cd_is_recognised_by_its_system_string()
    {
        var img = new byte[0x1000];
        Encoding.ASCII.GetBytes("PC Engine CD-ROM SYSTEM").CopyTo(img, 0x800);
        var id = FormatIdentifier.Identify(img);
        Assert.Equal("PC Engine CD", id.Name);
        Assert.Equal("disc image", id.Category);
    }

    [Fact]
    public void Neo_geo_cd_is_recognised_by_its_system_string()
    {
        var img = new byte[0x1000];
        Encoding.ASCII.GetBytes("NEO-GEO").CopyTo(img, 0x40);
        Assert.Equal("Neo-Geo CD", FormatIdentifier.Identify(img).Name);
    }

    [Fact]
    public void Unrelated_data_is_not_mistaken_for_a_retro_disc()
    {
        var img = new byte[0x400];
        new Random(1).NextBytes(img);
        img[0] = 0x02;   // not the 3DO record type
        Assert.Equal("Unknown", FormatIdentifier.Identify(img).Name);
    }
}
