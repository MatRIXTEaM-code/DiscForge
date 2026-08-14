// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DiscForge.Core.Media;

public sealed class TmdFormatException(string message) : Exception(message);

/// <summary>
/// Reads the PlayStation TMD 3-D model format — the geometry "TMD RIP" pulls out,
/// and the input to "TMD2DXF". TMD is a plain, unencrypted model container: a
/// header, an object table, and per-object vertices, normals and drawing
/// primitives. This reads the reliable geometry (objects, vertices, normals) and
/// enumerates the primitive packets, and exports the vertices to DXF.
///
/// Clean-room, from the public Sony TMD description:
///   Header (12 bytes): u32 id (0x00000041), u32 flags (bit0 = FIXP), u32 nobj.
///   Object entry (28 bytes): vert_top, n_vert, normal_top, n_normal,
///                            primitive_top, n_primitive, i32 scale.
///   With FIXP = 0 the *_top values are byte offsets from the top of the object
///   table (the address right after the 12-byte header); with FIXP = 1 they are
///   absolute. (The "end of the object table" reading is a common error — it
///   overruns by nobj*28; a real model confirmed the base is the header end.)
///   Vertex / normal = SVECTOR: i16 x, i16 y, i16 z, i16 pad (8 bytes).
///
/// Scope: vertex and normal geometry is decoded fully, and the common polygon
/// primitives — flat or gouraud, triangle or quad, textured or not — are decoded
/// into faces, so DXF export emits 3DFACE polygons (objects with only unhandled
/// modes, such as lines or sprites, fall back to a vertex point cloud). The face
/// layout follows the documented TMD packet structure and is validated by round
/// trip; a real model would confirm the rarer textured-mode offsets. Nothing here
/// is protection-related.
/// </summary>
public static class Tmd
{
    public readonly record struct Vertex(short X, short Y, short Z);

    public sealed record TmdObject
    {
        public required int Scale { get; init; }
        public required IReadOnlyList<Vertex> Vertices { get; init; }
        public required IReadOnlyList<Vertex> Normals { get; init; }
        public required int PrimitiveCount { get; init; }
        /// <summary>Decoded polygon faces — each an array of 3 or 4 vertex indices
        /// into <see cref="Vertices"/>. Only the common polygon modes (flat/gouraud,
        /// triangle/quad, with or without texture) are decoded; lines, sprites and
        /// unrecognised modes are skipped.</summary>
        public required IReadOnlyList<int[]> Faces { get; init; }
    }

    public sealed record TmdModel
    {
        public required uint Flags { get; init; }
        public required IReadOnlyList<TmdObject> Objects { get; init; }
        public int VertexTotal => Objects.Sum(o => o.Vertices.Count);
    }

    private const uint TmdId = 0x00000041;
    private const int HeaderSize = 12;
    private const int ObjectEntrySize = 28;

    public static bool IsTmd(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(data) == TmdId;

    public static TmdModel Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsTmd(data))
            throw new TmdFormatException("Missing the 0x00000041 TMD id — not a TMD model.");

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        uint nobj = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        bool fixp = (flags & 0x1) != 0;

        if (nobj > 100_000)
            throw new TmdFormatException($"Implausible object count {nobj} — file is likely not a TMD.");

        // With FIXP = 0 the *_top pointers are byte offsets from the address right
        // after the 12-byte header — i.e. the top of the object table — NOT the end
        // of the object table. (Confirmed against a real model: base 12 lands the
        // last normal exactly on EOF; base 12+nobj*28 overruns by nobj*28.)
        int objTable = HeaderSize;
        int dataBase = HeaderSize;                                 // FIXP=0 offset base
        int PtrBase(uint top) => fixp ? (int)top : dataBase + (int)top;

