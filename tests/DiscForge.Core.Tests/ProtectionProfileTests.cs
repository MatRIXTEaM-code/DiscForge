// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Files;
using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the unified protection profile's capture-completeness synthesis: a raw (2352) image holds the
/// sector body but not subchannel; a cooked ISO holds only the filesystem markers; the physical/angular timing
/// facet is never held by a sector image; and with no scheme detected the profile reads as clean.
/// </summary>
public class ProtectionProfileTests
{
    // A raw 2352-byte/sector image with the 12-byte sync mark at each sector head, so it classifies as Bin2352.
    private static byte[] RawImage(int sectors)
    {
        var b = new byte[2352 * sectors];
        for (int s = 0; s < sectors; s++)
        {
            int o = s * 2352;
            b[o] = 0x00;
            for (int i = 1; i < 11; i++) b[o + i] = 0xFF;
            b[o + 11] = 0x00;
        }
        return b;
    }

    // A minimal ISO 9660 image: a "CD001" primary volume descriptor at LBA 16.
    private static byte[] IsoImage(int sectors)
    {
        var b = new byte[2048 * Math.Max(sectors, 17)];
        int pvd = 16 * 2048;
        b[pvd] = 0x01;                     // volume descriptor type: primary
        "CD001"u8.CopyTo(b.AsSpan(pvd + 1));
        b[pvd + 6] = 0x01;                 // version
        return b;
    }

    private static ProtectionFacet Facet(ProtectionProfile p, string prefix) =>
        p.CaptureCompleteness.Single(f => f.Name.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void A_raw_capture_holds_the_sector_body_but_not_subchannel_or_timing()
    {
        using var acc = SectorAccess.Open(new MemoryStream(RawImage(50)), ".bin", owns: true);
        var p = ProtectionProfiler.Build(acc, Array.Empty<string>());

        Assert.True(p.RawSectors);
        Assert.False(p.HasSubchannel);
        Assert.True(Facet(p, "raw sector body").Preservable);
        Assert.False(Facet(p, "subchannel").Preservable);
        Assert.False(Facet(p, "physical/angular").Preservable);
    }

    [Fact]
    public void A_cooked_iso_holds_only_filesystem_markers()
    {
        using var acc = SectorAccess.Open(new MemoryStream(IsoImage(50)), ".iso", owns: true);
        var p = ProtectionProfiler.Build(acc, Array.Empty<string>());

        Assert.False(p.RawSectors);
        Assert.True(Facet(p, "filesystem").Preservable);
        Assert.False(Facet(p, "raw sector body").Preservable);
        Assert.False(Facet(p, "subchannel").Preservable);
    }

    [Fact]
    public void A_clean_image_reports_no_protection()
    {
        using var acc = SectorAccess.Open(new MemoryStream(RawImage(20)), ".bin", owns: true);
        var p = ProtectionProfiler.Build(acc, Array.Empty<string>());

        Assert.False(p.AnyProtection);
        Assert.True(p.FullyPreserved); // vacuously — no scheme is under-captured
        Assert.Contains("No known protection", p.Summary());
    }
}
