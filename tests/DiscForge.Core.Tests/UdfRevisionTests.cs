// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Udf;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// UDF write revision selection (1.02 / 1.50). The two revisions are structurally
/// identical for a read-only random-access image, differing only in the revision number
/// recorded in the OSTA domain identifier suffix and the integrity descriptor. These
/// tests lock those bytes; the images are additionally validated end-to-end against
/// udftools' udfinfo (which reports udfrev=1.02 / 1.50 respectively).
/// </summary>
public class UdfRevisionTests
{
    private static IReadOnlyList<UdfBuilder.Node> Tree() => new[]
    {
        UdfBuilder.Node.File("readme.txt", Encoding.ASCII.GetBytes("hi")),
        UdfBuilder.Node.Dir("sub", new[]
        {
            UdfBuilder.Node.File("inner.txt", Encoding.ASCII.GetBytes("nested")),
        }),
    };

    // Find the 2-byte UDF revision suffix that follows an "*OSTA UDF Compliant" domain
    // identifier: the id starts at regid+1, the revision suffix at regid+24/25.
    private static ushort DomainRevision(byte[] image)
    {
        byte[] needle = Encoding.ASCII.GetBytes("*OSTA UDF Compliant");
        for (int i = 0; i + needle.Length + 2 < image.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (image[i + j] != needle[j]) { match = false; break; }
            if (match)
                return (ushort)(image[i + 23] | (image[i + 24] << 8));   // regid+24/25 from '*' at regid+1
        }
        throw new InvalidOperationException("No *OSTA UDF Compliant domain identifier found.");
    }

    [Fact]
    public void Default_build_is_udf_102()
    {
        var image = UdfBuilder.Build("TEST", Tree());
        Assert.Equal(0x0102, DomainRevision(image));
    }

    [Fact]
    public void Explicit_150_stamps_revision_0x0150()
    {
        var image = UdfBuilder.Build("TEST", Tree(), UdfBuilder.UdfRevision.Udf150);
        Assert.Equal(0x0150, DomainRevision(image));
    }

