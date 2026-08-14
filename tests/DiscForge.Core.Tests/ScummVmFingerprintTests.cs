// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text;
using DiscForge.Core.ScummVm;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The ScummVM Advanced-Detector fingerprint: MD5 of the first N bytes (default
/// 5000) plus the file's exact size. These verify the head-bounded MD5 matches a
/// plain MD5 of the same leading bytes, that a file shorter than the limit hashes
/// whole, and that directory scanning reports sizes and stable, relative names.
/// </summary>
public class ScummVmFingerprintTests
{
    private static string Md5Hex(byte[] data) =>
        System.Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    [Fact]
    public void HeadMd5_HashesOnlyTheFirstNBytes()
    {
        var data = new byte[10000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);

        using var ms = new MemoryStream(data);
        string got = ScummVmFingerprint.HeadMd5(ms, 5000);

        Assert.Equal(Md5Hex(data[..5000]), got);
        // Changing a byte past the limit must not change the fingerprint.
        var data2 = (byte[])data.Clone();
        data2[6000] ^= 0xFF;
        using var ms2 = new MemoryStream(data2);
        Assert.Equal(got, ScummVmFingerprint.HeadMd5(ms2, 5000));
    }

    [Fact]
    public void HeadMd5_ShortStream_HashesTheWholeThing()
    {
        var data = Encoding.ASCII.GetBytes("MONKEY.001 tiny payload");
        using var ms = new MemoryStream(data);
        Assert.Equal(Md5Hex(data), ScummVmFingerprint.HeadMd5(ms, 5000));
    }

    [Fact]
    public void ForFile_ReportsNameSizeAndHeadMd5()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_svm_").FullName;
        try
        {
            var data = new byte[7000];
            new Random(1234).NextBytes(data);
            string file = Path.Combine(dir, "COMI.LA0");
            File.WriteAllBytes(file, data);

            var fp = ScummVmFingerprint.ForFile(file, 5000);
            Assert.Equal("COMI.LA0", fp.Name);
            Assert.Equal(7000, fp.Size);
            Assert.Equal(Md5Hex(data[..5000]), fp.Md5);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ForDirectory_ScansFiles_WithRelativeSortedNames()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_svm_").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "MONKEY.000"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(dir, "MONKEY.001"), new byte[] { 4, 5, 6, 7 });
            var sub = Directory.CreateDirectory(Path.Combine(dir, "MUSIC"));
            File.WriteAllBytes(Path.Combine(sub.FullName, "track1.flac"), new byte[] { 8 });

            var top = ScummVmFingerprint.ForDirectory(dir, recursive: false);
            Assert.Equal(new[] { "MONKEY.000", "MONKEY.001" }, top.Select(p => p.Name).ToArray());
            Assert.Equal(3, top[0].Size);
            Assert.Equal(4, top[1].Size);

            var all = ScummVmFingerprint.ForDirectory(dir, recursive: true);
            Assert.Contains(all, p => p.Name == "MUSIC/track1.flac" && p.Size == 1);
            Assert.Equal(3, all.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
