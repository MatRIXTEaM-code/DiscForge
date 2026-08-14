// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Fat;
using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// fat-lint audits FAT volume structure. Its full behaviour (clean volumes, lost clusters, diverged FAT
/// copies, broken/truncated cluster chains) is cross-validated in-cloud against dosfsck on real mkfs.fat
/// images; these CI tests hand-craft a minimal empty FAT12 and confirm the checks that need no populated
/// tree: a clean volume lints clean, an allocated-but-unreferenced cluster is reported as lost, and a
/// divergence between the two FAT copies is caught.
/// </summary>
public class FatLintTests
{
    private const int Bps = 512, Reserved = 1, NumFats = 2, FatSz = 1, TotSec = 64;
    private static int Fat0 => Reserved * Bps;
    private static int Fat1 => Fat0 + FatSz * Bps;

    /// <summary>A minimal, valid, empty FAT12 volume.</summary>
    private static byte[] BuildFat12()
    {
        var img = new byte[TotSec * Bps];
        img[0] = 0xEB; img[1] = 0x3C; img[2] = 0x90;                 // jump
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0B), Bps);
        img[0x0D] = 1;                                               // sectors per cluster
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0E), Reserved);
        img[0x10] = NumFats;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x11), 16);   // root entries
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x13), TotSec);
        img[0x15] = 0xF0;                                            // media descriptor
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x16), FatSz);
        img[0x1FE] = 0x55; img[0x1FF] = 0xAA;
        // FAT reserved entries (media + EOC) in both copies.
        foreach (var fs in new[] { Fat0, Fat1 }) { img[fs] = 0xF0; img[fs + 1] = 0xFF; img[fs + 2] = 0xFF; }
        return img;
    }

    private static void SetFat12(byte[] img, int fatStart, int n, int val)
    {
        int o = fatStart + n + n / 2;
        int pair = img[o] | (img[o + 1] << 8);
        pair = (n & 1) == 1 ? (pair & 0x000F) | (val << 4) : (pair & 0xF000) | (val & 0xFFF);
        img[o] = (byte)(pair & 0xFF);
        img[o + 1] = (byte)((pair >> 8) & 0xFF);
    }

    [Fact]
    public void A_clean_empty_volume_lints_clean()
    {
        var r = FatLint.Check(BuildFat12());
        Assert.True(r.IsFat);
        Assert.Equal(FatType.Fat12, r.Type);
        Assert.True(r.Ok);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void An_allocated_unreferenced_cluster_is_reported_lost()
    {
        var img = BuildFat12();
        SetFat12(img, Fat0, 5, 0xFFF);   // allocate cluster 5 (EOC) in both copies, referenced by nobody
        SetFat12(img, Fat1, 5, 0xFFF);

        var r = FatLint.Check(img);
        Assert.Contains(r.Findings, f => f.Severity == LintSeverity.Warning && f.Message.Contains("lost"));
    }

    [Fact]
    public void Diverged_fat_copies_are_caught()
    {
        var img = BuildFat12();
        img[Fat1 + 20] ^= 0xFF;          // corrupt only the second FAT copy

        var r = FatLint.Check(img);
        Assert.Contains(r.Findings, f => f.Severity == LintSeverity.Warning && f.Message.Contains("diverged"));
    }
}
