// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.GameCube;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The clean-room GameCube junk generator and its SELF-VALIDATING reconstructor. The generator's
/// bit-exactness vs Nintendo can't be proven without a real disc, so these tests prove the two
/// things that DO make it safe: the generator is deterministic and position-consistent, and the
/// reconstructor only fills scrubbed padding when it first reproduces the image's OWN surviving
/// junk byte-for-byte — declining (never corrupting) otherwise.
/// </summary>
public class GcJunkGeneratorTests
{
    private static readonly byte[] DiscId = { (byte)'G', (byte)'A', (byte)'L', (byte)'E' };

    [Fact]
    public void Generator_is_deterministic_and_position_consistent()
    {
        // Same disc id + offset → same bytes, every time.
        var a = GcJunkGenerator.Generate(DiscId, 0x2460, 4096);
        var b = GcJunkGenerator.Generate(DiscId, 0x2460, 4096);
        Assert.Equal(a, b);

        // A sub-range asked for on its own equals that slice of the larger fill — so a caller can
        // regenerate any window of any region and get the disc's bytes there.
        var whole = GcJunkGenerator.Generate(DiscId, 0x40000 - 100, 400);   // crosses a block seam
        var tail = GcJunkGenerator.Generate(DiscId, 0x40000, 300);          // the block after the seam
        Assert.True(whole.AsSpan(100, 300).SequenceEqual(tail));

        // Different disc id → different junk (extremely likely).
        var other = GcJunkGenerator.Generate(new byte[] { (byte)'S', (byte)'O', (byte)'U', (byte)'P' }, 0x2460, 4096);
        Assert.NotEqual(a, other);

        // It doesn't produce all-zeros (that's the scrubbed state it's meant to replace).
        Assert.Contains(a, x => x != 0);
    }

    [Fact]
    public void Reconstructor_self_validates_then_fills_scrubbed_padding()
    {
        var image = BuildImage(fillRegionA: true, zeroRegionB: true);
        using var input = new MemoryStream(image, writable: false);
        using var output = new MemoryStream();

        var report = GcJunkReconstructor.Reconstruct(input, output);

        Assert.True(report.SelfValidated);
        Assert.True(report.Reconstructed);
        Assert.Equal(1, report.IntactRegionsChecked);
        Assert.Equal(1, report.ScrubbedRegionsFilled);
        Assert.Equal(RegionBLen, report.BytesFilled);

        // Region B in the output now carries the regenerated junk for its absolute offset.
        var outBytes = output.ToArray();
        var expected = GcJunkGenerator.Generate(DiscId, RegionBStart, (int)RegionBLen);
        Assert.True(outBytes.AsSpan((int)RegionBStart, (int)RegionBLen).SequenceEqual(expected));
        // Region A (the surviving junk) is untouched.
        Assert.True(outBytes.AsSpan((int)RegionAStart, (int)RegionALen)
            .SequenceEqual(image.AsSpan((int)RegionAStart, (int)RegionALen)));
    }

    [Fact]
    public void Reconstructor_declines_when_surviving_junk_does_not_match()
    {
        // Tamper one byte of the surviving-junk region: the generator no longer reproduces it,
        // so the reconstructor must DECLINE and leave the scrubbed region untouched.
        var image = BuildImage(fillRegionA: true, zeroRegionB: true);
        image[RegionAStart + 1000] ^= 0xFF;

        using var input = new MemoryStream(image, writable: false);
        using var output = new MemoryStream();
        var report = GcJunkReconstructor.Reconstruct(input, output);

        Assert.False(report.SelfValidated);
        Assert.False(report.Reconstructed);
        Assert.Equal(0, report.ScrubbedRegionsFilled);
        // Region B stays zero — nothing was written on a failed self-validation.
        var outBytes = output.ToArray();
        Assert.All(outBytes[(int)RegionBStart..(int)(RegionBStart + RegionBLen)], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Reconstructor_declines_a_fully_scrubbed_image()
    {
        // No surviving junk to validate against → decline (don't write an unprovable guess).
        var image = BuildImage(fillRegionA: false, zeroRegionB: true);   // region A also zeroed
        using var input = new MemoryStream(image, writable: false);
        using var output = new MemoryStream();

        var report = GcJunkReconstructor.Reconstruct(input, output);

        Assert.False(report.SelfValidated);
        Assert.False(report.Reconstructed);
        Assert.Contains("no SURVIVING junk", report.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    // ---- synthetic GameCube image: boot+apploader (used), region A (junk or zero),
    //      a small FST (used, splits the padding), region B (scrubbed zeros) ----
    private const long RegionAStart = 0x2460;
    private const long RegionALen = 0x100000;
    private const long FstOffset = RegionAStart + RegionALen;   // 0x102460
    private const long FstSize = 0x1000;
    private const long RegionBStart = FstOffset + FstSize;      // 0x103460
    private const long RegionBLen = 0x100000;
    private const long ImageLen = RegionBStart + RegionBLen;    // 0x203460

    private static byte[] BuildImage(bool fillRegionA, bool zeroRegionB)
    {
        var img = new byte[ImageLen];
        DiscId.CopyTo(img, 0);
        // Header: dolOffset = 0 (skip), fstOffset / fstSize carve a middle "used" block.
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x420), 0);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x424), (uint)FstOffset);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x428), (uint)FstSize);
        // Apploader header at 0x2440: Size (0x14) and TrailerSize (0x18) = 0 → used = [0x2440,0x2460).
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x2440 + 0x14), 0);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x2440 + 0x18), 0);

        if (fillRegionA)
        {
            var junk = GcJunkGenerator.Generate(DiscId, RegionAStart, (int)RegionALen);
            junk.CopyTo(img, (int)RegionAStart);
        }
        // Region B left as zeros when zeroRegionB (the scrubbed state). FST block stays zero (used).
        _ = zeroRegionB;
        return img;
    }
}
