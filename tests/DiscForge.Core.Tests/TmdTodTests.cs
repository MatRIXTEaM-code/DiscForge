// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Media;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the TMD model reader / DXF export. A TMD is built to the public
/// layout (header, one object, three vertices) and read back; the checks pin the
/// object/vertex counts, the signed 16-bit vertex coordinates, and that the DXF
/// export carries the points.
/// </summary>
public class TmdTests
{
    private static void U32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
    private static void S16(byte[] b, int at, short v) => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(at), v);

    private static byte[] BuildTmd((short X, short Y, short Z)[] verts)
    {
        // header(12) + 1 object entry(28) + vertices(8 each). FIXP=0, so *_top is a
        // byte offset from the header end (offset 12) — the vertex data physically
        // sits at 40, so vert_top = 40 - 12 = 28. (A real model proved this base;
        // the old "= 0" only worked because the parser had the same off-by-28 bug.)
        int dataBase = 12 + 28;
        uint vTop = (uint)(dataBase - 12);
        var tmd = new byte[dataBase + verts.Length * 8];
        U32(tmd, 0, 0x41);          // id
        U32(tmd, 4, 0);             // flags (FIXP=0)
        U32(tmd, 8, 1);             // nobj

        int e = 12;
        U32(tmd, e + 0, vTop);                  // vert_top
        U32(tmd, e + 4, (uint)verts.Length);    // n_vert
        U32(tmd, e + 8, 0);                     // normal_top
        U32(tmd, e + 12, 0);                    // n_normal
        U32(tmd, e + 16, 0);                    // primitive_top
        U32(tmd, e + 20, 0);                    // n_primitive
        U32(tmd, e + 24, 0);                    // scale

        for (int i = 0; i < verts.Length; i++)
        {
            int p = dataBase + i * 8;
            S16(tmd, p, verts[i].X);
            S16(tmd, p + 2, verts[i].Y);
            S16(tmd, p + 4, verts[i].Z);
        }
        return tmd;
    }

    [Fact]
    public void An_object_and_its_vertices_read_back()
    {
        var model = Tmd.Parse(BuildTmd(new[] { ((short)1, (short)2, (short)3),
                                               ((short)-4, (short)5, (short)-6),
                                               ((short)100, (short)-200, (short)300) }));

        var obj = Assert.Single(model.Objects);
        Assert.Equal(3, obj.Vertices.Count);
        Assert.Equal(new Tmd.Vertex(1, 2, 3), obj.Vertices[0]);
        Assert.Equal(new Tmd.Vertex(-4, 5, -6), obj.Vertices[1]);
        Assert.Equal(new Tmd.Vertex(100, -200, 300), obj.Vertices[2]);
        Assert.Equal(3, model.VertexTotal);
    }

    // A TMD with one object: 3 vertices and one flat-shaded untextured triangle
    // primitive referencing vertices 0,1,2.
    private static byte[] BuildTmdWithFace()
    {
        int dataBase = 12 + 28;
        int vertsLen = 3 * 8;
        int primOff = vertsLen;                 // primitives right after the vertices
        var tmd = new byte[dataBase + vertsLen + 16];

        U32(tmd, 0, 0x41); U32(tmd, 4, 0); U32(tmd, 8, 1);   // header: id, flags, nobj

        // *_top are offsets from the header end (12): vertices are physically at 40
        // (top 28), primitives at 40 + vertsLen (top 28 + vertsLen).
        int e = 12;
        U32(tmd, e + 0, (uint)(dataBase - 12));   U32(tmd, e + 4, 3);            // vert_top, n_vert
        U32(tmd, e + 8, 0);   U32(tmd, e + 12, 0);                               // normal_top, n_normal
        U32(tmd, e + 16, (uint)(dataBase - 12 + primOff)); U32(tmd, e + 20, 1);  // primitive_top, n_primitive
        U32(tmd, e + 24, 0);                                  // scale

        var verts = new[] { ((short)1, (short)2, (short)3), ((short)4, (short)5, (short)6), ((short)7, (short)8, (short)9) };
        for (int i = 0; i < 3; i++)
        {
            int p = dataBase + i * 8;
            S16(tmd, p, verts[i].Item1); S16(tmd, p + 2, verts[i].Item2); S16(tmd, p + 4, verts[i].Item3);
        }

        int prim = dataBase + primOff;
        tmd[prim + 0] = 0;      // olen
        tmd[prim + 1] = 3;      // ilen (3 words)
        tmd[prim + 2] = 0;      // flag
        tmd[prim + 3] = 0x20;   // mode: polygon, flat, triangle, untextured
        // w0 colour (prim+4), w1 = normal0|vertex0 (prim+8), w2 = vertex1|vertex2 (prim+12)
        S16(tmd, prim + 8, 0);  S16(tmd, prim + 10, 0);   // normal0=0, vertex0=0
        S16(tmd, prim + 12, 1); S16(tmd, prim + 14, 2);   // vertex1=1, vertex2=2
        return tmd;
    }

    [Fact]
    public void A_flat_triangle_primitive_decodes_to_a_face()
    {
        var obj = Assert.Single(Tmd.Parse(BuildTmdWithFace()).Objects);
        var face = Assert.Single(obj.Faces);
        Assert.Equal(new[] { 0, 1, 2 }, face);
    }

    [Fact]
    public void The_dxf_export_emits_3dface_when_faces_exist()
    {
        var dxf = Tmd.ToDxf(Tmd.Parse(BuildTmdWithFace()));
        Assert.Contains("3DFACE", dxf);
        Assert.DoesNotContain("POINT", dxf);   // faces present → no point-cloud fallback
    }

    [Fact]
    public void A_non_tmd_is_refused()
    {
        Assert.False(Tmd.IsTmd(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
        Assert.Throws<TmdFormatException>(() => Tmd.Parse(new byte[12]));
    }

    [Fact]
    public void The_dxf_export_lists_the_vertices_as_points()
    {
        var dxf = Tmd.ToDxf(Tmd.Parse(BuildTmd(new[] { ((short)7, (short)8, (short)9) })));
        Assert.Contains("POINT", dxf);
        Assert.Contains("OBJ0", dxf);
        Assert.Contains("\n7\n", dxf);      // x
        Assert.Contains("\n8\n", dxf);      // y
        Assert.Contains("\n9\n", dxf);      // z
        Assert.EndsWith("EOF\n", dxf);
    }
}

/// <summary>
/// Tests for the TOD animation reader. TOD is validated by round trip — the writer
/// and reader agree on the container — pending a real Sony-produced sample, exactly
/// as the NRG reader was before one existed.
/// </summary>
public class TodTests
{
    [Fact]
    public void A_tod_round_trips_its_frames_and_packets()
    {
        var tod = new Tod.TodFile
        {
            Version = 1,
            Resolution = 1,
            Frames = new[]
            {
                new Tod.TodFrame
                {
                    FrameNumber = 0,
                    Packets = new[]
                    {
                        new Tod.TodPacket { ObjectId = 1, Type = 1, Flag = 2, Data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } },
                        new Tod.TodPacket { ObjectId = 2, Type = 0, Flag = 0, Data = Array.Empty<byte>() },
                    },
                },
                new Tod.TodFrame
                {
                    FrameNumber = 5,
                    Packets = new[]
                    {
                        new Tod.TodPacket { ObjectId = 3, Type = 4, Flag = 1, Data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF } },
                    },
                },
            },
        };

        var read = Tod.Parse(Tod.Write(tod));

        Assert.Equal(2, read.Frames.Count);
        Assert.Equal(1, read.Version);
        Assert.Equal(2, read.Frames[0].Packets.Count);
        Assert.Equal(1, read.Frames[0].Packets[0].ObjectId);
        Assert.Equal(1, read.Frames[0].Packets[0].Type);
        Assert.Equal(2, read.Frames[0].Packets[0].Flag);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, read.Frames[0].Packets[0].Data);
        Assert.Empty(read.Frames[0].Packets[1].Data);
        Assert.Equal(5, read.Frames[1].FrameNumber);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, read.Frames[1].Packets[0].Data);
    }

    [Fact]
    public void A_short_file_is_refused()
    {
        Assert.Throws<TodFormatException>(() => Tod.Parse(new byte[4]));
    }
}
