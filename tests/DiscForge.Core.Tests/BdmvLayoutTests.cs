// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.DvdVideo;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The BD-Video (BDMV) structure validator — the front half of the Blu-ray assembler,
/// which builds the validated tree into a pure UDF 2.50 image.
/// </summary>
public class BdmvLayoutTests
{
    private static string[] ValidBdmv() => new[]
    {
        "BDMV/index.bdmv",
        "BDMV/MovieObject.bdmv",
        "BDMV/PLAYLIST/00000.mpls",
        "BDMV/PLAYLIST/00001.mpls",
        "BDMV/CLIPINF/00000.clpi",
        "BDMV/STREAM/00000.m2ts",
        "BDMV/BACKUP/index.bdmv",
        "BDMV/BACKUP/MovieObject.bdmv",
    };

    [Fact]
    public void A_well_formed_bdmv_is_valid()
    {
        var v = BdmvLayout.Validate(ValidBdmv());
        Assert.True(v.IsValid);
        Assert.Equal(2, v.PlaylistCount);
        Assert.Equal(1, v.ClipCount);
        Assert.Equal(1, v.StreamCount);
        Assert.True(v.HasBackup);
        Assert.Empty(v.Warnings);
    }

    [Fact]
    public void Case_and_backslashes_are_normalised()
    {
        var v = BdmvLayout.Validate(new[]
        {
            "bdmv\\INDEX.BDMV", "BDMV\\movieobject.bdmv",
            "BDMV\\playlist\\00000.MPLS", "BDMV\\CLIPINF\\00000.clpi", "BDMV\\stream\\00000.M2TS",
            "BDMV\\BACKUP\\index.bdmv",
        });
        Assert.True(v.IsValid);
    }

    [Fact]
    public void A_missing_index_is_fatal()
    {
        var v = BdmvLayout.Validate(new[]
        {
            "BDMV/MovieObject.bdmv", "BDMV/PLAYLIST/00000.mpls",
            "BDMV/CLIPINF/00000.clpi", "BDMV/STREAM/00000.m2ts",
        });
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("index.bdmv"));
    }

    [Fact]
    public void No_streams_is_fatal()
    {
        var v = BdmvLayout.Validate(new[]
        {
            "BDMV/index.bdmv", "BDMV/MovieObject.bdmv",
            "BDMV/PLAYLIST/00000.mpls", "BDMV/CLIPINF/00000.clpi",
        });
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("m2ts"));
    }

    [Fact]
    public void A_missing_backup_is_only_a_warning()
    {
        var v = BdmvLayout.Validate(new[]
        {
            "BDMV/index.bdmv", "BDMV/MovieObject.bdmv",
            "BDMV/PLAYLIST/00000.mpls", "BDMV/CLIPINF/00000.clpi", "BDMV/STREAM/00000.m2ts",
        });
        Assert.True(v.IsValid);
        Assert.Contains(v.Warnings, w => w.Contains("BACKUP"));
    }

    [Fact]
    public void Mismatched_clip_and_stream_counts_warn()
    {
        var v = BdmvLayout.Validate(new[]
        {
            "BDMV/index.bdmv", "BDMV/MovieObject.bdmv", "BDMV/PLAYLIST/00000.mpls",
            "BDMV/CLIPINF/00000.clpi", "BDMV/CLIPINF/00001.clpi",
            "BDMV/STREAM/00000.m2ts", "BDMV/BACKUP/index.bdmv",
        });
        Assert.True(v.IsValid);
        Assert.Contains(v.Warnings, w => w.Contains("one clip per stream"));
    }
}
