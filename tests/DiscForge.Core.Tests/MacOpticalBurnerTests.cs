// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Burning;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The macOS burn backend drives Apple's own hdiutil / system_profiler tools, so its correctness is the
/// argument list it builds. hdiutil verifies a written disc by default, so a verifying burn passes no extra
/// flag and a non-verifying burn adds -noverifyburn; a requested speed appends -speed N. (The actual write
/// needs a Mac with an optical recorder and is exercised there; these lock the command construction.)
/// </summary>
public class MacOpticalBurnerTests
{
    [Fact]
    public void A_verifying_burn_passes_no_extra_flag()
    {
        var a = MacOpticalBurner.BuildBurnArgs("/tmp/game.iso", verify: true);
        Assert.Equal(new[] { "burn", "/tmp/game.iso" }, a);
    }

    [Fact]
    public void A_non_verifying_burn_adds_noverifyburn()
    {
        var a = MacOpticalBurner.BuildBurnArgs("/tmp/game.iso", verify: false);
        Assert.Equal(new[] { "burn", "/tmp/game.iso", "-noverifyburn" }, a);
    }

    [Fact]
    public void A_speed_request_is_appended()
    {
        var a = MacOpticalBurner.BuildBurnArgs("/tmp/game.iso", verify: true, speedMultiplier: 8);
        Assert.Equal(new[] { "burn", "/tmp/game.iso", "-speed", "8" }, a);
    }

    [Fact]
    public void Drive_enumeration_queries_the_disc_burning_data_type()
    {
        Assert.Equal(new[] { "SPDiscBurningDataType", "-detailLevel", "mini" }, MacOpticalBurner.BuildDrivesArgs());
    }

    [Fact]
    public void An_empty_image_path_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => MacOpticalBurner.BuildBurnArgs("", verify: true));
    }
}
