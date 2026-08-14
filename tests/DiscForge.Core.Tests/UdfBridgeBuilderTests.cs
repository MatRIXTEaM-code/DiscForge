// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Iso;
using DiscForge.Core.Udf;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the UDF-bridge builder. A bridge image must be readable as BOTH
/// ISO 9660 (with Joliet) AND UDF 1.02, and — the defining property — every
/// file's data must be stored exactly ONCE, with both filesystems' directory
/// entries pointing at the SAME absolute sectors.
///
/// So each tree is built, then read back through DiscForge's own two oracles:
/// <see cref="IsoReader"/> for the ISO 9660 side and <see cref="UdfReader"/> for
/// the UDF side. A file that round-trips byte-exact through both, and whose ISO
/// extent equals its UDF File Entry's resolved data sector (checked straight from
/// the image bytes), is the honest proof the bridge is real and not two disjoint
/// filesystems sharing a container.
/// </summary>
public class UdfBridgeBuilderTests
{
    private const int SS = 2048;

    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static IsoBuilder.Node File(string name, byte[] data) => IsoBuilder.Node.File(name, data);
    private static IsoBuilder.Node Dir(string name, params IsoBuilder.Node[] children) =>
        IsoBuilder.Node.Dir(name, children);

    private static byte[] IsoExtract(byte[] image, IsoEntry entry)
    {
        using var img = new MemoryStream(image);
        using var o = new MemoryStream();
        IsoReader.ExtractFile(img, entry, o);
        return o.ToArray();
    }

    private static byte[] UdfExtract(byte[] image, UdfVolume vol, UdfEntry entry)
    {
        using var img = new MemoryStream(image);
        using var o = new MemoryStream();
        UdfReader.ExtractFile(img, vol, entry, o);
        return o.ToArray();
    }

