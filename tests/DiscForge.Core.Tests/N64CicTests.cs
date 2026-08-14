// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Rom;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

public class N64CicTests
{
    private static byte[] MakeZ64(int size)
    {
        var b = new byte[size];
        b[0] = 0x80; b[1] = 0x37; b[2] = 0x12; b[3] = 0x40;
        var rnd = new Random(7);
        for (int i = 4; i < size; i++) b[i] = (byte)rnd.Next(256);
        return b;
    }

    [Fact]
    public void Detects_and_normalises_all_three_byte_orders()
    {
        var z64 = MakeZ64(0x2000);
        var v64 = (byte[])z64.Clone();
        for (int i = 0; i + 1 < v64.Length; i += 2) (v64[i], v64[i + 1]) = (v64[i + 1], v64[i]);
        var n64 = (byte[])z64.Clone();
        for (int i = 0; i + 3 < n64.Length; i += 4)
        { (n64[i], n64[i + 3]) = (n64[i + 3], n64[i]); (n64[i + 1], n64[i + 2]) = (n64[i + 2], n64[i + 1]); }

        Assert.Equal("z64", N64Cic.DetectOrder(z64));
        Assert.Equal("v64", N64Cic.DetectOrder(v64));
        Assert.Equal("n64", N64Cic.DetectOrder(n64));
        Assert.Equal(z64, N64Cic.Normalize(v64, "v64"));
        Assert.Equal(z64, N64Cic.Normalize(n64, "n64"));
    }

    [Fact]
    public void Boot_crc_is_deterministic_across_all_cic_branches()
    {
        var rom = MakeZ64(0x101000);
        var (a1, a2) = N64Cic.ComputeBootCrc(rom, 6102, 0xF8CA4DDC);
        var (b1, b2) = N64Cic.ComputeBootCrc(rom, 6102, 0xF8CA4DDC);
        Assert.Equal(a1, b1);
        Assert.Equal(a2, b2);
        // 6105 (rolling-window) and 6106 (alternate combine) branches must also be stable.
        Assert.Equal(N64Cic.ComputeBootCrc(rom, 6105, 0xDF26F436), N64Cic.ComputeBootCrc(rom, 6105, 0xDF26F436));
        Assert.Equal(N64Cic.ComputeBootCrc(rom, 6106, 0x1FEA617A), N64Cic.ComputeBootCrc(rom, 6106, 0x1FEA617A));
    }

    // These are the boot-CRC values an independent implementation of the published algorithm produces for the
    // deterministic seed-7 synthetic ROM — a regression lock on the byte-order/overflow/ROL details of the port.
    [Fact]
    public void Boot_crc_matches_the_reference_vectors()
    {
        var rom = MakeZ64(0x101000);
        Assert.Equal((0x3EB8409Fu, 0xCE5CE029u), N64Cic.ComputeBootCrc(rom, 6102, 0xF8CA4DDC));
        Assert.Equal((0xC0D5E170u, 0xC1370CEDu), N64Cic.ComputeBootCrc(rom, 6105, 0xDF26F436));
        Assert.Equal((0x581D8642u, 0x640C4CA4u), N64Cic.ComputeBootCrc(rom, 6106, 0x1FEA617A));
    }

    [Fact]
    public void Analyze_reads_byte_order_and_bootcode_crc_over_the_right_region()
    {
        var rom = MakeZ64(0x101000);
        var info = N64Cic.Analyze(rom);
        Assert.Equal("z64", info.ByteOrder);
        Assert.Equal(Crc32.Compute(rom.AsSpan(0x40, 0x1000 - 0x40)), info.BootcodeCrc32);
        // Synthetic bootcode matches no real CIC, so the checksum is reported, not asserted.
        Assert.Null(info.Cic);
        Assert.Null(info.CrcValid);
    }

    [Fact]
    public void Rejects_a_too_small_or_unrecognised_rom()
    {
        Assert.Throws<ArgumentException>(() => N64Cic.Analyze(new byte[0x100]));
        Assert.Throws<ArgumentException>(() => N64Cic.Analyze(new byte[0x1000])); // no byte-order magic
    }
}
