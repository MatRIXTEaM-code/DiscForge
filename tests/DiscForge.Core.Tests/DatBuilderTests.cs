// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Dat;
using Xunit;

namespace DiscForge.Core.Tests;

public class DatBuilderTests
{
    private static readonly DatBuildRom[] Sample =
    {
        new("Game A", "Game A.iso", 5000, "e2d81152", "9761cec0d158b7ede00a99402f89757f", "580b593dbbf67f8e4478778e9f33b3684ee019d4"),
        new("Game B", "Game B.bin", 8000, "03F08F17", null, "8cb506965dd567d374b196ae3818c858cf019bf1"),
    };

    [Fact]
    public void Built_dat_round_trips_through_the_parser()
    {
        string xml = DatBuilder.Build("My Collection", Sample);
        var dat = DatFile.ParseText(xml);

        Assert.Equal("My Collection", dat.Name);
        Assert.Equal(2, dat.Count);

        // A dump verifies against the DAT it was built from — by CRC and by SHA-1.
        var byCrc = dat.Verify(5000, "e2d81152", null, null);
        Assert.True(byCrc.Verified);
        Assert.Equal("Game A", byCrc.Rom!.Game);

        var bySha1 = dat.Verify(8000, null, "8cb506965dd567d374b196ae3818c858cf019bf1", null);
        Assert.True(bySha1.Verified);
        Assert.Equal("Game B", bySha1.Rom!.Game);
    }

    [Fact]
    public void Crc_is_lowercased_regardless_of_input_case()
    {
        string xml = DatBuilder.Build("C", Sample);
        Assert.Contains("crc=\"03f08f17\"", xml);   // input "03F08F17" emitted lower-case
        Assert.DoesNotContain("03F08F17", xml);
    }

    [Fact]
    public void A_wrong_hash_does_not_verify()
    {
        var dat = DatFile.ParseText(DatBuilder.Build("C", Sample));
        Assert.False(dat.Verify(5000, "deadbeef", null, null).Verified);
    }

    [Fact]
    public void Special_characters_in_names_are_escaped()
    {
        var roms = new[] { new DatBuildRom("Tom & Jerry <fun>", "t&j.iso", 10, "00000000", null, null) };
        string xml = DatBuilder.Build("A & B", roms);
        // Must still parse (i.e. the ampersands/angle brackets were escaped).
        var dat = DatFile.ParseText(xml);
        Assert.Equal("Tom & Jerry <fun>", dat.Roms[0].Game);
    }
}
