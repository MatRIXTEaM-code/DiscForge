// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The mastering fingerprint reads a disc's ISO 9660 volume-descriptor identity and compares two copies. The test
/// builds hand-crafted PVD images: two identical copies read as IDENTICAL MASTERING; a re-mastered reproduction
/// (same title and size, different mastering tool / timestamp / padding) reads as DIVERGENT MASTERING and names
/// the differing fields; a different title reads as DIFFERENT VOLUME.
/// </summary>
public class MasteringFingerprintTests
{
    private static byte[] Pvd(string volId, string app, string created)
    {
        var b = new byte[2048];
        b[0] = 1; Encoding.ASCII.GetBytes("CD001").CopyTo(b, 1); b[6] = 1;
        void Put(int off, string s, int n)
        {
            var e = Encoding.ASCII.GetBytes(s);
            for (int i = 0; i < n; i++) b[off + i] = i < e.Length ? e[i] : (byte)' ';
        }
        Put(8, "PLAYSTATION", 32);
        Put(40, volId, 32);
        BitConverter.GetBytes((uint)20).CopyTo(b, 80);
        BitConverter.GetBytes((ushort)2048).CopyTo(b, 128);
        Put(318, "SONY", 128);
        Put(446, "MASTER HOUSE A", 128);
        Put(574, app, 128);
        Put(813, created, 17);
        Put(830, created, 17);
        return b;
    }

    private static string WriteImage(string dir, string name, string volId, string app, string created, byte tail)
    {
        Directory.CreateDirectory(dir);
        var img = new byte[20 * 2048];
        Pvd(volId, app, created).CopyTo(img, 16 * 2048);
        for (int i = 17 * 2048; i < img.Length; i++) img[i] = tail;
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, img);
        return path;
    }

    [Fact]
    public void Identical_copies_read_as_identical_mastering()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_mf_" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = WriteImage(dir, "a.iso", "MY GAME", "CD MASTERING SUITE 4.2", "2001010112000000", 0x00);
            var b = WriteImage(dir, "b.iso", "MY GAME", "CD MASTERING SUITE 4.2", "2001010112000000", 0x00);
            var cmp = MasteringPrinter.Compare(MasteringPrinter.Extract(a), MasteringPrinter.Extract(b));
            Assert.Equal(MasteringVerdict.IdenticalMastering, cmp.Verdict);
            Assert.Empty(cmp.Divergences);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_remastered_reproduction_is_flagged_divergent_with_the_fields()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_mf_" + Guid.NewGuid().ToString("N"));
        try
        {
            var genuine = WriteImage(dir, "g.iso", "MY GAME", "CD MASTERING SUITE 4.2", "2001010112000000", 0x00);
            var repro = WriteImage(dir, "r.iso", "MY GAME", "IMGBURN 2.5.8.0", "2021060309300000", 0xAA);
            var cmp = MasteringPrinter.Compare(MasteringPrinter.Extract(genuine), MasteringPrinter.Extract(repro));

            Assert.Equal(MasteringVerdict.DivergentMastering, cmp.Verdict);
            Assert.Contains(cmp.Divergences, d => d.Contains("mastering tool"));
            Assert.Contains(cmp.Divergences, d => d.Contains("creation time"));
            Assert.Contains(cmp.Divergences, d => d.Contains("padding"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_different_title_reads_as_different_volume()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_mf_" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = WriteImage(dir, "a.iso", "MY GAME", "TOOL", "2001010112000000", 0x00);
            var b = WriteImage(dir, "b.iso", "OTHER GAME", "TOOL", "2001010112000000", 0x00);
            var cmp = MasteringPrinter.Compare(MasteringPrinter.Extract(a), MasteringPrinter.Extract(b));
            Assert.Equal(MasteringVerdict.DifferentVolume, cmp.Verdict);
            Assert.Single(cmp.Divergences);   // only the volume id differs
        }
        finally { Directory.Delete(dir, true); }
    }
}
