// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class RockRidgeTests
{
    private static byte[] Both32(uint v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), v);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), v);
        return b;
    }

    private static byte[] Nm(string name, byte flags = 0)
    {
        var nb = Encoding.UTF8.GetBytes(name);
        var b = new byte[5 + nb.Length];
        b[0] = (byte)'N'; b[1] = (byte)'M'; b[2] = (byte)b.Length; b[3] = 1; b[4] = flags;
        nb.CopyTo(b, 5);
        return b;
    }

    private static byte[] Px(uint mode, uint nlink, uint uid, uint gid)
    {
        var b = new byte[36];
        b[0] = (byte)'P'; b[1] = (byte)'X'; b[2] = 36; b[3] = 1;
        Both32(mode).CopyTo(b, 4); Both32(nlink).CopyTo(b, 12); Both32(uid).CopyTo(b, 20); Both32(gid).CopyTo(b, 28);
        return b;
    }

    private static byte[] TfShort(int y, int mo, int d, int h, int mi, int s)
    {
        var b = new byte[12];
        b[0] = (byte)'T'; b[1] = (byte)'F'; b[2] = 12; b[3] = 1; b[4] = 0x02; // MODIFY
        b[5] = (byte)(y - 1900); b[6] = (byte)mo; b[7] = (byte)d; b[8] = (byte)h; b[9] = (byte)mi; b[10] = (byte)s; b[11] = 0;
        return b;
    }

    private static byte[] Sl(string target)
    {
        var parts = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var comps = new List<byte>();
        if (target.StartsWith('/')) { comps.Add(0x08); comps.Add(0); } // ROOT
        foreach (var p in parts)
        {
            var pb = Encoding.UTF8.GetBytes(p);
            comps.Add(0x00); comps.Add((byte)pb.Length); comps.AddRange(pb);
        }
        var b = new byte[5 + comps.Count];
        b[0] = (byte)'S'; b[1] = (byte)'L'; b[2] = (byte)b.Length; b[3] = 1; b[4] = 0;
        comps.CopyTo(b, 5);
        return b;
    }

    private static byte[] Ce(uint block, uint offset, uint length)
    {
        var b = new byte[28];
        b[0] = (byte)'C'; b[1] = (byte)'E'; b[2] = 28; b[3] = 1;
        Both32(block).CopyTo(b, 4); Both32(offset).CopyTo(b, 12); Both32(length).CopyTo(b, 20);
        return b;
    }

    private static byte[] Cat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(0x41EDu, "drwxr-xr-x")]   // 040755
    [InlineData(0x81A4u, "-rw-r--r--")]   // 0100644
    [InlineData(0xA1FFu, "lrwxrwxrwx")]   // symlink 0120777
    [InlineData(0x89FFu, "-rwsrwxrwx")]   // setuid 04777
    [InlineData(0x13FFu, "prwxrwxrwt")]   // fifo + sticky 01777
    public void Formats_the_posix_mode_string(uint mode, string expected)
        => Assert.Equal(expected, RockRidge.FormatMode(mode));

    [Fact]
    public void Decodes_name_permissions_ownership_and_time()
    {
        var su = Cat(Nm("Réadme.txt"), Px(0x81A4, 1, 501, 20), TfShort(2021, 6, 15, 9, 30, 0));
        var info = RockRidge.Parse(su);
        Assert.True(info.Present);
        Assert.Equal("Réadme.txt", info.Name);
        Assert.Equal("-rw-r--r--", info.ModeString);
        Assert.Equal(501u, info.Uid);
        Assert.Equal(20u, info.Gid);
        Assert.Equal(1u, info.Links);
        Assert.NotNull(info.Modified);
        Assert.Equal(2021, info.Modified!.Value.Year);
        Assert.Equal(6, info.Modified.Value.Month);
        Assert.Equal(15, info.Modified.Value.Day);
    }

    [Fact]
    public void Reassembles_a_name_split_across_continue_entries()
    {
        var info = RockRidge.Parse(Cat(Nm("long-", 0x01), Nm("name")));
        Assert.Equal("long-name", info.Name);
    }

    [Fact]
    public void Recovers_a_symlink_target()
    {
        var info = RockRidge.Parse(Cat(Px(0xA1FF, 1, 0, 0), Sl("/usr/local/bin")));
        Assert.True(info.IsSymlink);
        Assert.Equal("/usr/local/bin", info.SymlinkTarget);
    }

    [Fact]
    public void Reads_deep_directory_relocation_markers()
    {
        var re = new byte[] { (byte)'R', (byte)'E', 4, 1 };
        var cl = new byte[12]; cl[0] = (byte)'C'; cl[1] = (byte)'L'; cl[2] = 12; cl[3] = 1;
        Both32(12345).CopyTo(cl, 4);
        var info = RockRidge.Parse(Cat(re, cl));
        Assert.True(info.Relocated);
        Assert.Equal(12345u, info.ChildLocation);
    }

    [Fact]
    public void Follows_a_ce_continuation_area_for_overflow_entries()
    {
        var cont = Px(0x81A4, 1, 7, 8);
        var su = Cat(Nm("withCE"), Ce(100, 40, (uint)cont.Length));
        var info = RockRidge.Parse(su, (absOffset, len) =>
        {
            Assert.Equal(100L * 2048 + 40, absOffset);
            return cont.AsSpan(0, Math.Min(len, cont.Length)).ToArray();
        });
        Assert.Equal("withCE", info.Name);
        Assert.Equal(7u, info.Uid);
        Assert.Equal(8u, info.Gid);
    }

    [Fact]
    public void Decodes_a_long_form_timestamp()
    {
        var stamp = Encoding.ASCII.GetBytes("2019030412000000");
        var tf = new byte[5 + 17];
        tf[0] = (byte)'T'; tf[1] = (byte)'F'; tf[2] = (byte)tf.Length; tf[3] = 1; tf[4] = 0x82; // LONG_FORM | MODIFY
        stamp.CopyTo(tf, 5);
        var info = RockRidge.Parse(tf);
        Assert.NotNull(info.Modified);
        Assert.Equal(2019, info.Modified!.Value.Year);
        Assert.Equal(3, info.Modified.Value.Month);
        Assert.Equal(4, info.Modified.Value.Day);
        Assert.Equal(12, info.Modified.Value.Hour);
    }

    [Fact]
    public void Garbage_is_absent_not_thrown()
    {
        var info = RockRidge.Parse(new byte[] { 1, 2, 3, 0, 0, 9 });
        Assert.False(info.Present);
    }

    // End-to-end: build a real Rock Ridge ISO with IsoBuilder and read the POSIX view back.
    [Fact]
    public void Round_trips_a_real_rock_ridge_iso_through_isobuilder()
    {
        var tree = new List<IsoBuilder.Node>
        {
            IsoBuilder.Node.File("readme.txt", Encoding.ASCII.GetBytes("hello")),
            IsoBuilder.Node.Dir("docs", new[] { IsoBuilder.Node.File("guide.txt", Encoding.ASCII.GetBytes("nested")) }),
        };
        var res = IsoBuilder.BuildTree("RRTEST", tree, joliet: false, boot: null, rockRidge: true);

        using var ms = new MemoryStream(res.Image);
        var listing = IsoReader.Read(ms, IsoReader.NamePreference.Iso9660);

        Assert.True(listing.RockRidge);
        var readme = Assert.Single(listing.Entries, e => e.Name == "readme.txt");
        Assert.Equal("-rw-r--r--", readme.RockRidge?.ModeString);
        var docs = Assert.Single(listing.Entries, e => e.Name == "docs" && e.IsDirectory);
        Assert.Equal("drwxr-xr-x", docs.RockRidge?.ModeString);
        Assert.Contains(listing.Entries, e => e.Path.EndsWith("/docs/guide.txt"));
    }
}
