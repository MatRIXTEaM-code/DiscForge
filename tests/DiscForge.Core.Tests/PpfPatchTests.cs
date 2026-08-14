// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Patch;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the PPF patch engine — the format PPF-O-Matic, the PPF Patch
/// Engine and PAL region patchers speak.
///
/// Two things are pinned. First, that patches DiscForge writes round-trip: a
/// created patch applied to the original reproduces the modified image exactly,
/// and its undo data restores the original. Second, that patches DiscForge did
/// NOT write — hand-built PPF 1.0 and 2.0 byte streams matching the published
/// layout — parse and apply correctly, because interop with real patch files is
/// the whole point.
///
/// The safety behaviours matter as much as the happy path: a patch must refuse
/// an image whose validation block does not match (applying it would corrupt
/// the wrong file), and must refuse an image too short for its records.
/// </summary>
public class PpfPatchTests
{
    private static byte[] Pattern(int length, int seed = 7)
    {
        var b = new byte[length];
        int x = seed;
        for (int i = 0; i < length; i++) { x = x * 1103515245 + 12345; b[i] = (byte)(x >> 16); }
        return b;
    }

    private static byte[] With(byte[] src, params (int Offset, byte[] Bytes)[] edits)
    {
        var copy = (byte[])src.Clone();
        foreach (var (offset, bytes) in edits)
            Array.Copy(bytes, 0, copy, offset, bytes.Length);
        return copy;
    }

    // ---- create / apply round trip -----------------------------------------

    [Fact]
    public void A_created_patch_turns_the_original_into_the_modified_image()
    {
        var original = Pattern(50_000);
        var modified = With(original,
            (100, Encoding.ASCII.GetBytes("HELLO")),
            (0x9400, Encoding.ASCII.GetBytes("a changed region of the disc")),
            (49_990, Encoding.ASCII.GetBytes("theend")));

        var ppf = PpfPatch.Create(original, modified);
        var target = (byte[])original.Clone();
        using var stream = new MemoryStream(target);
        stream.SetLength(target.Length);

        int applied = PpfPatch.Apply(PpfPatch.Parse(ppf), stream);

        Assert.True(applied > 0);
        Assert.Equal(modified, stream.ToArray());
    }

