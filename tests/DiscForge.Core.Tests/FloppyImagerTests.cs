// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Floppy;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The floppy imager copies a block device to a flat .img in 512-byte sectors and
/// names the geometry. These pin the copy (byte-exact, correct sector count) and the
/// size→geometry mapping, and the short-final-read flag for a truncated disk.
/// </summary>
public class FloppyImagerTests
{
    [Theory]
    [InlineData(1_474_560, "1.44 MB")]
    [InlineData(737_280, "720 KB")]
    [InlineData(1_228_800, "1.2 MB")]
    [InlineData(368_640, "360 KB")]
    [InlineData(2_949_120, "2.88 MB")]
    public void Names_known_floppy_geometries(long bytes, string expect)
        => Assert.Contains(expect, FloppyImager.DescribeSize(bytes));

    [Fact]
    public void Non_standard_size_is_flagged()
        => Assert.Contains("non-standard", FloppyImager.DescribeSize(123_456));

    [Fact]
    public void Copies_byte_exact_and_counts_sectors()
    {
        var src = new byte[16 * FloppyImager.SectorBytes];   // 16 sectors
        new Random(7).NextBytes(src);
        using var input = new MemoryStream(src);
        using var output = new MemoryStream();

        var report = FloppyImager.Copy(input, output);

        Assert.Equal(src.Length, report.Bytes);
        Assert.Equal(16, report.Sectors);
        Assert.False(report.ShortFinalRead);
        Assert.Equal(src, output.ToArray());
    }

    [Fact]
    public void A_partial_final_sector_is_flagged()
    {
        var src = new byte[FloppyImager.SectorBytes + 100];   // one full sector + a stub
        using var input = new MemoryStream(src);
        using var output = new MemoryStream();

        var report = FloppyImager.Copy(input, output);

        Assert.Equal(src.Length, report.Bytes);   // all bytes still copied
        Assert.Equal(1, report.Sectors);          // only one *whole* sector
        Assert.True(report.ShortFinalRead);
    }
}
