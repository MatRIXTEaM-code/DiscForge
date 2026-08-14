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
/// Tests for the GameCube padding map. A synthetic image is laid out with a boot header, apploader, DOL, FST
/// and one file, leaving four padding gaps (after each structure, plus the tail). Filling those gaps with
/// high-entropy bytes reads as intact junk; zeroing them reads as scrubbed; a low-entropy non-zero pattern is
/// flagged as suspicious. The map must also find the used structures and exclude them from the padding.
/// </summary>
public class GcJunkMapTests
{
    private const int Size = 0x400000;

    // Build a full-layout GC image whose padding gaps carry the given fill pattern.
    private static byte[] Build(Func<int, byte> fillAt)
    {
        var buf = new byte[Size];
        for (int i = 0; i < Size; i++) buf[i] = fillAt(i);

        void U32(int off, uint v) => BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(off), v);
        void Zero(int off, int len) => Array.Clear(buf, off, len);

        // Boot header.
        "GALE"u8.CopyTo(buf.AsSpan(0x00));
        "01"u8.CopyTo(buf.AsSpan(0x04));
        U32(0x1C, GcmReader.Magic);
        "TEST GAME"u8.CopyTo(buf.AsSpan(0x20));
        U32(0x420, 0x100000);   // DOL offset
        U32(0x424, 0x200000);   // FST offset
        U32(0x428, 26);         // FST size
        Zero(0x440, 0x2440 - 0x440); // bi2 (used area)

        // Apploader at 0x2440: size 0x100 + trailer 0x20 => footprint 0x140.
        Zero(0x2440, 0x140);
        "2002/01/01"u8.CopyTo(buf.AsSpan(0x2440));
        U32(0x2440 + 0x14, 0x100);
        U32(0x2440 + 0x18, 0x20);

        // DOL at 0x100000: one section (file offset 0x100, size 0xF00) => total 0x1000.
        Zero(0x100000, 0x1000);
        U32(0x100000 + 0x00, 0x100);
        U32(0x100000 + 0x90, 0xF00);

        // FST at 0x200000: root(dir, count=2) + file entry + "A\0".
        int f = 0x200000;
        Zero(f, 26);
        buf[f + 0x00] = 1;              // root directory flag
        U32(f + 0x08, 2);              // total entries
        U32(f + 0x10, 0x300000);       // file data offset
        U32(f + 0x14, 0x1000);         // file size
        buf[f + 0x18] = (byte)'A';

        Zero(0x300000, 0x1000);        // file data (used area)
        return buf;
    }

    private static GcJunkMap Analyze(byte[] image) => GcJunkMapper.Analyze(new MemoryStream(image));

    [Fact]
    public void High_entropy_padding_reads_as_intact_junk()
    {
        // Deterministic high-entropy fill: a byte value that cycles through all 256 values.
        var map = Analyze(Build(i => (byte)((i * 2654435761u) >> 24)));
        Assert.Equal(GcPaddingVerdict.JunkIntact, map.Verdict);
        Assert.Equal(4, map.Regions.Count(r => r.Length >= GcJunkMapper.SignificantRegionBytes));
        Assert.All(map.Regions.Where(r => r.Length >= GcJunkMapper.SignificantRegionBytes),
                   r => Assert.Equal(JunkClass.Junk, r.Class));
    }

    [Fact]
    public void Zeroed_padding_reads_as_scrubbed()
    {
        var map = Analyze(Build(_ => 0));
        Assert.Equal(GcPaddingVerdict.Scrubbed, map.Verdict);
        Assert.All(map.Regions.Where(r => r.Length >= GcJunkMapper.SignificantRegionBytes),
                   r => Assert.Equal(JunkClass.Zeroed, r.Class));
    }

    [Fact]
    public void Low_entropy_non_zero_padding_is_flagged_as_suspicious()
    {
        var pattern = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var map = Analyze(Build(i => pattern[i % 4]));
        Assert.Equal(GcPaddingVerdict.Suspicious, map.Verdict);
        Assert.Contains(map.Regions, r => r.Class == JunkClass.Structured);
    }

    [Fact]
    public void The_used_structures_are_excluded_from_the_padding()
    {
        var map = Analyze(Build(_ => 0));
        // No padding region may overlap the boot area, apploader, DOL, FST or the file extent.
        (long, long)[] used =
        {
            (0, 0x2440), (0x2440, 0x2580), (0x100000, 0x101000),
            (0x200000, 0x20001A), (0x300000, 0x301000),
        };
        foreach (var r in map.Regions)
            foreach (var (s, e) in used)
                Assert.False(r.Start < e && r.End > s, $"padding region 0x{r.Start:X} overlaps used [0x{s:X},0x{e:X})");
    }
}
