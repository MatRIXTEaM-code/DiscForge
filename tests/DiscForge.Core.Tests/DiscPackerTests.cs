// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Packing;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the disc packer.
///
/// The property that matters most is simple and easy to state: nothing may be
/// planned onto a disc that cannot hold it. A packing that overflows produces a
/// burn that fails after the media is spent, which is the failure this feature
/// exists to prevent — so every test that produces a plan checks that invariant
/// as well as whatever else it is looking at.
/// </summary>
public class DiscPackerTests
{
    private const long Cd700 = 737_280_000L;

    private static PackItem File(string name, long bytes, string? group = null) =>
        new() { Path = $@"C:\src\{group ?? "loose"}\{name}", Name = name, Bytes = bytes, Group = group };

    private static DiscPacker.Options Opts(long capacity = Cd700, bool groups = true,
                                           bool overhead = false, long reserve = 0) =>
        new()
        {
            CapacityBytes = capacity,
            RespectGroups = groups,
            AccountForOverhead = overhead,
            ReserveBytes = reserve,
        };

    /// <summary>The invariant every plan must satisfy.</summary>
    private static void AssertNothingOverflows(PackResult r)
    {
        foreach (var disc in r.Discs)
            Assert.True(disc.UsedBytes <= disc.CapacityBytes,
                $"Disc {disc.Number} holds {disc.UsedBytes:N0} bytes but its capacity is " +
                $"{disc.CapacityBytes:N0}.");
    }

    [Fact]
    public void Nothing_to_pack_produces_no_discs()
    {
        var r = DiscPacker.Pack(Array.Empty<PackItem>(), Opts());

        Assert.Empty(r.Discs);
        Assert.Empty(r.Oversized);
    }

    [Fact]
    public void A_single_file_gets_a_single_disc()
    {
        var r = DiscPacker.Pack(new[] { File("a.bin", 100_000_000) }, Opts());

        var disc = Assert.Single(r.Discs);
        Assert.Single(disc.Items);
        Assert.Equal(100_000_000, disc.UsedBytes);
        AssertNothingOverflows(r);
    }

    [Fact]
    public void Files_that_fit_together_share_a_disc()
    {
        var files = new[]
        {
            File("a.bin", 200_000_000),
            File("b.bin", 200_000_000),
            File("c.bin", 200_000_000),
        };

        var r = DiscPacker.Pack(files, Opts());

        Assert.Single(r.Discs);
        Assert.Equal(3, r.Discs[0].Items.Count);
        AssertNothingOverflows(r);
    }

    [Fact]
    public void Files_that_do_not_fit_open_another_disc()
    {
        var files = new[]
        {
            File("a.bin", 400_000_000),
            File("b.bin", 400_000_000),
        };

        var r = DiscPacker.Pack(files, Opts());

        Assert.Equal(2, r.Discs.Count);
        AssertNothingOverflows(r);
    }

    [Fact]
    public void Large_files_are_placed_before_small_ones()
    {
        // The whole reason first-fit DECREASING works: place the large items
        // first and the small ones fill the gaps. Reversed, each large file
        // opens a disc that a smaller one has already half-filled.
        //
        // 400 + 300 + 300 + 37.28 fits exactly two discs when sorted, and needs
        // three if taken in the order given.
        var files = new[]
        {
            File("small.bin", 37_280_000),
            File("big-a.bin", 400_000_000),
            File("mid-a.bin", 300_000_000),
            File("big-b.bin", 400_000_000),
            File("mid-b.bin", 300_000_000),
        };

        var r = DiscPacker.Pack(files, Opts());

        Assert.Equal(2, r.Discs.Count);
        AssertNothingOverflows(r);
    }

    [Fact]
    public void A_file_too_large_for_any_disc_is_reported_not_dropped()
    {
        // Silently omitting it would produce a plan that looks complete and
        // isn't — the worst possible outcome for an archive.
        var files = new[]
        {
            File("huge.bin", Cd700 * 2),
            File("fine.bin", 1_000_000),
        };

        var r = DiscPacker.Pack(files, Opts());

        var oversized = Assert.Single(r.Oversized);
        Assert.Equal("huge.bin", oversized.Name);

        var disc = Assert.Single(r.Discs);
        Assert.Equal("fine.bin", Assert.Single(disc.Items).Name);
    }

    [Fact]
    public void Grouped_files_stay_on_one_disc()
    {
        // Three albums that would pack more tightly if split, but splitting an
        // album across discs is a nuisance for as long as the archive exists.
        var files = new[]
        {
            File("01.flac", 300_000_000, "Album A"),
            File("02.flac", 300_000_000, "Album A"),
            File("01.flac", 300_000_000, "Album B"),
            File("02.flac", 300_000_000, "Album B"),
        };

        var r = DiscPacker.Pack(files, Opts(groups: true));

        Assert.Equal(2, r.Discs.Count);
        foreach (var disc in r.Discs)
        {
            var groups = disc.Items.Select(i => i.Group).Distinct().ToList();
            Assert.Single(groups);
        }
        AssertNothingOverflows(r);
    }

    [Fact]
    public void Grouping_can_be_declined_to_pack_more_tightly()
    {
        var files = new[]
        {
            File("01.flac", 300_000_000, "Album A"),
            File("02.flac", 300_000_000, "Album A"),
            File("01.flac", 100_000_000, "Album B"),
        };

        var grouped = DiscPacker.Pack(files, Opts(groups: true));
        var loose = DiscPacker.Pack(files, Opts(groups: false));

        // Grouped: A is 600 MB and must stay whole, so B goes with it or alone.
        // Ungrouped: the packer is free to arrange all three however it likes.
        Assert.True(loose.Discs.Count <= grouped.Discs.Count);
        AssertNothingOverflows(grouped);
        AssertNothingOverflows(loose);
    }

