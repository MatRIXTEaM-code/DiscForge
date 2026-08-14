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
/// The Linux burn backend drives growisofs (DVD/Blu-ray) and wodim (CD), so its correctness is the argument
/// lists it builds and the writer it picks by media size. The real write needs a Linux box with a recorder;
/// these lock the command construction and the CD-vs-DVD tool selection.
/// </summary>
public class LinuxOpticalBurnerTests
{
    [Fact]
    public void Growisofs_args_burn_the_image_to_the_device()
    {
        var a = LinuxOpticalBurner.BuildGrowisofsArgs("/dev/sr0", "/img/game.iso");
        Assert.Equal(new[] { "-dvd-compat", "-Z", "/dev/sr0=/img/game.iso" }, a);
    }

    [Fact]
    public void Wodim_args_do_a_cd_data_burn_with_eject()
    {
        var a = LinuxOpticalBurner.BuildWodimArgs("/dev/sr0", "/img/game.iso");
        Assert.Equal(new[] { "-v", "dev=/dev/sr0", "-eject", "-data", "/img/game.iso" }, a);
    }

    [Fact]
    public void The_writer_is_chosen_by_media_size()
    {
        Assert.Equal("wodim", LinuxOpticalBurner.PreferredTool(700L * 1024 * 1024));      // CD-sized
        Assert.Equal("growisofs", LinuxOpticalBurner.PreferredTool(4L * 1024 * 1024 * 1024)); // DVD-sized
    }

    [Fact]
    public void An_empty_image_path_is_rejected()
    {
        Assert.Throws<System.ArgumentException>(() => LinuxOpticalBurner.BuildWodimArgs("/dev/sr0", ""));
    }
}
