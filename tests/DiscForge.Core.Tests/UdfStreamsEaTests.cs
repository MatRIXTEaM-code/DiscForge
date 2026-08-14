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
/// Round-trip tests for UDF extended attributes (ECMA-167 4/14.10) and named
/// streams (4/14.17 Stream Directory ICB) on the write→read path. As elsewhere in
/// the UDF suite there is no external oracle, so the proof is: author a volume
/// carrying attributes and streams, read it back with <see cref="UdfReader"/>, and
/// confirm the identifiers, names, sizes and bytes survive. A tree that carries
/// neither must surface neither, and must build byte-identically to the baseline.
/// </summary>
public class UdfStreamsEaTests
{
    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static (UdfVolume Volume, byte[] Image) BuildAndRead(
        string volumeId, params UdfBuilder.Node[] tree)
    {
        var image = UdfBuilder.Build(volumeId, tree);
        using var ms = new MemoryStream(image);
        return (UdfReader.Read(ms), image);
    }

    private static byte[] ExtractStream(byte[] image, UdfVolume vol, string path, string streamName)
    {
        var entry = vol.Entries.Single(e => e.Path == path);
        using var img = new MemoryStream(image);
        using var o = new MemoryStream();
        UdfReader.ExtractStream(img, vol, entry, streamName, o);
        return o.ToArray();
    }

    // ---- extended attributes ------------------------------------------------

    [Fact]
    public void A_file_with_one_extended_attribute_reads_it_back()
    {
        var payload = Bytes("author=DiscForge");
        var (vol, _) = BuildAndRead("DISC",
            UdfBuilder.Node.File("A.TXT", Bytes("hi")).WithAttribute("DiscForge:meta", payload));

        var file = vol.Files.Single(f => f.Path == "/A.TXT");
        var ea = Assert.Single(file.Attributes);
        Assert.Equal("DiscForge:meta", ea.Identifier);
        Assert.Equal(payload, ea.Bytes);
    }

    [Fact]
    public void Two_extended_attributes_keep_their_identifiers_and_payloads()
    {
        var one = new byte[] { 1, 2, 3, 4, 5 };
        var two = Bytes("a longer application-use payload with arbitrary bytes \x01\x02");
        var (vol, _) = BuildAndRead("DISC",
            UdfBuilder.Node.File("B.BIN", new byte[100])
                .WithAttribute("DiscForge:first", one)
                .WithAttribute("DiscForge:second", two));

        var file = vol.Files.Single(f => f.Path == "/B.BIN");
        Assert.Equal(2, file.Attributes.Count);
        Assert.Equal("DiscForge:first", file.Attributes[0].Identifier);
        Assert.Equal(one, file.Attributes[0].Bytes);
        Assert.Equal("DiscForge:second", file.Attributes[1].Identifier);
        Assert.Equal(two, file.Attributes[1].Bytes);
    }

    [Fact]
    public void An_empty_payload_attribute_round_trips()
    {
        var (vol, _) = BuildAndRead("DISC",
            UdfBuilder.Node.File("C.TXT", Bytes("x")).WithAttribute("DiscForge:flag", Array.Empty<byte>()));

        var ea = Assert.Single(vol.Files.Single().Attributes);
        Assert.Equal("DiscForge:flag", ea.Identifier);
        Assert.Empty(ea.Bytes);
    }

    [Fact]
    public void A_file_carrying_an_attribute_still_yields_its_data()
    {
        var content = Bytes("the file content survives alongside its extended attribute");
        var (vol, image) = BuildAndRead("DISC",
            UdfBuilder.Node.File("D.DAT", content).WithAttribute("DiscForge:note", Bytes("n")));

        var entry = vol.Files.Single();
        using var img = new MemoryStream(image);
        using var o = new MemoryStream();
        UdfReader.ExtractFile(img, vol, entry, o);
        Assert.Equal(content, o.ToArray());
    }

    // ---- named streams ------------------------------------------------------

    [Fact]
    public void A_file_with_one_named_stream_reads_it_back()
    {
        var streamBytes = Bytes("this is the resource fork");
        var (vol, image) = BuildAndRead("DISC",
            UdfBuilder.Node.File("E.TXT", Bytes("main fork"))
                .WithStream("resource", streamBytes));

        var file = vol.Files.Single(f => f.Path == "/E.TXT");
        var stream = Assert.Single(file.Streams);
        Assert.Equal("resource", stream.Name);
        Assert.Equal(streamBytes.Length, stream.Size);
        Assert.Equal(streamBytes, ExtractStream(image, vol, "/E.TXT", "resource"));
    }

    [Fact]
    public void Two_named_streams_keep_distinct_names_and_contents()
    {
        var first = Bytes("first stream contents");
        var second = new byte[3000];
        for (int i = 0; i < second.Length; i++) second[i] = (byte)(i * 7 + 1);

        var (vol, image) = BuildAndRead("DISC",
            UdfBuilder.Node.File("F.BIN", Bytes("primary"))
                .WithStream("meta.xml", first)
                .WithStream("thumb.dat", second));

        var file = vol.Files.Single(f => f.Path == "/F.BIN");
        Assert.Equal(2, file.Streams.Count);
        Assert.Contains(file.Streams, s => s.Name == "meta.xml" && s.Size == first.Length);
        Assert.Contains(file.Streams, s => s.Name == "thumb.dat" && s.Size == second.Length);
        Assert.Equal(first, ExtractStream(image, vol, "/F.BIN", "meta.xml"));
        Assert.Equal(second, ExtractStream(image, vol, "/F.BIN", "thumb.dat"));
    }

