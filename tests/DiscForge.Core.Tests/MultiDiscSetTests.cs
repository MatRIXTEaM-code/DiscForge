// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.IO;
using System.Text;
using DiscForge.Core.Convert;
using DiscForge.Core.Library;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for multi-disc set detection (the "(Disc N)" / "(Disc N of M)" naming
/// convention), the per-disc hash manifest, and the PSIO MULTIDISC.LST byte format.
/// </summary>
public class MultiDiscSetTests
{
    // ---- detection --------------------------------------------------------------

    [Fact]
    public void Two_discs_of_the_same_title_are_grouped_in_order()
    {
        var titles = MultiDiscDetector.Detect(new[]
        {
            "/games/Epic RPG (USA) (Disc 2).cue",
            "/games/Epic RPG (USA) (Disc 1).cue",
        });

        var t = Assert.Single(titles);
        Assert.Equal("Epic RPG (USA)", t.Title);
        Assert.True(t.Complete);
        Assert.Equal(new[] { 1, 2 }, t.Discs.Select(d => d.DiscNumber));
        Assert.Equal(new[] { "/games/Epic RPG (USA) (Disc 1).cue", "/games/Epic RPG (USA) (Disc 2).cue" }, t.OrderedPaths);
    }

    [Fact]
    public void A_missing_middle_disc_is_reported_incomplete()
    {
        var titles = MultiDiscDetector.Detect(new[]
        {
            "/g/Saga (Disc 1).cue",
            "/g/Saga (Disc 3).cue",
        });
        var t = Assert.Single(titles);
        Assert.False(t.Complete);
        Assert.Equal(new[] { 2 }, t.MissingDiscNumbers);
    }

    [Fact]
    public void A_declared_total_beyond_what_was_found_is_reported_incomplete()
    {
        var titles = MultiDiscDetector.Detect(new[] { "/g/Trilogy (Disc 1 of 3).cue" });
        var t = Assert.Single(titles);
        Assert.False(t.Complete);
        Assert.Equal(new[] { 2, 3 }, t.MissingDiscNumbers);
    }

    [Fact]
    public void A_lone_disc_with_no_declared_total_and_no_sibling_is_not_a_set()
    {
        // "(Disc 1)" alone, no "of N" and no sibling disc 2+, is ambiguous — not reported.
        var titles = MultiDiscDetector.Detect(new[] { "/g/Solo Game (Disc 1).cue" });
        Assert.Empty(titles);
    }

    [Fact]
    public void Untagged_files_are_ignored()
    {
        var titles = MultiDiscDetector.Detect(new[] { "/g/Some Game (USA).cue", "/g/readme.txt" });
        Assert.Empty(titles);
    }

    [Fact]
    public void Matching_is_case_insensitive_on_the_disc_tag()
    {
        var titles = MultiDiscDetector.Detect(new[] { "/g/X (disc 1).cue", "/g/X (DISC 2).cue" });
        var t = Assert.Single(titles);
        Assert.Equal(2, t.Discs.Count);
    }

    [Fact]
    public void Same_title_in_different_folders_is_not_merged()
    {
        var titles = MultiDiscDetector.Detect(new[]
        {
            "/a/X (Disc 1).cue", "/a/X (Disc 2).cue",
            "/b/X (Disc 1).cue", "/b/X (Disc 2).cue",
        });
        Assert.Equal(2, titles.Count);
    }

    [Fact]
    public void A_duplicate_disc_number_keeps_the_first_occurrence()
    {
        var titles = MultiDiscDetector.Detect(new[]
        {
            "/g/Y (Disc 1).cue", "/g/Y (Disc 1).bin", "/g/Y (Disc 2).cue",
        });
        var t = Assert.Single(titles);
        Assert.Equal(2, t.Discs.Count);
        Assert.Equal("/g/Y (Disc 1).cue", t.Discs.Single(d => d.DiscNumber == 1).Path);
    }

    [Fact]
    public void Blank_and_null_like_entries_are_skipped_without_throwing()
    {
        var titles = MultiDiscDetector.Detect(new[] { "", "   ", "/g/Z (Disc 1).cue", "/g/Z (Disc 2).cue" });
        Assert.Single(titles);
    }

    // ---- manifest -----------------------------------------------------------------

    [Fact]
    public void The_manifest_hashes_every_disc_and_carries_completeness()
    {
        var title = new MultiDiscTitle
        {
            Title = "Set",
            Discs = new[]
            {
                new MultiDiscEntry { DiscNumber = 1, Path = "one" },
                new MultiDiscEntry { DiscNumber = 2, Path = "two" },
            },
            MissingDiscNumbers = Array.Empty<int>(),
        };

        var manifest = MultiDiscManifestBuilder.Build(title, path =>
            new MemoryStream(Encoding.ASCII.GetBytes(path == "one" ? "AAAA" : "BBBBBB")));

        Assert.Equal("Set", manifest.Title);
        Assert.True(manifest.Complete);
        Assert.Equal(2, manifest.Discs.Count);
        Assert.Equal(4, manifest.Discs.Single(d => d.DiscNumber == 1).Bytes);
        Assert.Equal(6, manifest.Discs.Single(d => d.DiscNumber == 2).Bytes);
        Assert.All(manifest.Discs, d => Assert.Equal(64, d.Sha256.Length));
        Assert.Contains("\"title\"", manifest.Json(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_manifest_propagates_missing_disc_numbers_as_incomplete()
    {
        var title = new MultiDiscTitle
        {
            Title = "Gappy",
            Discs = new[] { new MultiDiscEntry { DiscNumber = 1, Path = "one" } },
            MissingDiscNumbers = new[] { 2 },
        };
        var manifest = MultiDiscManifestBuilder.Build(title, _ => new MemoryStream(new byte[] { 1, 2, 3 }));
        Assert.False(manifest.Complete);
        Assert.Equal(new[] { 2 }, manifest.MissingDiscNumbers);
    }

    // ---- PSIO MULTIDISC.LST ------------------------------------------------------

    [Fact]
    public void BuildLst_joins_leaf_names_with_CRLF_and_no_trailing_terminator()
    {
        string lst = PsioMultiDisc.BuildLst(new[] { @"C:\sd\Game\Game (Disc 1).cue", @"C:\sd\Game\Game (Disc 2).cue" });
        Assert.Equal("Game (Disc 1).cue\r\nGame (Disc 2).cue", lst);
        Assert.False(lst.EndsWith("\r\n", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildLst_strips_directories_leaving_bare_file_names()
    {
        string lst = PsioMultiDisc.BuildLst(new[] { "/mnt/sd/G/a.cue", "/mnt/sd/G/b.cue" });
        Assert.Equal("a.cue\r\nb.cue", lst);
    }

    [Fact]
    public void BuildLst_rejects_an_empty_list()
    {
        Assert.Throws<ArgumentException>(() => PsioMultiDisc.BuildLst(Array.Empty<string>()));
    }
}
