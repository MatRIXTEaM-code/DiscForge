// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.GameCube;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the single-image GameCube health check: a full-size disc with a sane boot header and matching
/// region reads as healthy; a short image is flagged as scrubbed/truncated; and a bi2-vs-game-code region
/// disagreement is caught. Images are built from a hand-laid boot header + minimal FST, using a sparse file
/// to reach the standard disc size without consuming disk.
/// </summary>
public class GameCubeVerifyTests
{
    private static Stream Build(byte country, long setLength, char regionLetter = 'E')
    {
        var h = new byte[0x480];
        Encoding.ASCII.GetBytes($"GAL{regionLetter}").CopyTo(h, 0);
        Encoding.ASCII.GetBytes("01").CopyTo(h, 4);
        h[0x08] = 1;                                                     // audio-streaming flag
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x1C), GcmReader.Magic);
        Encoding.ASCII.GetBytes("TEST GAME").CopyTo(h, 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x420), 0x2440);  // DOL
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x424), 0x460);   // FST offset
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x428), 26);      // FST size
        h[0x458] = country;
        // Minimal FST: root (count=2) + one file + "A\0" string table.
        h[0x460] = 1; BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x468), 2);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x470), 0x8000);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x474), 100);
        h[0x478] = (byte)'A';

        var ms = new MemoryStream();
        ms.Write(h);
        if (setLength > h.Length) ms.SetLength(setLength);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void A_standard_sized_disc_with_a_sane_header_reads_as_healthy()
    {
        using var s = Build(country: 1, setLength: GameCubeVerify.GameCubeSingleLayerBytes);
        var h = GameCubeVerify.Check(s);
        Assert.Equal("GALE", h.GameCode);
        Assert.Equal("NTSC-U", h.BiRegion);
        Assert.True(h.RegionConsistent);
        Assert.Equal(GcSizeClass.GameCubeSingleLayer, h.SizeClass);
        Assert.True(h.AudioStreaming);
        Assert.True(h.Healthy);
    }

    [Fact]
    public void A_short_image_is_flagged_as_scrubbed_or_truncated()
    {
        using var s = Build(country: 1, setLength: 0x100000);
        var h = GameCubeVerify.Check(s);
        Assert.Equal(GcSizeClass.GameCubeSmaller, h.SizeClass);
        Assert.Contains(h.Warnings, w => w.Contains("short of a standard"));
    }

    [Fact]
    public void A_region_disagreement_between_bi2_and_the_game_code_is_flagged()
    {
        using var s = Build(country: 2, setLength: GameCubeVerify.GameCubeSingleLayerBytes, regionLetter: 'E');
        var h = GameCubeVerify.Check(s);
        Assert.False(h.RegionConsistent);
        Assert.Contains(h.Warnings, w => w.Contains("region mismatch"));
    }
}
