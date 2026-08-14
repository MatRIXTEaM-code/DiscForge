// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// hfs-lint audits classic-Mac HFS volume structure. Its full behaviour — clean volumes, count mismatches,
/// out-of-bounds fork extents — is validated in-cloud against HFS images from two independent creators
/// (genisoimage and hfsutils' hformat) with hls as a cross-check, since there is no HFS builder to make one
/// in CI. These CI tests cover the reachable paths: a non-HFS buffer is reported as such, and an image that
/// carries the "BD" signature but no walkable catalog is reported as an error rather than crashing.
/// </summary>
public class HfsLintTests
{
    [Fact]
    public void A_non_hfs_buffer_is_reported_as_not_hfs()
    {
        var buf = new byte[4096];
        for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(i * 7);
        var r = HfsLint.Check(buf);
        Assert.False(r.IsHfs);
    }

    [Fact]
    public void An_hfs_signature_without_a_walkable_catalog_is_an_error()
    {
        var buf = new byte[8192];
        buf[0x400] = 0x42;   // 'B'
        buf[0x401] = 0x44;   // 'D'  — the HFS Master Directory Block signature
        // Allocation-block size left zero: the reader cannot establish geometry, so the catalog
        // is unwalkable and the lint must surface that as an error, not throw.
        var r = HfsLint.Check(buf);
        Assert.True(r.IsHfs);
        Assert.False(r.Ok);
        Assert.Contains(r.Findings, f => f.Severity == LintSeverity.Error);
    }
}