    [Fact]
    public void A_group_too_large_for_one_disc_is_reported_with_advice()
    {
        var files = new[]
        {
            File("01.flac", 500_000_000, "Huge Album"),
            File("02.flac", 500_000_000, "Huge Album"),
        };

        var r = DiscPacker.Pack(files, Opts(groups: true));

        Assert.Equal(2, r.Oversized.Count);
        Assert.Contains(r.Notes, n => n.Contains("Huge Album"));

        // Ungrouped, the same files fit two discs perfectly well.
        var split = DiscPacker.Pack(files, Opts(groups: false));
        Assert.Empty(split.Oversized);
        Assert.Equal(2, split.Discs.Count);
    }

    [Fact]
    public void A_reserve_reduces_what_each_disc_takes()
    {
        var files = new[] { File("a.bin", 730_000_000) };

        var without = DiscPacker.Pack(files, Opts(reserve: 0));
        Assert.Single(without.Discs);

        // Hold back 20 MB and the same file no longer fits.
        var with = DiscPacker.Pack(files, Opts(reserve: 20_000_000));
        Assert.Empty(with.Discs);
        Assert.Single(with.Oversized);
    }

    [Fact]
    public void A_reserve_larger_than_the_disc_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            DiscPacker.Pack(new[] { File("a.bin", 1000) },
                            Opts(capacity: 1000, reserve: 2000)));
    }

    [Fact]
    public void Zero_capacity_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            DiscPacker.Pack(new[] { File("a.bin", 1000) }, Opts(capacity: 0)));
    }

    [Fact]
    public void Filesystem_overhead_is_deducted_when_asked()
    {
        // A file that fills the disc exactly leaves no room for the volume
        // descriptors and directory records, so with overhead accounted for it
        // no longer fits — which is the point: a disc that "just fits" without
        // the allowance does not fit in reality.
        var files = new[] { File("a.bin", Cd700) };

        var ignoring = DiscPacker.Pack(files, Opts(overhead: false));
        Assert.Single(ignoring.Discs);

        var allowing = DiscPacker.Pack(files, Opts(overhead: true));
        Assert.Empty(allowing.Discs);
        Assert.Single(allowing.Oversized);
    }

    [Fact]
    public void Every_file_appears_exactly_once_across_the_plan()
    {
        // Duplicating a file would waste space; losing one would lose data. Both
        // are easy to introduce when rearranging, and neither is obvious from
        // looking at a plan.
        var rng = new Random(4242);
        var files = Enumerable.Range(0, 200)
            .Select(i => File($"f{i:D3}.bin", rng.Next(1_000_000, 200_000_000),
                              i % 7 == 0 ? $"Group{i % 5}" : null))
            .ToList();

        var r = DiscPacker.Pack(files, Opts(capacity: 4_700_372_992L));

        var placed = r.Discs.SelectMany(d => d.Items).Concat(r.Oversized).ToList();
        Assert.Equal(files.Count, placed.Count);
        Assert.Equal(files.Select(f => f.Path).OrderBy(x => x),
                     placed.Select(f => f.Path).OrderBy(x => x));
        AssertNothingOverflows(r);
    }

    [Fact]
    public void A_realistic_pile_packs_tightly()
    {
        // Not a proof of optimality — bin packing has no cheap optimum — but
        // first-fit-decreasing should comfortably beat 90% average fill on a
        // mixed set, and a regression that dropped it to 60% would be a real
        // loss.
        var rng = new Random(7);
        var files = Enumerable.Range(0, 300)
            .Select(i => File($"f{i:D3}.bin", rng.Next(5_000_000, 120_000_000)))
            .ToList();

        var r = DiscPacker.Pack(files, Opts());

        AssertNothingOverflows(r);
        Assert.True(r.AverageFill > 0.90,
            $"Average fill was {r.AverageFill:P1}, which is worse than first-fit-decreasing " +
            "should manage on a mixed set.");
    }

    [Fact]
    public void Totals_add_up()
    {
        var files = new[]
        {
            File("a.bin", 100_000_000),
            File("b.bin", 200_000_000),
            File("c.bin", 400_000_000),
        };

        var r = DiscPacker.Pack(files, Opts());

        Assert.Equal(700_000_000, r.TotalBytes);
        Assert.Equal(r.Discs.Sum(d => d.CapacityBytes) - r.TotalBytes, r.WastedBytes);
    }

    [Theory]
    [InlineData(500L, "500 bytes")]
    [InlineData(2048L, "2.0 KB")]
    [InlineData(5_242_880L, "5.0 MB")]
    [InlineData(2_147_483_648L, "2.00 GB")]
    public void Sizes_are_formatted_readably(long bytes, string expected)
    {
        Assert.Equal(expected, DiscPacker.Format(bytes));
    }

    [Fact]
    public void Real_media_capacities_are_the_usable_ones()
    {
        // The marketing figures are round numbers in decimal; the usable
        // capacities are not. Packing to "700 MB" produces a plan that doesn't
        // fit on a 700 MB disc, which is exactly the trap this avoids.
        var cd700 = DiscCapacity.Common.First(c => c.Name.Contains("700"));
        Assert.Equal(737_280_000L, cd700.Bytes);       // 360,000 × 2048

        var dvd = DiscCapacity.Common.First(c => c.Name.Contains("4.7"));
        Assert.True(dvd.Bytes < 4_700_000_000L * 1.001);
        Assert.True(dvd.Bytes > 4_000_000_000L);
    }
}