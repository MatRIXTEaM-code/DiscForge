// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// CHD extraction of an uncompressed (NONE) hunk — the case chdman emits when a hunk
/// is incompressible, so it stores the hunk's bytes verbatim. This is a real chdman
/// CD CHD of 8 incompressible (pseudo-random) MODE1 sectors, so its single hunk is
/// stored NONE. Extraction decodes the compressed map (which marks the hunk NONE),
/// copies the bytes verbatim, and the CHD's stored SHA-1 proves the result is
/// byte-exact.
/// </summary>
public class ChdNoneHunkTests
{
    // chdman createcd on 8 random MODE1/2352 sectors with -c cdzl,cdlz (no FLAC, so
    // the incompressible data falls back to NONE). One 8-frame hunk, stored verbatim.
    private const string ChdBase64 =
        "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";

    private static byte[] Sample() => System.Convert.FromBase64String(ChdBase64);

    [Fact]
    public void An_all_none_cd_chd_extracts_and_verifies()
    {
        var result = ChdExtractor.ExtractCd(Sample());
        Assert.True(result.Verified);          // reconstruction matched the CHD SHA-1
        Assert.Equal(1, result.Tracks);
        Assert.Equal(8 * 2352, result.Bin.Length);
    }
}
