// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.ThreeDo;
using Xunit;

namespace DiscForge.Core.Tests;

public class OperaFsTests
{
    private const int Bs = 2048;

    private static void Be32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
    private static void Str(byte[] b, int o, string s, int len)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        for (int i = 0; i < len; i++) b[o + i] = i < bytes.Length ? bytes[i] : (byte)0;
    }

    // Write a directory entry; returns its size in bytes.
    private static int WriteEntry(byte[] img, int off, string name, string tag, bool dir,
        uint byteCount, uint blockCount, uint firstAvatar)
    {
        Be32(img, off + 0, dir ? 0x02u : 0x00u);   // flags: low byte 2 = directory
        Be32(img, off + 4, 0);                       // id
        Str(img, off + 8, tag, 4);                   // type tag
        Be32(img, off + 12, Bs);                     // block size
        Be32(img, off + 16, byteCount);
        Be32(img, off + 20, blockCount);
        Str(img, off + 32, name, 32);
        Be32(img, off + 64, 0);                      // last avatar index = 0 → one avatar
        Be32(img, off + 68, firstAvatar);            // avatar 0
        return 72 + 4;                               // fixed + one avatar
    }

    private static void WriteDirHeader(byte[] img, int block, uint firstFree)
    {
        int o = block * Bs;
        Be32(img, o + 0, 0xFFFFFFFF);   // next block
        Be32(img, o + 4, 0xFFFFFFFF);   // prev block
        Be32(img, o + 8, 0);            // flags
        Be32(img, o + 12, firstFree);   // first free byte offset
        Be32(img, o + 16, 20);          // first entry offset
    }

    // Block 0: volume label. Block 1: root dir (HELLO.file + SUB dir). Block 2: SUB dir (INNER.file).
    private static byte[] BuildImage()
    {
        var img = new byte[Bs * 4];

        img[0] = 1;                                   // record type
        for (int i = 0; i < 5; i++) img[1 + i] = 0x5A;   // ZZZZZ
        Str(img, 40, "3DO GAME DISC", 32);            // label
        Be32(img, 76, Bs);                            // block size
        Be32(img, 80, 4);                             // block count
        Be32(img, 88, 1);                             // root dir blocks
        Be32(img, 96, 0);                             // last root copy = 0
        Be32(img, 100, 1);                            // root dir avatar 0 → block 1

        // Root directory at block 1.
        int o = 1 * Bs + 20;
        o += WriteEntry(img, o, "HELLO", "*fil", dir: false, byteCount: 1000, blockCount: 1, firstAvatar: 3);
        o += WriteEntry(img, o, "SUB", "*dir", dir: true, byteCount: 0, blockCount: 1, firstAvatar: 2);
        WriteDirHeader(img, 1, (uint)(o - 1 * Bs));

        // Sub directory at block 2.
        int p = 2 * Bs + 20;
        p += WriteEntry(img, p, "INNER", "*fil", dir: false, byteCount: 42, blockCount: 1, firstAvatar: 3);
        WriteDirHeader(img, 2, (uint)(p - 2 * Bs));

        return img;
    }

    [Fact]
    public void Recognises_an_opera_volume()
    {
        Assert.True(OperaFs.IsVolume(BuildImage()));
        Assert.False(OperaFs.IsVolume(new byte[Bs]));   // all zero
    }

    [Fact]
    public void Reads_the_volume_label_and_geometry()
    {
        var vol = OperaFs.Read(BuildImage());
        Assert.Equal("3DO GAME DISC", vol.Label);
        Assert.Equal(2048u, vol.BlockSize);
        Assert.Equal(4u, vol.BlockCount);
    }

    [Fact]
    public void Lists_root_files_and_directories()
    {
        var vol = OperaFs.Read(BuildImage());
        Assert.Contains(vol.Entries, e => e.Path == "/HELLO" && !e.IsDirectory && e.ByteCount == 1000);
        Assert.Contains(vol.Entries, e => e.Path == "/SUB" && e.IsDirectory);
    }

    [Fact]
    public void Recurses_into_subdirectories()
    {
        var vol = OperaFs.Read(BuildImage());
        Assert.Contains(vol.Entries, e => e.Path == "/SUB/INNER" && !e.IsDirectory && e.ByteCount == 42);
    }

    [Fact]
    public void Total_bytes_sums_the_files()
    {
        var vol = OperaFs.Read(BuildImage());
        Assert.Equal(1042, vol.TotalBytes);   // 1000 + 42
    }

    [Fact]
    public void A_non_opera_image_throws()
    {
        var junk = new byte[Bs * 2];
        Encoding.ASCII.GetBytes("NOT AN OPERA DISC").CopyTo(junk, 0);
        Assert.Throws<OperaFormatException>(() => OperaFs.Read(junk));
    }

    [Fact]
    public void Render_shows_the_tree()
    {
        var text = OperaFs.Render(OperaFs.Read(BuildImage()));
        Assert.Contains("3DO Opera volume", text);
        Assert.Contains("/SUB/INNER", text);
    }
}
