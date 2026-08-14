// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.CdInteractive;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Pure CD-i (Green Book) discs use the ISO 9660 layout but with big-endian numeric
/// fields and an empty root-directory record in the volume descriptor — the tree is
/// reached through the big-endian path table. This pins that reader: a synthetic
/// Green Book image (root with a file and a subdirectory, the subdirectory with its
/// own file) must enumerate every file and directory with correct paths and sizes.
/// (The reader is additionally validated end-to-end against a real Philips CD-i
/// "Movie" disc, whose 18-file filesystem it reads correctly.)
/// </summary>
public class CdInteractiveGreenBookTests
{
    private const int S = 2048;

    private static void WriteDirRecord(byte[] img, int at, uint extent, uint size, byte[] id, bool _)
    {
        int recLen = 33 + id.Length;
        if ((recLen & 1) != 0) recLen++;          // pad to even
        img[at] = (byte)recLen;                    // length
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(at + 6, 4), extent);   // extent (BE)
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(at + 14, 4), size);    // size (BE)
        img[at + 32] = (byte)id.Length;            // LEN_FI
        id.CopyTo(img, at + 33);
    }

    private static int WritePathEntry(byte[] img, int at, byte lenDi, uint extent, ushort parent, byte[] name)
    {
        img[at] = lenDi;
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(at + 2, 4), extent);
        BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(at + 6, 2), parent);
        name.CopyTo(img, at + 8);
        int len = 8 + name.Length;
        if ((name.Length & 1) != 0) len++;         // pad identifier to even
        return len;
    }

    private static byte[] BuildGreenBookImage()
    {
        // Sectors: 16 = volume descriptor, 17 = path table, 18 = root dir,
        // 19 = SUB dir; file extents 20/21 need not hold data for enumeration.
        var img = new byte[22 * S];

        // --- Volume descriptor at sector 16 ---
        int vd = 16 * S;
        img[vd] = 1;
        Encoding.ASCII.GetBytes("CD-I ").CopyTo(img, vd + 1);
        img[vd + 6] = 1;
        Pad(img, vd + 8, 32, "CD-RTOS");
        Pad(img, vd + 40, 32, "TEST_CDI_VOLUME");
        // path table size (BE half at 136) and location (BE "type M" at 148)
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(vd + 136, 4), (uint)PathTableBytes());
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(vd + 148, 4), 17);
        // root directory record at offset 156 is left ZERO — as on a real CD-i disc

        // --- Path table at sector 17 (root, then SUB) ---
        int pt = 17 * S;
        int p = pt;
        p += WritePathEntry(img, p, 1, 18, 1, new byte[] { 0x00 });                 // root
        p += WritePathEntry(img, p, 3, 19, 1, Encoding.ASCII.GetBytes("SUB"));      // /SUB

        // --- Root directory at sector 18 ---
        int r = 18 * S;
        WriteDirRecord(img, r, 18, S, new byte[] { 0x00 }, true); r += Even(34);    // .
        WriteDirRecord(img, r, 18, S, new byte[] { 0x01 }, true); r += Even(34);    // ..
        WriteDirRecord(img, r, 19, S, Encoding.ASCII.GetBytes("SUB"), true); r += Even(36);       // SUB (dir)
        WriteDirRecord(img, r, 20, 1234, Encoding.ASCII.GetBytes("HELLO.TXT"), false);            // file

        // --- SUB directory at sector 19 ---
        int su = 19 * S;
        WriteDirRecord(img, su, 19, S, new byte[] { 0x00 }, true); su += Even(34);
        WriteDirRecord(img, su, 18, S, new byte[] { 0x01 }, true); su += Even(34);
        WriteDirRecord(img, su, 21, 5678, Encoding.ASCII.GetBytes("INNER.DAT"), false);           // file

        // Plant known content at HELLO.TXT's extent (sector 20) for the extract test.
        for (int i = 0; i < 1234; i++) img[20 * S + i] = (byte)(i & 0xFF);

        return img;
    }

    private static int PathTableBytes() => (8 + 2) /* root: 8+1 pad→2 */ + (8 + 4) /* SUB: 8+3 pad→4 */;
    private static int Even(int n) => (n & 1) == 0 ? n : n + 1;
    private static void Pad(byte[] img, int at, int len, string s)
    {
        for (int i = 0; i < len; i++) img[at + i] = (byte)' ';
        Encoding.ASCII.GetBytes(s).CopyTo(img, at);
    }

    [Fact]
    public void Reads_a_green_book_tree_via_the_big_endian_path_table()
    {
        using var ms = new MemoryStream(BuildGreenBookImage());
        Assert.True(CdInteractiveReader.IsCdInteractive(ms));
        ms.Position = 0;
        var disc = CdInteractiveReader.Read(ms);

        Assert.Equal(CdInteractiveKind.PureCdi, disc.Kind);
        Assert.Equal("TEST_CDI_VOLUME", disc.VolumeId);

        var files = disc.Filesystem.Files.ToDictionary(f => f.Path, f => f.Size);
        Assert.Equal(2, files.Count);
        Assert.Equal(1234u, files["/HELLO.TXT"]);
        Assert.Equal(5678u, files["/SUB/INNER.DAT"]);

        var dirs = disc.Filesystem.Directories.Select(d => d.Path).ToList();
        Assert.Contains("/SUB", dirs);

        // Total bytes is the sum of file sizes, so the empty offset-156 record no
        // longer hides the whole filesystem.
        Assert.Equal(1234L + 5678L, disc.Filesystem.TotalBytes);
    }

    [Fact]
    public void Extracts_a_file_by_path_truncated_to_its_length()
    {
        using var src = new MemoryStream(BuildGreenBookImage());
        using var outp = new MemoryStream();
        long wrote = CdInteractiveReader.ExtractFile(src, "/HELLO.TXT", outp);

        Assert.Equal(1234, wrote);
        var got = outp.ToArray();
        Assert.Equal(1234, got.Length);                       // truncated to the directory length
        for (int i = 0; i < got.Length; i++)
            Assert.Equal((byte)(i & 0xFF), got[i]);           // matches the planted content
    }
}
