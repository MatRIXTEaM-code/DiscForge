// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using DiscForge.Core.Udf;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// udf-lint checks the UDF structures on an image against the spec. These tests build a real UDF-bridge,
/// confirm a clean volume lints clean, and then reintroduce the exact File Set Descriptor tag-location
/// bug DiscForge once shipped (an absolute sector where a partition-relative block belongs) to prove the
/// linter catches it — the check that makes strict readers report "File Set Descriptor not found". A
/// zeroed FSD and a plain ISO (no UDF) round out the behaviour.
/// </summary>
public class UdfLintTests
{
    private const int SS = 2048;

    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static IsoBuilder.Node[] Tree() => new[]
    {
        IsoBuilder.Node.File("readme.txt", Bytes("udf lint test")),
        IsoBuilder.Node.Dir("docs", new[] { IsoBuilder.Node.File("intro.txt", Bytes("nested")) }),
        IsoBuilder.Node.File("blob.bin", MakeBlob(5000)),
    };

    private static byte[] MakeBlob(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((i * 41 + 7) % 256);
        return b;
    }

    private static int PartitionStart(byte[] img)
    {
        uint mvdsLoc = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(256 * SS + 20, 4));
        uint mvdsLen = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(256 * SS + 16, 4));
        for (uint s = mvdsLoc; s < mvdsLoc + mvdsLen / SS; s++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan((int)s * SS, 2)) == 5)
                return (int)BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)s * SS + 188, 4));
        return -1;
    }

    private static void FixChecksum(byte[] img, int tagOffset)
    {
        int sum = 0;
        for (int i = 0; i < 4; i++) sum += img[tagOffset + i];
        for (int i = 5; i < 16; i++) sum += img[tagOffset + i];
        img[tagOffset + 4] = (byte)sum;
    }

    [Fact]
    public void A_clean_bridge_lints_clean()
    {
        var img = UdfBridgeBuilder.Build("ULINT", Tree());
        var r = UdfLint.Check(img);
        Assert.True(r.HasUdf);
        Assert.True(r.Ok, UdfLint.Render(r));
        Assert.Equal(0, r.Errors);
    }

    [Fact]
    public void An_absolute_fsd_tag_location_is_flagged()
    {
        var img = UdfBridgeBuilder.Build("ULINT", Tree());
        int ps = PartitionStart(img);
        Assert.True(ps > 0);

        // Re-introduce the classic bug: the FSD at partition block 0 records the ABSOLUTE sector as its
        // tag location instead of 0. Fix the tag checksum so only the location is wrong (as the real bug was).
        int fsd = ps * SS;
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(fsd + 12, 4), (uint)ps);
        FixChecksum(img, fsd);

        var r = UdfLint.Check(img);
        Assert.False(r.Ok);
        Assert.Contains(r.Findings, f => f.Severity == LintSeverity.Error &&
            f.Where == "FSD" && f.Message.Contains("partition-relative"));
    }

    [Fact]
    public void A_missing_fsd_is_flagged()
    {
        var img = UdfBridgeBuilder.Build("ULINT", Tree());
        int ps = PartitionStart(img);
        Array.Clear(img, ps * SS, SS);   // zero the FSD sector

        var r = UdfLint.Check(img);
        Assert.False(r.Ok);
        Assert.Contains(r.Findings, f => f.Where == "FSD" && f.Message.Contains("no File Set Descriptor"));
    }

    [Fact]
    public void A_plain_iso_has_no_udf()
    {
        var img = IsoBuilder.BuildTree("NOUDF", Tree(), joliet: true).Image;
        var r = UdfLint.Check(img);
        Assert.False(r.HasUdf);
        Assert.Equal(0, r.Errors);
    }
}
