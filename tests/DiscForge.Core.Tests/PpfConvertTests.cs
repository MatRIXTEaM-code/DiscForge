// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Patch;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for PPF conversion between revisions and metadata editing — the jobs
/// the old "PPF Converter" and "PPF Editor" tools did. The bar is behavioural: a
/// converted or edited patch must still apply to produce the same result, and an
/// edit must change only the metadata asked for, leaving the actual changes
/// alone.
/// </summary>
public class PpfConvertTests
{
    private static byte[] Pattern(int length, int seed = 5)
    {
        var b = new byte[length];
        int x = seed;
        for (int i = 0; i < length; i++) { x = x * 1103515245 + 12345; b[i] = (byte)(x >> 16); }
        return b;
    }

    private static (byte[] Original, byte[] Modified) Pair()
    {
        var original = Pattern(60_000);
        var modified = (byte[])original.Clone();
        Encoding.ASCII.GetBytes("PATCHED").CopyTo(modified, 40_000);
        Encoding.ASCII.GetBytes("more").CopyTo(modified, 100);
        return (original, modified);
    }

    private static byte[] Applied(byte[] ppf, byte[] original)
    {
        var target = (byte[])original.Clone();
        using var stream = new MemoryStream(target);
        PpfPatch.Apply(PpfPatch.Parse(ppf), stream, force: true);
        return stream.ToArray();
    }

    // ---- serialize round-trip ----------------------------------------------

    [Fact]
    public void A_v3_patch_serializes_back_to_an_equivalent_v3()
    {
        var (original, modified) = Pair();
        var ppf = PpfPatch.Create(original, modified);
        var patch = PpfPatch.Parse(ppf);

        var reserialized = PpfPatch.Serialize(patch);

        // Applying either produces the modified image.
        Assert.Equal(modified, Applied(reserialized, original));
        var again = PpfPatch.Parse(reserialized);
        Assert.Equal(PpfVersion.V3, again.Version);
        Assert.True(again.CanUndo);
        Assert.True(again.HasValidationBlock);
    }

    // ---- convert down -------------------------------------------------------

    [Fact]
    public void Convert_to_v1_still_applies_to_the_same_result()
    {
        var (original, modified) = Pair();
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified));

        var v1 = PpfPatch.ConvertTo(patch, PpfVersion.V1);
        var reparsed = PpfPatch.Parse(v1);

        Assert.Equal(PpfVersion.V1, reparsed.Version);
        Assert.False(reparsed.CanUndo);              // v1 has no undo
        Assert.False(reparsed.HasValidationBlock);   // v1 has no validation
        Assert.Equal(modified, Applied(v1, original));
    }

    [Fact]
    public void Convert_v3_to_v2_keeps_the_validation_block()
    {
        var (original, modified) = Pair();
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified));   // has a block

        var v2 = PpfPatch.ConvertTo(patch, PpfVersion.V2);
        var reparsed = PpfPatch.Parse(v2);

        Assert.Equal(PpfVersion.V2, reparsed.Version);
        Assert.True(reparsed.HasValidationBlock);
        Assert.Equal(modified, Applied(v2, original));
    }

    [Fact]
    public void Convert_to_v2_without_a_validation_block_is_refused()
    {
        var (original, modified) = Pair();
        // Build without validation, so there is no block to carry into v2.
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified,
            new PpfPatch.CreateOptions { IncludeValidation = false }));

        var ex = Assert.Throws<PpfFormatException>(() => PpfPatch.ConvertTo(patch, PpfVersion.V2));
        Assert.Contains("validation block", ex.Message);
    }

    [Fact]
    public void Convert_up_from_v1_to_v3_is_allowed_and_applies()
    {
        var (original, modified) = Pair();
        // Start from a v1 (no undo, no validation).
        var v1 = PpfPatch.ConvertTo(PpfPatch.Parse(PpfPatch.Create(original, modified)), PpfVersion.V1);

        var v3 = PpfPatch.ConvertTo(PpfPatch.Parse(v1), PpfVersion.V3);
        var reparsed = PpfPatch.Parse(v3);

        Assert.Equal(PpfVersion.V3, reparsed.Version);
        Assert.False(reparsed.CanUndo);   // nothing to gain undo from
        Assert.Equal(modified, Applied(v3, original));
    }

    // ---- metadata editing ---------------------------------------------------

    [Fact]
    public void Editing_the_description_changes_only_the_description()
    {
        var (original, modified) = Pair();
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified,
            new PpfPatch.CreateOptions { Description = "Old description" }));

        var edited = PpfPatch.WithMetadata(patch, description: "New and improved");
        var written = PpfPatch.Serialize(edited);
        var reparsed = PpfPatch.Parse(written);

        Assert.Equal("New and improved", reparsed.Description);
        Assert.Equal(patch.Records.Count, reparsed.Records.Count);
        Assert.Equal(modified, Applied(written, original));   // the patch still works
    }

    [Fact]
    public void Editing_the_file_id_is_preserved_through_a_round_trip()
    {
        var (original, modified) = Pair();
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified));

        var edited = PpfPatch.WithMetadata(patch, fileId: "Repacked by DiscForge 2026");
        var reparsed = PpfPatch.Parse(PpfPatch.Serialize(edited));

        Assert.NotNull(reparsed.FileId);
        Assert.Contains("Repacked by DiscForge 2026", reparsed.FileId);
    }

    [Fact]
    public void Editing_leaves_the_original_patch_object_unchanged()
    {
        var (original, modified) = Pair();
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified,
            new PpfPatch.CreateOptions { Description = "Original" }));

        _ = PpfPatch.WithMetadata(patch, description: "Changed");

        Assert.Equal("Original", patch.Description);   // records are immutable
    }
}
