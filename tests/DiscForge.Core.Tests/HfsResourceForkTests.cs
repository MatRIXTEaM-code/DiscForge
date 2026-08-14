// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Hfs;
using Xunit;

namespace DiscForge.Core.Tests;

public class HfsResourceForkTests
{
    // Builds a byte-exact classic Mac resource fork with two resources:
    //   'vers' id 1  (unnamed)  -> version stamp "1.0" / "1.0, DiscForge test"
    //   'STR ' id 128 (named "Greeting") -> Pascal string "Hello"
    private static byte[] BuildFork()
    {
        static byte[] Pascal(string s)
        {
            var t = Encoding.ASCII.GetBytes(s);
            var b = new byte[t.Length + 1];
            b[0] = (byte)t.Length;
            t.CopyTo(b, 1);
            return b;
        }

        // --- resource data blobs -------------------------------------------------
        var versData = new List<byte> { 0x01, 0x00, 0x80, 0x00, 0x00, 0x00 };  // major/minor BCD, release, prerel, region
        versData.AddRange(Pascal("1.0"));
        versData.AddRange(Pascal("1.0, DiscForge test"));
        var strData = Pascal("Hello");

        const int dataOff = 16;
        var data = new List<byte>();
        int versDataOff = data.Count;                 // offset within the data area
        data.AddRange(BE32((uint)versData.Count));
        data.AddRange(versData);
        int strDataOff = data.Count;
        data.AddRange(BE32((uint)strData.Length));
        data.AddRange(strData);
        int dataLen = data.Count;

        int mapOff = dataOff + dataLen;

        // --- resource map --------------------------------------------------------
        // 28-byte map preamble, then type list, reference lists, name list.
        const int typeListRel = 28;                   // type list starts 28 bytes into the map
        // type list: 2-byte (count-1) + 8 bytes per type (2 types) = 18 bytes
        int refListsRel = typeListRel + 2 + 2 * 8;    // reference lists follow the type entries
        int versRefRel = refListsRel;                 // vers: 1 resource
        int strRefRel = versRefRel + 12;              // STR : 1 resource
        int nameListRel = strRefRel + 12;

        var map = new List<byte>();
        map.AddRange(new byte[16]);                   // reserved header copy
        map.AddRange(new byte[4]);                    // reserved next-map handle
        map.AddRange(new byte[2]);                    // reserved file ref
        map.AddRange(new byte[2]);                    // fork attributes
        map.AddRange(BE16((ushort)typeListRel));      // offset to type list
        map.AddRange(BE16((ushort)nameListRel));      // offset to name list
        // type list
        map.AddRange(BE16(1));                         // number of types - 1  (=> 2 types)
        map.AddRange(Encoding.ASCII.GetBytes("vers"));
        map.AddRange(BE16(0));                          // 1 resource of this type - 1
        map.AddRange(BE16((ushort)(versRefRel - typeListRel)));  // ref-list offset from type-list start
        map.AddRange(Encoding.ASCII.GetBytes("STR "));
        map.AddRange(BE16(0));
        map.AddRange(BE16((ushort)(strRefRel - typeListRel)));
        // reference list: vers id 1, unnamed
        map.AddRange(BE16(1));                          // id
        map.AddRange(BE16(0xFFFF));                     // no name
        map.Add(0);                                     // attributes
        map.AddRange(new byte[] { (byte)(versDataOff >> 16), (byte)(versDataOff >> 8), (byte)versDataOff });
        map.AddRange(new byte[4]);                      // reserved handle
        // reference list: STR  id 128, named
        map.AddRange(BE16(128));                        // id
        map.AddRange(BE16(0));                          // name at name-list offset 0
        map.Add(0);
        map.AddRange(new byte[] { (byte)(strDataOff >> 16), (byte)(strDataOff >> 8), (byte)strDataOff });
        map.AddRange(new byte[4]);
        // name list
        map.AddRange(Pascal("Greeting"));
        int mapLen = map.Count;

        var fork = new List<byte>();
        fork.AddRange(BE32(dataOff));
        fork.AddRange(BE32((uint)mapOff));
        fork.AddRange(BE32((uint)dataLen));
        fork.AddRange(BE32((uint)mapLen));
        fork.AddRange(data);
        fork.AddRange(map);
        return fork.ToArray();
    }

    private static byte[] BE32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] BE16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }

    [Fact]
    public void Header_is_recognised()
    {
        Assert.True(HfsResourceFork.Looks(BuildFork()));
        Assert.False(HfsResourceFork.Looks(new byte[8]));
        Assert.False(HfsResourceFork.Looks(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void Enumerates_every_resource_with_type_id_and_name()
    {
        var info = HfsResourceFork.Parse(BuildFork());
        Assert.Equal(2, info.Count);
        Assert.Equal(new[] { "vers", "STR " }, info.Types);

        var vers = Assert.Single(info.Resources, r => r.Type == "vers");
        Assert.Equal((short)1, vers.Id);
        Assert.Null(vers.Name);

        var str = Assert.Single(info.Resources, r => r.Type == "STR ");
        Assert.Equal((short)128, str.Id);
        Assert.Equal("Greeting", str.Name);
    }

    [Fact]
    public void Slices_a_resources_bytes_by_offset_and_length()
    {
        var fork = BuildFork();
        var info = HfsResourceFork.Parse(fork);
        var str = Assert.Single(info.Resources, r => r.Type == "STR ");
        var bytes = HfsResourceFork.GetData(fork, str).ToArray();
        // Pascal "Hello": length byte 5 then the text.
        Assert.Equal(6, bytes.Length);
        Assert.Equal(5, bytes[0]);
        Assert.Equal("Hello", Encoding.ASCII.GetString(bytes, 1, 5));
    }

    [Fact]
    public void Decodes_the_vers_version_stamp()
    {
        var fork = BuildFork();
        var info = HfsResourceFork.Parse(fork);
        var vers = Assert.Single(info.Resources, r => r.Type == "vers");
        var v = HfsResourceFork.DecodeVersion(HfsResourceFork.GetData(fork, vers));
        Assert.NotNull(v);
        Assert.Equal(1, v!.Major);
        Assert.Equal(0, v.Minor);
        Assert.Equal("release", v.Stage);
        Assert.Equal("1.0", v.ShortText);
        Assert.Equal("1.0, DiscForge test", v.LongText);
    }

    [Fact]
    public void An_empty_type_list_yields_no_resources()
    {
        // A fork whose map advertises 0xFFFF types (count - 1 == -1) is a valid empty fork.
        var fork = BuildFork();
        // Overwrite the type-count word with 0xFFFF: it lives at mapOff + 28.
        int mapOff = (int)BinaryPrimitives.ReadUInt32BigEndian(fork.AsSpan(4));
        fork[mapOff + 28] = 0xFF;
        fork[mapOff + 29] = 0xFF;
        var info = HfsResourceFork.Parse(fork);
        Assert.Empty(info.Resources);
    }

    [Fact]
    public void A_truncated_fork_throws_rather_than_reading_past_the_end()
    {
        var fork = BuildFork();
        var chopped = fork[..40];
        Assert.Throws<HfsFormatException>(() => HfsResourceFork.Parse(chopped));
    }
}
