// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Nec;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for PC-FX identification: the "PC-FX:Hu_CD-ROM" boot signature is found wherever it sits in the data
/// area (after a leading gap), the offset is reported, and the boot-header text is surfaced; a buffer without
/// the signature is not mistaken for a PC-FX disc.
/// </summary>
public class PcfxTests
{
    private static byte[] Image(long sigAt, string bootText)
    {
        var b = new byte[sigAt + 0x400];
        var boot = Encoding.ASCII.GetBytes("PC-FX:Hu_CD-ROM " + new string('\0', 8) + bootText);
        boot.CopyTo(b, (int)sigAt);
        return b;
    }

    [Fact]
    public void The_boot_signature_is_found_and_the_offset_reported()
    {
        var img = Image(0x9300, "MACROSS (C)1994 NEC");
        var disc = Pcfx.Identify(img);

        Assert.True(disc.IsPcfx);
        Assert.Equal(0x9300, disc.SignatureOffset);
        Assert.Contains("MACROSS", disc.BootText);
        Assert.True(Pcfx.IsPcfx(img));
    }

    [Fact]
    public void A_disc_without_the_signature_is_not_pc_fx()
    {
        var b = new byte[100_000];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(i * 37 + 11);
        var disc = Pcfx.Identify(b);

        Assert.False(disc.IsPcfx);
        Assert.Equal(-1, disc.SignatureOffset);
        Assert.False(Pcfx.IsPcfx(b));
    }
}
