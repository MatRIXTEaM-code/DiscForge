// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.GameCube;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the self-validating junk reconstructor, including the CRC-32 confirmation step: the
/// finished output always carries a CRC-32, and a caller-supplied expected value (as if from a
/// Redump entry) is checked independently of the self-validation gate.
/// </summary>
public class GcJunkReconstructorTests
{
    private static readonly byte[] DiscId = Encoding.ASCII.GetBytes("GALE");
    private const long ImageLength = 0xC0000;   // 768 KiB — small but has two real padding gaps
    private const long Gap1Start = 0x2460, Gap1End = 0x8000;      // "intact junk" region
    private const long FileStart = 0x8000, FileEnd = 0x8064;      // a tiny "used" file
    private const long Gap2Start = 0x8064;                        // "scrubbed" region, to the tail

    /// <summary>A minimal but structurally valid image: real GcJunkGenerator output in the first gap
    /// (so self-validation has real junk to check against) and an all-zero second gap (the "scrubbed"
    /// target to be reconstructed).</summary>
    private static byte[] BuildScrubbedImage()
    {
        var img = new byte[ImageLength];
        DiscId.CopyTo(img, 0);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x1C), GcmReader.Magic);
        Encoding.ASCII.GetBytes("TEST GAME").CopyTo(img, 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x420), 0x50000);   // DOL — kept trivial/zero
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x424), 0x460);    // FST offset
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x428), 26);       // FST size
        img[0x460] = 1; BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x468), 2);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x470), (uint)FileStart);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0x474), (uint)(FileEnd - FileStart));
        img[0x478] = (byte)'A';

        // Gap 1: real junk-generator output, so the reconstructor's self-validation has something
        // genuine to confirm against.
        var junk = GcJunkGenerator.Generate(DiscId, Gap1Start, (int)(Gap1End - Gap1Start));
        junk.CopyTo(img, Gap1Start);

        // Gap 2 (0x8064..end) is left all-zero — the "scrubbed" region under test.
        return img;
    }

    [Fact]
    public void Reconstruct_fills_the_scrubbed_region_once_the_generator_self_validates()
    {
        using var input = new MemoryStream(BuildScrubbedImage());
        using var output = new MemoryStream();
        var report = GcJunkReconstructor.Reconstruct(input, output);

        Assert.True(report.SelfValidated);
        Assert.True(report.Reconstructed);
        Assert.Equal(1, report.ScrubbedRegionsFilled);
        Assert.Equal(ImageLength - Gap2Start, report.BytesFilled);

        var expectedFill = GcJunkGenerator.Generate(DiscId, Gap2Start, (int)(ImageLength - Gap2Start));
        var actual = output.ToArray();
        Assert.Equal(expectedFill, actual[(int)Gap2Start..]);
    }

    [Fact]
    public void OutputCrc32_matches_a_hand_computed_CRC_of_the_final_bytes()
    {
        using var input = new MemoryStream(BuildScrubbedImage());
        using var output = new MemoryStream();
        var report = GcJunkReconstructor.Reconstruct(input, output);

        uint expectedCrc = Crc32.Compute(output.ToArray());
        Assert.Equal(expectedCrc, report.OutputCrc32);
        Assert.Null(report.ExpectedCrc32);
        Assert.Null(report.CrcConfirmed);
    }

    [Fact]
    public void A_matching_expectedCrc32_reports_CrcConfirmed_true()
    {
        // First pass to learn the real output CRC (as if it came from a Redump entry).
        using (var input0 = new MemoryStream(BuildScrubbedImage()))
        using (var output0 = new MemoryStream())
        {
            var pre = GcJunkReconstructor.Reconstruct(input0, output0);
            uint knownGood = pre.OutputCrc32;

            using var input1 = new MemoryStream(BuildScrubbedImage());
            using var output1 = new MemoryStream();
            var report = GcJunkReconstructor.Reconstruct(input1, output1, knownGood);

            Assert.Equal(knownGood, report.ExpectedCrc32);
            Assert.True(report.CrcConfirmed);
        }
    }

    [Fact]
    public void A_wrong_expectedCrc32_reports_CrcConfirmed_false_without_affecting_reconstruction()
    {
        using var input = new MemoryStream(BuildScrubbedImage());
        using var output = new MemoryStream();
        var report = GcJunkReconstructor.Reconstruct(input, output, expectedCrc32: 0xDEADBEEF);

        Assert.False(report.CrcConfirmed);
        // The mismatch is purely informational — it doesn't roll back a reconstruction that already
        // self-validated correctly.
        Assert.True(report.SelfValidated);
        Assert.True(report.Reconstructed);
    }

    [Fact]
    public void A_fully_scrubbed_image_with_no_surviving_junk_still_reports_a_CRC32()
    {
        var img = BuildScrubbedImage();
        Array.Clear(img, (int)Gap1Start, (int)(Gap1End - Gap1Start));   // zero out the "intact" gap too

        using var input = new MemoryStream(img);
        using var output = new MemoryStream();
        var report = GcJunkReconstructor.Reconstruct(input, output);

        Assert.False(report.SelfValidated);
        Assert.False(report.Reconstructed);
        Assert.Equal(Crc32.Compute(output.ToArray()), report.OutputCrc32);
    }
}