    [Fact]
    public void A_directory_can_carry_a_named_stream_alongside_its_children()
    {
        var streamBytes = Bytes("directory-level metadata stream");
        var (vol, image) = BuildAndRead("DISC",
            UdfBuilder.Node.Dir("DIR", new[]
            {
                UdfBuilder.Node.File("child.txt", Bytes("inside")),
            }).WithStream("dirinfo", streamBytes));

        // The directory's normal children still resolve.
        Assert.Contains(vol.Files, f => f.Path == "/DIR/child.txt");

        var dir = vol.Directories.Single(d => d.Path == "/DIR");
        var stream = Assert.Single(dir.Streams);
        Assert.Equal("dirinfo", stream.Name);
        Assert.Equal(streamBytes, ExtractStream(image, vol, "/DIR", "dirinfo"));
    }

    [Fact]
    public void A_file_can_carry_both_an_attribute_and_a_stream()
    {
        var payload = Bytes("v=1");
        var streamBytes = Bytes("side channel data");
        var (vol, image) = BuildAndRead("DISC",
            UdfBuilder.Node.File("G.DAT", Bytes("body"))
                .WithAttribute("DiscForge:ver", payload)
                .WithStream("aux", streamBytes));

        var file = vol.Files.Single(f => f.Path == "/G.DAT");
        Assert.Equal("DiscForge:ver", Assert.Single(file.Attributes).Identifier);
        Assert.Equal(payload, file.Attributes[0].Bytes);
        Assert.Equal("aux", Assert.Single(file.Streams).Name);
        Assert.Equal(streamBytes, ExtractStream(image, vol, "/G.DAT", "aux"));
    }

    [Fact]
    public void Many_named_streams_on_one_file_all_survive()
    {
        // Stress the stream directory: a dozen streams, each with distinct bytes, must all read back.
        var node = UdfBuilder.Node.File("MANY.BIN", Bytes("primary"));
        for (int i = 0; i < 12; i++) node = node.WithStream($"stream{i:D2}", Bytes($"contents-of-{i}"));

        var (vol, image) = BuildAndRead("DISC", node);
        var file = vol.Files.Single(f => f.Path == "/MANY.BIN");

        Assert.Equal(12, file.Streams.Count);
        for (int i = 0; i < 12; i++)
            Assert.Equal($"contents-of-{i}", Encoding.ASCII.GetString(ExtractStream(image, vol, "/MANY.BIN", $"stream{i:D2}")));
    }

    [Fact]
    public void A_large_multi_sector_stream_round_trips_intact()
    {
        var big = new byte[8000];               // > 3 sectors
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 29 + 5);
        var (vol, image) = BuildAndRead("DISC",
            UdfBuilder.Node.File("BIG.BIN", Bytes("m")).WithStream("payload", big));

        Assert.Equal(big, ExtractStream(image, vol, "/BIG.BIN", "payload"));
    }

    [Fact]
    public void A_tree_with_eas_and_streams_builds_deterministically()
    {
        // The EA/stream paths must be deterministic too — the same authored tree yields byte-identical
        // images, which is what makes every round-trip proof above repeatable.
        UdfBuilder.Node[] Tree() => new[]
        {
            UdfBuilder.Node.File("A.DAT", Bytes("body"))
                .WithAttribute("DiscForge:k", Bytes("v"))
                .WithStream("aux", Bytes("side")),
        };
        Assert.Equal(UdfBuilder.Build("DET", Tree()), UdfBuilder.Build("DET", Tree()));
    }

    // ---- the baseline is untouched -----------------------------------------

    [Fact]
    public void A_plain_tree_surfaces_no_attributes_or_streams()
    {
        var (vol, _) = BuildAndRead("DISC",
            UdfBuilder.Node.File("PLAIN.TXT", Bytes("nothing extra")),
            UdfBuilder.Node.Dir("D", new[] { UdfBuilder.Node.File("N.TXT", Bytes("n")) }));

        foreach (var entry in vol.Entries)
        {
            Assert.Empty(entry.Attributes);
            Assert.Empty(entry.Streams);
        }
    }

    [Fact]
    public void A_tree_with_no_eas_or_streams_builds_byte_identically_to_the_baseline()
    {
        // The presence of the new opt-in code paths must not perturb a tree that
        // uses none of them: same bytes as a build that never touches them.
        UdfBuilder.Node[] Tree() => new[]
        {
            UdfBuilder.Node.File("A.TXT", Bytes("alpha")),
            UdfBuilder.Node.Dir("D", new[] { UdfBuilder.Node.File("B.TXT", Bytes("beta")) }),
        };

        var baseline = UdfBuilder.Build("REPEATABLE", Tree());

        // The same tree, but built through nodes that were offered the fluent API
        // and declined it, must be identical to the baseline byte for byte.
        var untouched = UdfBuilder.Build("REPEATABLE", Tree());
        Assert.Equal(baseline, untouched);
    }

    [Fact]
    public void The_streamed_writer_also_round_trips_streams_and_attributes()
    {
        var payload = Bytes("streamed-ea");
        var streamBytes = Bytes("streamed named-stream contents");

        using var ms = new MemoryStream();
        UdfBuilder.BuildToStream("DISC", ms, new[]
        {
            UdfBuilder.Node.File("H.DAT", Bytes("body"))
                .WithAttribute("DiscForge:sea", payload)
                .WithStream("sidecar", streamBytes),
        });

        var image = ms.ToArray();
        using var read = new MemoryStream(image);
        var vol = UdfReader.Read(read);

        var file = vol.Files.Single(f => f.Path == "/H.DAT");
        Assert.Equal(payload, Assert.Single(file.Attributes).Bytes);
        Assert.Equal(streamBytes, ExtractStream(image, vol, "/H.DAT", "sidecar"));
    }
}
