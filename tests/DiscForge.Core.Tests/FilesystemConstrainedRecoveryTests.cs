// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Iso;
using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Filesystem-constrained recovery: use what the filesystem says a sector IS to reconstruct the safe
/// cases and precisely identify the rest. The pure core is driven by synthetic role maps; the ISO path
/// builds a real ISO 9660 image and erases known sectors. The contract: free space is reconstructed
/// only under a validated uniform fill (a non-uniform free class is declined, never guessed), file
/// content is bounded by name and byte range, and metadata is bounded as such.
/// </summary>
public class FilesystemConstrainedRecoveryTests
{
    private const int SS = 2048;

    private static byte[] Image(int sectors, byte fill = 0)
    {
        var b = new byte[sectors * SS];
        if (fill != 0) Array.Fill(b, fill);
        return b;
    }

    // ---- pure core (synthetic classification) ------------------------------

    [Fact]
    public void Free_space_is_reconstructed_under_a_validated_uniform_fill()
    {
        var img = Image(6, fill: 0x00);                 // all zero → free space is uniformly 0x00
        var roles = new[] { FsRole.System, FsRole.System, FsRole.Metadata, FsRole.FreeSpace, FsRole.FreeSpace, FsRole.FreeSpace };
        var labels = new string[6];
        Array.Fill(labels, "");
        // Erase a free-space sector; a surviving free sector proves the 0x00 convention.
        img.AsSpan(4 * SS, SS).Fill(0xEE);              // pretend "erased" garbage
        var r = FilesystemConstrainedRecovery.Recover(img, new long[] { 4 }, roles, labels);

        var f = Assert.Single(r.Findings);
        Assert.Equal(FcrOutcome.Recovered, f.Outcome);
        Assert.All(r.Image[(4 * SS)..(5 * SS)], b => Assert.Equal(0, b));   // rebuilt as 0x00
    }

    [Fact]
    public void A_non_uniform_free_class_is_declined_not_guessed()
    {
        var img = Image(5, fill: 0x00);
        // Two free sectors survive but disagree (one 0x00, one 0xAB) → no convention.
        img.AsSpan(3 * SS, SS).Fill(0xAB);
        var roles = new[] { FsRole.System, FsRole.Metadata, FsRole.FreeSpace, FsRole.FreeSpace, FsRole.FreeSpace };
        var labels = new string[5]; Array.Fill(labels, "");

        var r = FilesystemConstrainedRecovery.Recover(img, new long[] { 4 }, roles, labels);
        Assert.Equal(FcrOutcome.Bounded, Assert.Single(r.Findings).Outcome);   // declined
    }

    [Fact]
    public void File_data_is_bounded_by_name_and_byte_range_never_reconstructed()
    {
        var img = Image(4, fill: 0x00);
        var roles = new[] { FsRole.System, FsRole.Metadata, FsRole.FileData, FsRole.FreeSpace };
        var labels = new[] { "", "", "/GAME.DAT bytes 0..2,048", "" };

        var r = FilesystemConstrainedRecovery.Recover(img, new long[] { 2 }, roles, labels);
        var f = Assert.Single(r.Findings);
        Assert.Equal(FcrOutcome.Bounded, f.Outcome);
        Assert.Equal(FsRole.FileData, f.Role);
        Assert.Contains("GAME.DAT", f.Detail);
    }

    // ---- ISO integration ---------------------------------------------------

    [Fact]
    public void An_erased_file_sector_of_a_real_iso_is_identified_by_file()
    {
        var content = new byte[5000];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i * 7 + 1);
        var iso = IsoBuilder.Build("DISC", new[] { new IsoBuilder.FileEntry("BIG.BIN", content) }).Image;

        var (roles, labels) = FilesystemConstrainedRecovery.BuildIsoMap(iso);
        // Find a sector classified as this file's data and "erase" it.
        long fileSector = Array.FindIndex(roles, x => x == FsRole.FileData);
        Assert.True(fileSector >= 0);

        var r = FilesystemConstrainedRecovery.RecoverIso(iso, new long[] { fileSector });
        var f = Assert.Single(r.Findings);
        Assert.Equal(FcrOutcome.Bounded, f.Outcome);
        Assert.Contains("BIG.BIN", f.Detail);
    }

    [Fact]
    public void Erased_free_tail_of_a_real_iso_is_reconstructed()
    {
        var iso = IsoBuilder.Build("DISC", new[] { new IsoBuilder.FileEntry("A.TXT", Encoding.ASCII.GetBytes("hello")) }).Image;
        // Append three zero-filled tail sectors (free space): erase one, leave two to prove the
        // 0x00 free-space convention.
        var padded = new byte[iso.Length + 3 * SS];
        iso.CopyTo(padded, 0);
        long tail = iso.Length / SS;

        var (roles, _) = FilesystemConstrainedRecovery.BuildIsoMap(padded);
        Assert.Equal(FsRole.FreeSpace, roles[tail]);

        padded.AsSpan((int)(tail * SS), SS).Fill(0x99);     // erased garbage in one free tail sector
        var r = FilesystemConstrainedRecovery.RecoverIso(padded, new long[] { tail });

        Assert.Equal(FcrOutcome.Recovered, Assert.Single(r.Findings).Outcome);
        Assert.All(r.Image[(int)(tail * SS)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_within_volume_unaccounted_sector_is_never_reconstructed()
    {
        // Safety regression: a sector INSIDE the declared volume that the classifier couldn't place
        // (here a Joliet secondary-namespace directory) must never be reconstructed as free-space fill —
        // it could be real content the reader didn't enumerate. Only padding beyond the Volume Space
        // Size is reconstructable. Even though the free tail is uniform 0x00, this sector stays untouched.
        var iso = IsoBuilder.Build("DISC", new[] { new IsoBuilder.FileEntry("A.TXT", Encoding.ASCII.GetBytes("hi")) }).Image;
        var (roles, _) = FilesystemConstrainedRecovery.BuildIsoMap(iso);

        long unaccounted = Array.FindIndex(roles, r => r == FsRole.Unknown);
        Assert.True(unaccounted >= 0, "expected an unresolved within-volume sector on a Joliet ISO");

        var r = FilesystemConstrainedRecovery.RecoverIso(iso, new[] { unaccounted });
        var f = Assert.Single(r.Findings);
        Assert.NotEqual(FcrOutcome.Recovered, f.Outcome);       // NOT zeroed — it may be content
    }

    [Fact]
    public void A_length_mismatch_between_roles_and_image_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            FilesystemConstrainedRecovery.Recover(Image(3), Array.Empty<long>(), new FsRole[2], new string[2]));
    }
}