        var objects = new List<TmdObject>((int)nobj);
        for (int i = 0; i < nobj; i++)
        {
            int e = objTable + i * ObjectEntrySize;
            if (e + ObjectEntrySize > data.Length)
                throw new TmdFormatException("Object table extends past the end of the file.");

            uint vertTop = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(e));
            uint nVert = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(e + 4));
            uint normTop = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(e + 8));
            uint nNorm = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(e + 12));
            uint primTop = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(e + 16));
            uint nPrim = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(e + 20));
            int scale = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(e + 24));

            objects.Add(new TmdObject
            {
                Scale = scale,
                Vertices = ReadVectors(data, PtrBase(vertTop), (int)nVert),
                Normals = ReadVectors(data, PtrBase(normTop), (int)nNorm),
                PrimitiveCount = (int)nPrim,
                Faces = ReadFaces(data, PtrBase(primTop), (int)nPrim),
            });
        }

        return new TmdModel { Flags = flags, Objects = objects };
    }

    // Decode a directory of primitive packets into polygon faces. Each packet is a
    // 4-byte header (olen, ilen, flag, mode) then ilen 32-bit words; ilen advances
    // the cursor, so unrecognised packets are skipped cleanly.
    private static IReadOnlyList<int[]> ReadFaces(byte[] data, int at, int count)
    {
        var faces = new List<int[]>();
        int cursor = at;
        for (int i = 0; i < count; i++)
        {
            if (cursor + 4 > data.Length) break;
            int ilen = data[cursor + 1];
            int mode = data[cursor + 3];
            int dataStart = cursor + 4;
            cursor += 4 + ilen * 4;
            if (cursor > data.Length + 0) { /* packet runs past EOF */ }

            var face = DecodeFace(data, dataStart, ilen, mode);
            if (face is not null) faces.Add(face);
        }
        return faces;
    }

    // Locate the vertex indices in a polygon packet from its mode bits. Layout:
    // [texture words: one per vertex, when TME] [colour words: nvert if gouraud
    // else 1, and none for textured-flat] [vertex/normal section]. In the vertex
    // section a gouraud packet stores (normal,vertex) per vertex (vertex in the high
    // 16 bits); a flat packet stores (normal0,vertex0) then the remaining vertices
    // packed two per word.
    private static int[]? DecodeFace(byte[] data, int dataStart, int ilen, int mode)
    {
        int code = (mode >> 5) & 0x07;
        if (code != 1) return null;             // 1 = polygon; 2 = line, 3 = sprite

        int nvert = (mode & 0x08) != 0 ? 4 : 3;
        bool gouraud = (mode & 0x10) != 0;
        bool textured = (mode & 0x04) != 0;

        int w = 0;
        if (textured) w += nvert;               // u/v + clut/tsb words
        w += !textured ? (gouraud ? nvert : 1)  // colour words
                       : (gouraud ? nvert : 0);

        ushort Hi(int word) => (ushort)((ReadWord(data, dataStart, word, ilen) >> 16) & 0xFFFF);
        ushort Lo(int word) => (ushort)(ReadWord(data, dataStart, word, ilen) & 0xFFFF);

        var v = new int[nvert];
        if (gouraud)
        {
            for (int k = 0; k < nvert; k++) v[k] = Hi(w + k);
        }
        else
        {
            v[0] = Hi(w);
            for (int k = 1; k < nvert; k++)
            {
                int word = w + 1 + (k - 1) / 2;
                v[k] = ((k - 1) & 1) == 0 ? Lo(word) : Hi(word);
            }
        }

        // Bounds-check against the packet length; a face that reads past ilen is a
        // mode this decoder doesn't handle, so drop it rather than guess.
        int neededWords = gouraud ? w + nvert : w + 1 + (nvert - 1 + 1) / 2;
        if (neededWords > ilen) return null;
        return v;
    }

    private static uint ReadWord(byte[] data, int dataStart, int word, int ilen)
    {
        int at = dataStart + word * 4;
        if (word < 0 || word >= ilen || at + 4 > data.Length) return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
    }

    private static IReadOnlyList<Vertex> ReadVectors(byte[] data, int at, int count)
    {
        var list = new List<Vertex>(Math.Max(0, count));
        for (int i = 0; i < count; i++)
        {
            int p = at + i * 8;
            if (p + 6 > data.Length)
                throw new TmdFormatException("Vertex/normal data extends past the end of the file.");
            list.Add(new Vertex(
                BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p)),
                BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p + 2)),
                BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p + 4))));
        }
        return list;
    }

    /// <summary>Export the model to DXF. Objects with decoded polygon faces are
    /// written as 3DFACE entities (triangles and quads); an object with no
    /// recognised faces falls back to a POINT cloud of its vertices. Each object is
    /// on its own layer (OBJ0, OBJ1, …).</summary>
    public static string ToDxf(TmdModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var sb = new StringBuilder();
        sb.Append("0\nSECTION\n2\nENTITIES\n");
        for (int o = 0; o < model.Objects.Count; o++)
        {
            var obj = model.Objects[o];
            string layer = "OBJ" + o.ToString(CultureInfo.InvariantCulture);

            int emitted = 0;
            foreach (var face in obj.Faces)
            {
                if (!FaceInRange(face, obj.Vertices.Count)) continue;
                WriteFace(sb, layer, obj, face);
                emitted++;
            }

            if (emitted == 0)   // no usable faces — fall back to a point cloud
                foreach (var v in obj.Vertices)
                    WritePoint(sb, layer, v);
        }
        sb.Append("0\nENDSEC\n0\nEOF\n");
        return sb.ToString();
    }

    private static bool FaceInRange(int[] face, int vertexCount)
    {
        foreach (int i in face) if (i < 0 || i >= vertexCount) return false;
        return face.Length is 3 or 4;
    }

    private static void WriteFace(StringBuilder sb, string layer, TmdObject obj, int[] face)
    {
        // A DXF 3DFACE always has four corners; a triangle repeats its last vertex.
        var a = obj.Vertices[face[0]];
        var b = obj.Vertices[face[1]];
        var c = obj.Vertices[face[2]];
        var d = obj.Vertices[face.Length == 4 ? face[3] : face[2]];

        sb.Append("0\n3DFACE\n8\n").Append(layer).Append('\n');
        Corner(sb, 0, a); Corner(sb, 1, b); Corner(sb, 2, c); Corner(sb, 3, d);
    }

    private static void Corner(StringBuilder sb, int n, Vertex v)
    {
        sb.Append(10 + n).Append('\n').Append(v.X.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(20 + n).Append('\n').Append(v.Y.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(30 + n).Append('\n').Append(v.Z.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void WritePoint(StringBuilder sb, string layer, Vertex v)
    {
        sb.Append("0\nPOINT\n8\n").Append(layer).Append('\n');
        sb.Append("10\n").Append(v.X.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("20\n").Append(v.Y.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("30\n").Append(v.Z.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }
}
