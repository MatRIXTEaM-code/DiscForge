// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Files;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// disc-diff compares two discs at the file level by content hash. These tests drive the comparison core
/// directly with content indexes (so they need no image on disk) and confirm each category: identical,
/// added, removed, changed content at the same path, and a move/rename detected because the bytes match
/// under a different path.
/// </summary>
public class DiscDiffTests
{
    private static ContentIndex Index(params IndexedFile[] files) =>
        new() { Filesystem = "test", VolumeId = "VOL", Files = files };

    private static IndexedFile F(string path, long size, string sha) => new(path, size, sha);

    [Fact]
    public void Identical_indexes_report_identical()
    {
        var a = Index(F("/readme.txt", 10, "AA"), F("/data.bin", 4000, "BB"));
        var b = Index(F("/readme.txt", 10, "AA"), F("/data.bin", 4000, "BB"));
        var r = DiscDiff.Compare(a, b);
        Assert.True(r.Identical);
        Assert.Equal(2, r.Unchanged);
    }

    [Fact]
    public void Added_and_removed_files_are_reported()
    {
        var a = Index(F("/keep.txt", 5, "AA"), F("/gone.txt", 6, "CC"));
        var b = Index(F("/keep.txt", 5, "AA"), F("/new.txt", 7, "DD"));
        var r = DiscDiff.Compare(a, b);
        Assert.False(r.Identical);
        Assert.Contains(r.Added, f => f.Path == "/new.txt");
        Assert.Contains(r.Removed, f => f.Path == "/gone.txt");
        Assert.Equal(1, r.Unchanged);
    }

    [Fact]
    public void A_changed_file_at_the_same_path_is_reported_as_changed()
    {
        var a = Index(F("/readme.txt", 10, "AA"));
        var b = Index(F("/readme.txt", 18, "ZZ"));
        var r = DiscDiff.Compare(a, b);
        var c = Assert.Single(r.Changed);
        Assert.Equal("/readme.txt", c.Path);
        Assert.Equal(10, c.SizeA);
        Assert.Equal(18, c.SizeB);
        Assert.Empty(r.Added);
        Assert.Empty(r.Removed);
    }

    [Fact]
    public void Same_bytes_at_a_new_path_is_a_move_not_an_add_plus_remove()
    {
        var a = Index(F("/docs/moveme.txt", 20, "EE"));
        var b = Index(F("/relocated.txt", 20, "EE"));
        var r = DiscDiff.Compare(a, b);
        Assert.Empty(r.Added);
        Assert.Empty(r.Removed);
        var m = Assert.Single(r.Moved);
        Assert.Equal("/docs/moveme.txt", m.PathA);
        Assert.Equal("/relocated.txt", m.PathB);
    }
}