    [Fact]
    public void Undo_data_restores_the_original_after_applying()
    {
        var original = Pattern(20_000);
        var modified = With(original, (5000, Encoding.ASCII.GetBytes("patched here")));
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified));

        var buffer = (byte[])original.Clone();
        using var stream = new MemoryStream();
        stream.Write(buffer); stream.Position = 0;

        PpfPatch.Apply(patch, stream);
        Assert.Equal(modified, stream.ToArray());

        PpfPatch.Undo(patch, stream);
        Assert.Equal(original, stream.ToArray());
    }

    [Fact]
    public void Identical_images_produce_a_patch_with_no_records()
    {
        var image = Pattern(10_000);
        var patch = PpfPatch.Parse(PpfPatch.Create(image, (byte[])image.Clone()));

        Assert.Empty(patch.Records);
    }

    [Fact]
    public void A_run_longer_than_255_bytes_is_split_across_records()
    {
        var original = Pattern(2_000);
        // 600 contiguous differing bytes → at least three records (255+255+90).
        var modified = With(original, (500, new byte[600]));
        // Ensure the run really differs everywhere (zeros might match originals):
        for (int i = 500; i < 1100; i++) modified[i] = (byte)~original[i];

        var records = PpfPatch.DiffToRecords(original, modified, includeUndo: true);

        Assert.True(records.Count >= 3);
        Assert.All(records, r => Assert.True(r.Data.Length <= 255));
        Assert.Equal(600, records.Sum(r => r.Data.Length));
        Assert.Equal(500, records[0].Offset);
    }

    [Fact]
    public void Create_refuses_images_of_different_lengths()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PpfPatch.Create(Pattern(1000), Pattern(1001)));
        Assert.Contains("same length", ex.Message);
    }

    [Fact]
    public void A_patch_written_without_undo_cannot_be_reverted()
    {
        var original = Pattern(9_400);
        var modified = With(original, (200, Encoding.ASCII.GetBytes("x")));
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified,
            new PpfPatch.CreateOptions { IncludeUndo = false }));

        Assert.False(patch.CanUndo);
        using var stream = new MemoryStream((byte[])original.Clone());
        Assert.Throws<PpfFormatException>(() => PpfPatch.Undo(patch, stream));
    }

    // ---- validation ---------------------------------------------------------

    [Fact]
    public void A_patch_with_a_validation_block_matches_its_own_original()
    {
        var original = Pattern(60_000);            // long enough for the 0x9320 block
        var modified = With(original, (40_000, Encoding.ASCII.GetBytes("edit")));
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified));

        Assert.True(patch.HasValidationBlock);
        using var stream = new MemoryStream((byte[])original.Clone());
        var check = PpfPatch.CheckApplicable(patch, stream);

        Assert.True(check.Ok);
        Assert.True(check.ValidationMatched);
    }

    [Fact]
    public void A_patch_refuses_an_image_whose_validation_block_differs()
    {
        var original = Pattern(60_000);
        var modified = With(original, (40_000, Encoding.ASCII.GetBytes("edit")));
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified));

        var wrongImage = Pattern(60_000, seed: 999);   // a different disc, same size
        using var stream = new MemoryStream(wrongImage);

        var check = PpfPatch.CheckApplicable(patch, stream);
        Assert.False(check.Ok);
        Assert.Contains("does not match", check.Problem);

        var ex = Assert.Throws<PpfFormatException>(() => PpfPatch.Apply(patch, stream));
        Assert.Contains("validation block", ex.Message);
        // And nothing was written.
        Assert.Equal(wrongImage, stream.ToArray());
    }

    [Fact]
    public void Force_applies_despite_a_validation_mismatch()
    {
        var original = Pattern(60_000);
        var modified = With(original, (40_000, Encoding.ASCII.GetBytes("edit")));
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified));

        var wrongImage = Pattern(60_000, seed: 42);
        using var stream = new MemoryStream(wrongImage);

        int applied = PpfPatch.Apply(patch, stream, force: true);
        Assert.Equal(patch.Records.Count, applied);
    }

    [Fact]
    public void A_patch_refuses_an_image_too_short_for_its_records()
    {
        var original = Pattern(60_000);
        var modified = With(original, (59_000, Encoding.ASCII.GetBytes("near the end")));
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified,
            new PpfPatch.CreateOptions { IncludeValidation = false }));

        using var tooShort = new MemoryStream(Pattern(40_000));
        var check = PpfPatch.CheckApplicable(patch, tooShort);

        Assert.False(check.Ok);
        Assert.Contains("larger or different image", check.Problem);
    }

    // ---- description and file_id -------------------------------------------

    [Fact]
    public void The_description_is_stored_and_read_back_trimmed()
    {
        var patch = PpfPatch.Parse(PpfPatch.Create(Pattern(1000), Pattern(1000),
            new PpfPatch.CreateOptions { Description = "Silent Hill PAL to NTSC" }));

        Assert.Equal("Silent Hill PAL to NTSC", patch.Description);
    }

    [Fact]
    public void A_description_longer_than_fifty_characters_is_truncated()
    {
        var longText = new string('X', 80);
        var patch = PpfPatch.Parse(PpfPatch.Create(Pattern(1000), Pattern(1000),
            new PpfPatch.CreateOptions { Description = longText }));

        Assert.Equal(50, patch.Description.Length);
    }

    [Fact]
    public void A_file_id_is_written_and_read_back()
    {
        var original = Pattern(2000);
        var modified = With(original, (10, new byte[] { 1, 2, 3 }));
        var patch = PpfPatch.Parse(PpfPatch.Create(original, modified,
            new PpfPatch.CreateOptions { FileId = "Translation v1.2 by Team" }));

        Assert.NotNull(patch.FileId);
        Assert.Contains("Translation v1.2 by Team", patch.FileId);
        // The file_id must not have been mistaken for patch records.
        Assert.Single(patch.Records);
    }

    // ---- refusals -----------------------------------------------------------

    [Fact]
    public void A_file_without_a_ppf_magic_is_refused()
    {
        var ex = Assert.Throws<PpfFormatException>(
            () => PpfPatch.Parse(Encoding.ASCII.GetBytes("NOTAPPFFILE................")));
        Assert.Contains("Not a PPF", ex.Message);
    }

    [Fact]
    public void A_file_too_short_for_a_magic_is_refused()
    {
        Assert.Throws<PpfFormatException>(() => PpfPatch.Parse(new byte[] { 1, 2, 3 }));
    }

    // ---- interop: hand-built PPF 1.0 and 2.0 --------------------------------

    private static byte[] BuildV1(string description, params (long Offset, byte[] Data)[] records)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("PPF10"));
        ms.WriteByte(0);                                  // method
        var desc = new byte[50];
        Encoding.ASCII.GetBytes(description).CopyTo(desc, 0);
        ms.Write(desc);
        Span<byte> off = stackalloc byte[4];
        foreach (var (offset, data) in records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(off, (uint)offset);
            ms.Write(off);
            ms.WriteByte((byte)data.Length);
            ms.Write(data);
        }
        return ms.ToArray();
    }

    private static byte[] BuildV2(byte[] original, params (long Offset, byte[] Data)[] records)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("PPF20"));
        ms.WriteByte(1);
        ms.Write(new byte[50]);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)original.Length);
        ms.Write(size);
        ms.Write(original.AsSpan(0x9320, 1024));          // validation block
        Span<byte> off = stackalloc byte[4];
        foreach (var (offset, data) in records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(off, (uint)offset);
            ms.Write(off);
            ms.WriteByte((byte)data.Length);
            ms.Write(data);
        }
        return ms.ToArray();
    }

    [Fact]
    public void A_hand_built_ppf1_parses_and_applies()
    {
        var ppf = BuildV1("An old-style patch",
            (16, Encoding.ASCII.GetBytes("AAAA")),
            (2048, Encoding.ASCII.GetBytes("BB")));
        var patch = PpfPatch.Parse(ppf);

        Assert.Equal(PpfVersion.V1, patch.Version);
        Assert.Equal("An old-style patch", patch.Description);
        Assert.Equal(2, patch.Records.Count);
        Assert.False(patch.HasValidationBlock);
        Assert.False(patch.CanUndo);

        using var stream = new MemoryStream(new byte[4096]);
        PpfPatch.Apply(patch, stream);
        var result = stream.ToArray();
        Assert.Equal(Encoding.ASCII.GetBytes("AAAA"), result[16..20]);
        Assert.Equal(Encoding.ASCII.GetBytes("BB"), result[2048..2050]);
    }

    [Fact]
    public void A_hand_built_ppf2_validates_against_its_original()
    {
        var original = Pattern(60_000);
        var ppf = BuildV2(original, (30_000, Encoding.ASCII.GetBytes("patched")));
        var patch = PpfPatch.Parse(ppf);

        Assert.Equal(PpfVersion.V2, patch.Version);
        Assert.True(patch.HasValidationBlock);
        Assert.Equal(0x9320, patch.ValidationOffset);
        Assert.Equal(60_000, patch.OriginalSize);

        using var right = new MemoryStream((byte[])original.Clone());
        Assert.True(PpfPatch.CheckApplicable(patch, right).ValidationMatched);

        using var wrong = new MemoryStream(Pattern(60_000, seed: 3));
        Assert.False(PpfPatch.CheckApplicable(patch, wrong).Ok);
    }

    [Fact]
    public void A_ppf1_offset_is_little_endian()
    {
        // Offset 0x00010000 = 65536. Read big-endian it would be 0x00000100.
        var ppf = BuildV1("le", (0x10000, new byte[] { 0xFF }));
        var patch = PpfPatch.Parse(ppf);
        Assert.Equal(0x10000, patch.Records[0].Offset);
    }

    [Fact]
    public void A_ppf3_offset_beyond_four_gigabytes_survives_the_64_bit_field()
    {
        // Only PPF 3.0's 8-byte offset can express this. Build one by hand at a
        // high offset and confirm it reads back — the reason PPF3 exists.
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("PPF30"));
        ms.WriteByte(0x02);
        ms.Write(new byte[50]);
        ms.WriteByte(0);          // image type
        ms.WriteByte(0);          // no block
        ms.WriteByte(0);          // no undo
        ms.WriteByte(0);          // dummy
        Span<byte> off = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(off, 0x1_0000_0100UL);
        ms.Write(off);
        ms.WriteByte(1);
        ms.WriteByte(0xAB);
        var patch = PpfPatch.Parse(ms.ToArray());

        Assert.Equal(0x1_0000_0100L, patch.Records[0].Offset);
    }

    [Fact]
    public void A_truncated_record_is_reported_not_silently_dropped()
    {
        // A PPF 1.0 promising 10 data bytes but carrying 2.
        var ppf = BuildV1("bad");
        var broken = ppf.Concat(new byte[] { 0x10, 0, 0, 0, 10, 1, 2 }).ToArray();
        Assert.Throws<PpfFormatException>(() => PpfPatch.Parse(broken));
    }
}
