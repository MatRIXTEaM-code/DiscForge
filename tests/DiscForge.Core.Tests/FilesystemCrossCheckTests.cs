// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Files;
using DiscForge.Core.Iso;
using DiscForge.Core.Udf;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// fs-verify cross-checks a disc's filesystem views (ISO 9660, Joliet, UDF) by content,
/// not by name, and reports whether they agree. These tests build a real UDF-bridge image
/// (where the ISO and UDF trees are independent but describe the same files) and confirm:
/// a clean bridge AGREEs; a plain ISO is SINGLE (nothing to cross-check); a bridge whose
/// ISO directory record has been tampered to shrink a file is caught as DIVERGENT; and a
/// bridge whose UDF File Set Descriptor is trashed (anchor left intact) is INCOMPLETE.
/// </summary>
public class FilesystemCrossCheckTests
{
    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static IsoBuilder.Node[] Tree() => new[]
    {
        IsoBuilder.Node.File("readme.txt", Bytes("root readme content")),
        IsoBuilder.Node.Dir("docs", new[] { IsoBuilder.Node.File("intro.txt", Bytes("nested doc")) }),
        IsoBuilder.Node.File("blob.bin", MakeBlob(6000)),
    };

    private static byte[] MakeBlob(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((i * 29 + 3) % 256);
        return b;
    }

    private static string WriteTemp(byte[] image, string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), "dforge_fsv_" + Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(path, image);
        return path;
    }

    [Fact]
    public void A_clean_bridge_agrees_across_all_views()
    {
        var img = UdfBridgeBuilder.Build("FSTEST", Tree());
        var path = WriteTemp(img, ".iso");
        try
        {
            var r = ImageBrowser.CrossCheck(path);
            Assert.Equal(CrossCheckVerdict.Agree, r.Verdict);
            Assert.Contains(r.Views, v => v.Kind == "ISO 9660 (8.3)" && v.FileCount == 3);
            Assert.Contains(r.Views, v => v.Kind == "UDF" && v.FileCount == 3);
            Assert.Empty(r.Discrepancies);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// HFS cataloguing must not false-positive: a plain (non-hybrid) bridge carries no HFS volume, so no
    /// "HFS (Mac)" view and no hybrid catalogue should appear. (The positive HFS-hybrid path is validated
    /// in-cloud against genisoimage-authored hybrids, since there is no HFS builder to make one in CI.)
    /// </summary>
    [Fact]
    public void A_non_hybrid_bridge_reports_no_hfs_view()
    {
        var img = UdfBridgeBuilder.Build("NOHFS", Tree());
        var path = WriteTemp(img, ".iso");
        try
        {
            var r = ImageBrowser.CrossCheck(path);
            Assert.DoesNotContain(r.Views, v => v.Kind == "HFS (Mac)");
            Assert.DoesNotContain(r.Discrepancies, d => d.Kind == "hybrid");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_plain_iso_is_single()
    {
        var img = IsoBuilder.BuildTree("PLAIN", Tree(), joliet: true).Image;
        var path = WriteTemp(img, ".iso");
        try
        {
            var r = ImageBrowser.CrossCheck(path);
            Assert.Equal(CrossCheckVerdict.Single, r.Verdict);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_tampered_iso_record_is_caught_as_divergent()
    {
        var img = UdfBridgeBuilder.Build("FSTEST", Tree());
        const int SS = 2048;

        // Shrink readme.txt's ISO 9660 directory-record size so the ISO view of the file
        // no longer matches the bytes UDF delivers.
        int pvd = 16 * SS;
        uint rootExt = BitConverter.ToUInt32(img, pvd + 156 + 2);
        uint rootLen = BitConverter.ToUInt32(img, pvd + 156 + 10);
        int o = (int)(rootExt * SS);
        int end = o + (int)rootLen;
        bool patched = false;
        while (o < end)
        {
            int len = img[o];
            if (len == 0) break;
            int fiLen = img[o + 32];
            string name = Encoding.ASCII.GetString(img, o + 33, fiLen);
            if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase))
            {
                BitConverter.GetBytes((uint)5).CopyTo(img, o + 10);                       // LE size
                BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((uint)5)).CopyTo(img, o + 14); // BE size
                patched = true;
                break;
            }
            o += len;
        }
        Assert.True(patched, "README record not found to tamper");

        var path = WriteTemp(img, ".iso");
        try
        {
            var r = ImageBrowser.CrossCheck(path);
            Assert.Equal(CrossCheckVerdict.Divergent, r.Verdict);
            Assert.Contains(r.Discrepancies, d => d.Kind == "only-in-iso");
            Assert.Contains(r.Discrepancies, d => d.Kind == "only-in-udf");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_bridge_with_a_trashed_udf_file_set_is_incomplete()
    {
        var img = UdfBridgeBuilder.Build("FSTEST", Tree());
        const int SS = 2048;
        // Trash the File Set Descriptor at partition block 0 (sector 257). The anchor at
        // 256 stays valid, so the disc is still recognised as UDF but can no longer be read.
        for (int i = 257 * SS; i < 258 * SS; i++) img[i] = 0xFF;

        var path = WriteTemp(img, ".iso");
        try
        {
            var r = ImageBrowser.CrossCheck(path);
            Assert.Equal(CrossCheckVerdict.Incomplete, r.Verdict);
            Assert.Contains(r.Views, v => v.Kind == "UDF" && v.Error is not null);
        }
        finally { File.Delete(path); }
    }
}
