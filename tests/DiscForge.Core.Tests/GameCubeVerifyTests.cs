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
        // DOL parked well away from both the fixed apploader offset (0x2440) and the high-entropy fill
        // windows below, so the (deliberately all-zero, TotalSize=0) DOL header table never gets clobbered
        // by the random fill and misread as huge bogus section sizes.
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x420), 0x50000); // DOL
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

        // GcJunkMapper only samples the first ~64 KB of each padding region to classify it, so filling just
        // that much with real high-entropy bytes (not the whole multi-hundred-MB tail) is enough to make this
        // fixture read as "junk intact" rather than "scrubbed" — a plain zero-filled tail is legitimately
        // indistinguishable from a scrubbed dump, which is exactly the behaviour under test elsewhere.
        var rng = new Random(12345);
        FillRandom(ms, 0x2460, 96 * 1024, rng);          // the small gap after the (trivial) apploader
        if (setLength > 0x8064) FillRandom(ms, 0x8064, (int)Math.Min(96 * 1024, setLength - 0x8064), rng);

        ms.Position = 0;
        return ms;
    }

    private static void FillRandom(MemoryStream ms, long at, int count, Random rng)
    {
        if (count <= 0 || at >= ms.Length) return;
        count = (int)Math.Min(count, ms.Length - at);
        var buf = new byte[count];
        rng.NextBytes(buf);
        ms.Position = at;
        ms.Write(buf, 0, count);
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

    [Fact]
    public void A_zero_padded_tail_reads_as_scrubbed_padding()
    {
        // Build() normally seeds a little high-entropy junk so the "healthy" fixture is representative;
        // skip that here by building the raw header ourselves with an all-zero tail.
        var h = new byte[0x480];
        Encoding.ASCII.GetBytes("GALE").CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x1C), GcmReader.Magic);
        Encoding.ASCII.GetBytes("TEST GAME").CopyTo(h, 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x420), 0x2440);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x424), 0x460);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x428), 26);
        h[0x458] = 1;
        h[0x460] = 1; BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x468), 2);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x470), 0x8000);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x474), 100);
        h[0x478] = (byte)'A';
        using var ms = new MemoryStream();
        ms.Write(h);
        ms.SetLength(GameCubeVerify.GameCubeSingleLayerBytes);
        ms.Position = 0;

        var h2 = GameCubeVerify.Check(ms);
        Assert.Equal(GcPaddingVerdict.Scrubbed, h2.PaddingVerdict);
        Assert.Contains(h2.Warnings, w => w.StartsWith("padding:"));
        Assert.False(h2.Healthy);
    }

    [Fact]
    public void A_nonzero_bi2_debug_monitor_size_is_flagged_as_a_debug_build()
    {
        using var s = Build(country: 1, setLength: GameCubeVerify.GameCubeSingleLayerBytes);
        // bi2.bin's DebugMonitorSize sits at 0x440 + 0x00.
        s.Position = 0x440;
        s.Write(new byte[] { 0x00, 0x01, 0x00, 0x00 }, 0, 4);
        s.Position = 0;

        var h = GameCubeVerify.Check(s);
        Assert.True(h.LooksLikeDebugBuild);
        Assert.Contains("debug", h.Summary(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_DOL_offset_past_the_end_of_the_image_is_caught_by_the_boot_chain_check()
    {
        // FST corruption can't be used for this: GcmReader.Read validates FST bounds itself and throws
        // before Check() gets a chance to collect warnings. The DOL offset isn't validated there, so
        // corrupting it is what actually exercises GcBoot.CheckChain's own (independent) bounds check.
        using var s = Build(country: 1, setLength: GameCubeVerify.GameCubeSingleLayerBytes);
        s.Position = 0x420;
        var bad = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bad, (uint)(GameCubeVerify.GameCubeSingleLayerBytes + 100));
        s.Write(bad, 0, 4);
        s.Position = 0;

        var h = GameCubeVerify.Check(s);
        Assert.Contains(h.Warnings, w => w.Contains("boot chain") && w.Contains("DOL"));
    }
}