    [Fact]
    public void The_two_revisions_differ_only_in_the_revision_bytes()
    {
        var a = UdfBuilder.Build("TEST", Tree(), UdfBuilder.UdfRevision.Udf102);
        var b = UdfBuilder.Build("TEST", Tree(), UdfBuilder.UdfRevision.Udf150);
        Assert.Equal(a.Length, b.Length);

        // The images differ only where the revision is recorded (domain-id suffixes in
        // the LVD/FSD/LV-Info, and the three LVID revision fields).
        int diffs = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) diffs++;
        Assert.InRange(diffs, 1, 32);        // a handful of revision bytes, nothing structural
    }

    [Theory]
    [InlineData(UdfBuilder.UdfRevision.Udf200, 0x0200)]
    [InlineData(UdfBuilder.UdfRevision.Udf201, 0x0201)]
    public void Udf200_uses_descriptor_version_3_and_extended_file_entries(UdfBuilder.UdfRevision rev, int expected)
    {
        var image = UdfBuilder.Build("TEST", Tree(), rev);
        Assert.Equal(expected, DomainRevision(image));

        // The File Set Descriptor's tag (bytes 2-3 = descriptor version) must be 3 for 2.00+.
        // The FSD sits at partition block 0; find it by its tag id (256, little-endian).
        // Simpler and robust: no plain File Entry (tag 261) may remain — every node is an
        // Extended File Entry (tag 266). Scan sector tag ids.
        int plainFe = 0, extFe = 0, ver3 = 0;
        for (int at = 0; at + 16 <= image.Length; at += 2048)
        {
            ushort tagId = (ushort)(image[at] | (image[at + 1] << 8));
            ushort ver = (ushort)(image[at + 2] | (image[at + 3] << 8));
            if (tagId == 261) plainFe++;
            if (tagId == 266) extFe++;
            if ((tagId is >= 1 and <= 266) && ver == 3) ver3++;
        }
        Assert.True(extFe >= 1, "expected Extended File Entries on a UDF 2.00 volume");
        Assert.Equal(0, plainFe);                 // no plain File Entries remain
        Assert.True(ver3 >= 1, "expected descriptor version 3 tags");
    }

    [Fact]
    public void A_udf200_image_round_trips_through_our_own_reader()
    {
        var image = UdfBuilder.Build("TEST", Tree(), UdfBuilder.UdfRevision.Udf200);
        using var ms = new MemoryStream(image);
        Assert.True(UdfReader.IsUdf(ms));

        var vol = UdfReader.Read(ms);
        // Both files survive the write→read round trip (extended file entries + version-3 tags).
        Assert.Equal(2, vol.Files.Count());

        var readme = vol.Files.Single(f => f.Path.EndsWith("readme.txt", StringComparison.OrdinalIgnoreCase));
        using var o = new MemoryStream();
        UdfReader.ExtractFile(ms, vol, readme, o);
        Assert.Equal(Encoding.ASCII.GetBytes("hi"), o.ToArray());
    }

    [Fact]
    public void A_udf250_volume_has_a_metadata_partition_and_round_trips()
    {
        var image = UdfBuilder.Build("BDTEST", Tree(), UdfBuilder.UdfRevision.Udf250);

        // A Metadata File Entry (file type 250) and its Mirror (251) must be present.
        int metadataFe = 0, mirrorFe = 0;
        for (int at = 0; at + 32 <= image.Length; at += 2048)
        {
            ushort tagId = (ushort)(image[at] | (image[at + 1] << 8));
            if (tagId is 261 or 266)
            {
                byte fileType = image[at + 16 + 11];       // ICB tag file type
                if (fileType == 250) metadataFe++;
                if (fileType == 251) mirrorFe++;
            }
        }
        Assert.Equal(1, metadataFe);
        Assert.Equal(1, mirrorFe);

        // Reading resolves the metadata partition (the same path Blu-ray uses) and finds
        // both files with their content intact.
        using var ms = new MemoryStream(image);
        var vol = UdfReader.Read(ms);
        Assert.Equal(2, vol.Files.Count());
        var readme = vol.Files.Single(f => f.Path.EndsWith("readme.txt", StringComparison.OrdinalIgnoreCase));
        using var o = new MemoryStream();
        UdfReader.ExtractFile(ms, vol, readme, o);
        Assert.Equal(Encoding.ASCII.GetBytes("hi"), o.ToArray());
    }

    [Fact]
    public void A_udf260_volume_stamps_0x0260_keeps_the_metadata_partition_and_round_trips()
    {
        // UDF 2.60 for a mastered image is 2.50's structure with the revision bumped: same metadata
        // + mirror File Entries, descriptor version 3, and a clean write→read round trip. (The 2.60
        // pseudo-overwrite partition is BD-R incremental recording, out of scope for mastering.)
        var image = UdfBuilder.Build("BD260", Tree(), UdfBuilder.UdfRevision.Udf260);
        Assert.Equal(0x0260, DomainRevision(image));

        int metadataFe = 0, mirrorFe = 0;
        for (int at = 0; at + 32 <= image.Length; at += 2048)
        {
            ushort tagId = (ushort)(image[at] | (image[at + 1] << 8));
            if (tagId is 261 or 266)
            {
                byte fileType = image[at + 16 + 11];
                if (fileType == 250) metadataFe++;
                if (fileType == 251) mirrorFe++;
            }
        }
        Assert.Equal(1, metadataFe);
        Assert.Equal(1, mirrorFe);

        using var ms = new MemoryStream(image);
        Assert.True(UdfReader.IsUdf(ms));
        var vol = UdfReader.Read(ms);
        Assert.Equal(2, vol.Files.Count());
        var readme = vol.Files.Single(f => f.Path.EndsWith("readme.txt", StringComparison.OrdinalIgnoreCase));
        using var o = new MemoryStream();
        UdfReader.ExtractFile(ms, vol, readme, o);
        Assert.Equal(Encoding.ASCII.GetBytes("hi"), o.ToArray());
    }

    [Fact]
    public void Udf260_differs_from_udf250_only_in_the_revision_bytes()
    {
        var a = UdfBuilder.Build("SAME", Tree(), UdfBuilder.UdfRevision.Udf250);
        var b = UdfBuilder.Build("SAME", Tree(), UdfBuilder.UdfRevision.Udf260);
        Assert.Equal(a.Length, b.Length);
        int diffs = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) diffs++;
        Assert.InRange(diffs, 1, 40);        // only the revision fields, nothing structural
    }
}
