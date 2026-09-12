// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Dat;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>Tests for parsing the public No-Intro/Redump catalogued-name convention into structured
/// region/revision/disc/variant fields — the "revision/variant-aware DAT match" half of item 7.</summary>
public class DatNameTagsTests
{
    [Fact]
    public void Plain_region_and_revision_are_extracted()
    {
        var t = DatNameTagParser.Parse("Super Smash Bros. Melee (USA) (Rev 2)");
        Assert.Equal("Super Smash Bros. Melee", t.Title);
        Assert.Equal(new[] { "USA" }, t.Regions);
        Assert.Equal(2, t.Revision);
        Assert.True(t.IsRetailFull);
    }

    [Fact]
    public void Multi_region_and_language_group_are_both_captured()
    {
        var t = DatNameTagParser.Parse("Donkey Konga (Europe) (En,Fr,De,Es,It)");
        Assert.Equal(new[] { "Europe" }, t.Regions);
        Assert.Equal(new[] { "En", "Fr", "De", "Es", "It" }, t.Languages);
    }

    [Fact]
    public void Demo_and_version_tags_mark_it_as_not_a_full_retail_release()
    {
        var t = DatNameTagParser.Parse("Interactive Multi-Game Demo Disc (USA) (Demo) (v35)");
        Assert.True(t.IsDemo);
        Assert.Equal("v35", t.VersionRaw);
        Assert.False(t.IsRetailFull);
    }

    [Fact]
    public void Kiosk_and_unlicensed_tags_are_recognized()
    {
        Assert.True(DatNameTagParser.Parse("Game Boy Player Start-Up Disc (USA) (Kiosk)").IsKiosk);
        Assert.True(DatNameTagParser.Parse("Homebrew Thing (World) (Unl)").IsUnlicensed);
    }

    [Fact]
    public void Disc_number_and_count_are_parsed()
    {
        var t = DatNameTagParser.Parse("Some RPG (USA) (Disc 1 of 2)");
        Assert.Equal(1, t.DiscNumber);
        Assert.Equal(2, t.DiscCount);
    }

    [Fact]
    public void An_unrecognized_tag_is_kept_verbatim_rather_than_dropped()
    {
        var t = DatNameTagParser.Parse("Something Weird (USA) (Some Unknown Tag)");
        Assert.Contains("Some Unknown Tag", t.OtherTags);
    }

    [Fact]
    public void Summary_composes_the_recognized_fields()
    {
        var t = DatNameTagParser.Parse("Metroid Prime (USA) (Rev 1)");
        Assert.Equal("Metroid Prime [USA, Rev 1]", t.Summary());
    }

    [Fact]
    public void A_name_with_no_parenthetical_tags_still_parses_a_title()
    {
        var t = DatNameTagParser.Parse("Untitled Prototype");
        Assert.Equal("Untitled Prototype", t.Title);
        Assert.Empty(t.Regions);
        Assert.Equal("Untitled Prototype", t.Summary());
    }
}
