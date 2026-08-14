// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.FileSystems;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The exFAT reader, proven against a hand-built volume: a boot sector, a FAT, a root directory with a
/// Volume Label entry and one File entry SET (File 0x85 + Stream 0xC0 + Name 0xC1), and the file's data
/// cluster. Reading the label, listing the file with its real size, and extracting its bytes exercises
/// the boot parse, the directory-set decode and the cluster walk end to end.
/// </summary>
public class ExFatTests
{
    private static void WU16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void WU32(byte[] b, int o, uint v) { for (int i = 0; i < 4; i++) b[o + i] = (byte)(v >> (8 * i)); }
    private static void WU64(byte[] b, int o, ulong v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i)); }

    private static byte[] BuildVolume(out byte[] content)
    {
        content = Encoding.ASCII.GetBytes("Hello, exFAT!");   // 13 bytes
        var vol = new byte[4 * 512];                          // 4 sectors of 512

        // ---- boot sector (sector 0) ----
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(vol, 3);
        WU32(vol, 80, 1);      // FatOffset (sectors)
        WU32(vol, 84, 1);      // FatLength
        WU32(vol, 88, 2);      // ClusterHeapOffset (sectors)
        WU32(vol, 92, 2);      // ClusterCount (clusters 2 and 3)
        WU32(vol, 96, 2);      // FirstClusterOfRootDirectory
        vol[108] = 9;          // BytesPerSectorShift -> 512
        vol[109] = 0;          // SectorsPerClusterShift -> 1

        // ---- FAT (sector 1) ----
        const int fat = 512;
        WU32(vol, fat + 0 * 4, 0xFFFFFFF8);
        WU32(vol, fat + 1 * 4, 0xFFFFFFFF);
        WU32(vol, fat + 2 * 4, 0xFFFFFFFF);   // cluster 2 (root dir) -> end of chain
        WU32(vol, fat + 3 * 4, 0xFFFFFFFF);   // cluster 3 (file data)

        // ---- root directory (sector 2 == cluster 2) ----
        int e = 1024;
        // Volume Label entry
        vol[e] = 0x83; vol[e + 1] = 7;
        for (int i = 0; i < 7; i++) WU16(vol, e + 2 + i * 2, "TESTVOL"[i]);
        e += 32;
        // File entry
        vol[e] = 0x85; vol[e + 1] = 2;        // secondary count = 2
        WU16(vol, e + 4, 0);                  // FileAttributes = file
        e += 32;
        // Stream Extension entry
        const string name = "HELLO.TXT";
        vol[e] = 0xC0; vol[e + 1] = 0x03;     // allocated + NoFatChain (contiguous)
        vol[e + 3] = (byte)name.Length;       // NameLength
        WU64(vol, e + 8, (ulong)content.Length);   // ValidDataLength
        WU32(vol, e + 20, 3);                 // FirstCluster
        WU64(vol, e + 24, (ulong)content.Length);  // DataLength
        e += 32;
        // File Name entry
        vol[e] = 0xC1;
        for (int i = 0; i < name.Length; i++) WU16(vol, e + 2 + i * 2, name[i]);
        // (next entry left 0x00 = end of directory)

        // ---- file data (sector 3 == cluster 3) ----
        content.CopyTo(vol, 1536);
        return vol;
    }

    [Fact]
    public void Reads_volume_lists_and_extracts_a_file()
    {
        var vol = BuildVolume(out var content);
        using var s = new MemoryStream(vol);

        var info = ExFat.ReadInfo(s);
        Assert.Equal(512, info.BytesPerSector);
        Assert.Equal(1, info.SectorsPerCluster);
        Assert.Equal((uint)2, info.RootDirCluster);
        Assert.Equal("TESTVOL", info.Label);

        var entries = ExFat.List(s, info, info.RootDirCluster);
        var file = Assert.Single(entries);
        Assert.Equal("HELLO.TXT", file.Name);
        Assert.Equal(content.Length, file.Size);
        Assert.False(file.IsDirectory);

        var resolved = ExFat.Resolve(s, info, "HELLO.TXT");
        Assert.NotNull(resolved);
        Assert.Equal("HELLO.TXT", resolved!.Name);

        using var outMs = new MemoryStream();
        long written = ExFat.ExtractFile(s, info, file, outMs);
        Assert.Equal(content.Length, written);
        Assert.Equal(content, outMs.ToArray());
    }

    [Fact]
    public void Rejects_a_non_exfat_image()
    {
        using var s = new MemoryStream(new byte[512]);
        Assert.Throws<InvalidDataException>(() => ExFat.ReadInfo(s));
    }
}