    /// <summary>Resolve the absolute data sector a UDF File Entry points at, parsed
    /// straight from the image bytes: partitionStart + the first short_ad's block.</summary>
    private static uint UdfDataSector(byte[] image, uint partitionStart, uint icbBlock)
    {
        int fe = (int)((partitionStart + icbBlock) * SS);
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(fe, 2));
        Assert.True(tag is 261 or 266, "entry is not a (Extended) File Entry");
        int lenEa, adOff;
        if (tag == 261) { lenEa = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(fe + 0xA8, 4)); adOff = fe + 0xB0 + lenEa; }
        else            { lenEa = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(fe + 0xD0, 4)); adOff = fe + 0xD8 + lenEa; }
        uint block = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(adOff + 4, 4));
        return partitionStart + block;
    }

    // ---- both filesystems are present and recognised ------------------------

    [Fact]
    public void A_bridge_image_is_recognised_as_both_iso9660_and_udf()
    {
        var image = UdfBridgeBuilder.Build("BRIDGE", new[] { File("A.TXT", Bytes("hi")) });
        using var ms = new MemoryStream(image);

        Assert.True(UdfReader.IsUdf(ms));                 // UDF side
        ms.Position = 0;
        var iso = IsoReader.Read(ms);                     // ISO side
        Assert.Equal("BRIDGE", iso.VolumeId);
    }

    [Fact]
    public void The_volume_id_round_trips_on_both_filesystems()
    {
        var image = UdfBridgeBuilder.Build("MY_BRIDGE", new[] { File("X", Bytes("x")) });
        using var ms = new MemoryStream(image);
        var iso = IsoReader.Read(ms);
        ms.Position = 0;
        var udf = UdfReader.Read(ms);

        Assert.Equal("MY_BRIDGE", iso.VolumeId);
        Assert.Equal("MY_BRIDGE", udf.VolumeId);
    }

    // ---- byte-exact round trip through both readers -------------------------

    [Fact]
    public void Every_file_reads_back_byte_exact_on_both_filesystems()
    {
        var blob = new byte[5000];
        for (int i = 0; i < blob.Length; i++) blob[i] = (byte)(i * 31 + 7);
        var contents = new Dictionary<string, byte[]>
        {
            ["/readme.txt"] = Bytes("root file contents"),
            ["/blob.bin"] = blob,
            ["/sub/nested.txt"] = Bytes("nested hello world"),
        };

        var image = UdfBridgeBuilder.Build("BRIDGE", new[]
        {
            File("readme.txt", contents["/readme.txt"]),
            File("blob.bin", contents["/blob.bin"]),
            Dir("sub", File("nested.txt", contents["/sub/nested.txt"])),
        });

        using var ms = new MemoryStream(image);
        var iso = IsoReader.Read(ms);
        ms.Position = 0;
        var udf = UdfReader.Read(ms);

        foreach (var (path, want) in contents)
        {
            var isoEntry = iso.Files.Single(f => f.Path == path);
            var udfEntry = udf.Files.Single(f => f.Path == path);
            Assert.Equal(want, IsoExtract(image, isoEntry));
            Assert.Equal(want, UdfExtract(image, udf, udfEntry));
        }
    }

    // ---- the shared-data property (the whole point) -------------------------

    [Fact]
    public void Each_files_iso_extent_equals_its_udf_data_sector()
    {
        var blob = new byte[7000];
        for (int i = 0; i < blob.Length; i++) blob[i] = (byte)(i * 13 + 1);

        var image = UdfBridgeBuilder.Build("BRIDGE", new[]
        {
            File("readme.txt", Bytes("top level")),
            File("blob.bin", blob),
            Dir("sub", File("nested.txt", Bytes("deeper"))),
        });

        using var ms = new MemoryStream(image);
        var iso = IsoReader.Read(ms);
        ms.Position = 0;
        var udf = UdfReader.Read(ms);

        foreach (var isoEntry in iso.Files)
        {
            var udfEntry = udf.Files.Single(f => f.Path == isoEntry.Path);
            uint udfSector = UdfDataSector(image, udf.PartitionStart, udfEntry.IcbBlock);

            // The heart of a bridge disc: one copy of the data, addressed by both.
            Assert.Equal(isoEntry.Extent, udfSector);
        }
    }

    // ---- empty files, and a deeper tree ------------------------------------

    [Fact]
    public void An_empty_file_reads_back_empty_on_both_filesystems()
    {
        var image = UdfBridgeBuilder.Build("BRIDGE", new[] { File("EMPTY.DAT", Array.Empty<byte>()) });
        using var ms = new MemoryStream(image);
        var iso = IsoReader.Read(ms);
        ms.Position = 0;
        var udf = UdfReader.Read(ms);

        Assert.Empty(IsoExtract(image, iso.Files.Single()));
        Assert.Empty(UdfExtract(image, udf, udf.Files.Single()));
    }

    [Fact]
    public void A_nested_tree_round_trips_and_stays_shared()
    {
        var image = UdfBridgeBuilder.Build("DEEP", new[]
        {
            Dir("a", Dir("b", Dir("c", File("deep.txt", Bytes("all the way down"))))),
        });

        using var ms = new MemoryStream(image);
        var iso = IsoReader.Read(ms);
        ms.Position = 0;
        var udf = UdfReader.Read(ms);

        var isoEntry = iso.Files.Single(f => f.Path == "/a/b/c/deep.txt");
        var udfEntry = udf.Files.Single(f => f.Path == "/a/b/c/deep.txt");
        Assert.Equal(Bytes("all the way down"), IsoExtract(image, isoEntry));
        Assert.Equal(Bytes("all the way down"), UdfExtract(image, udf, udfEntry));
        Assert.Equal(isoEntry.Extent, UdfDataSector(image, udf.PartitionStart, udfEntry.IcbBlock));
    }

    // ---- determinism --------------------------------------------------------

    [Fact]
    public void The_same_tree_builds_byte_identical_images()
    {
        IsoBuilder.Node[] Tree() => new[]
        {
            File("A.TXT", Bytes("alpha")),
            Dir("D", File("B.TXT", Bytes("beta"))),
        };

        var first = UdfBridgeBuilder.Build("REPEATABLE", Tree());
        var second = UdfBridgeBuilder.Build("REPEATABLE", Tree());
        Assert.Equal(first, second);
    }

    // ---- the streamed path matches the in-memory build ----------------------

    [Fact]
    public void The_streamed_build_matches_the_in_memory_build()
    {
        IsoBuilder.Node[] Tree() => new[]
        {
            File("A.TXT", Bytes("hello world")),
            Dir("SUB", File("B.BIN", new byte[5000])),
        };

        var inMemory = UdfBridgeBuilder.Build("BRIDGE", Tree());
        using var ms = new MemoryStream();
        UdfBridgeBuilder.BuildToStream("BRIDGE", ms, Tree());
        Assert.Equal(inMemory, ms.ToArray());
    }

    // ---- warnings -----------------------------------------------------------

    [Fact]
    public void A_duplicate_name_in_a_directory_is_warned_about()
    {
        var result = UdfBridgeBuilder.BuildResultOf("BRIDGE", new[]
        {
            File("SAME.TXT", Bytes("one")),
            File("same.txt", Bytes("two")),
        });

        Assert.Contains(result.Warnings, w => w.Contains("Duplicate name"));
    }
}
