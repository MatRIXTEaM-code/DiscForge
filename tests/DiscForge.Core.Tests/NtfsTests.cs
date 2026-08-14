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
/// The NTFS reader, proven at two levels. The two error-prone primitives — the run-list decoder and the
/// Update-Sequence-Array fixup — are checked against hand-computed vectors. Then a synthetic volume (boot
/// sector + a contiguous MFT whose record 0 describes itself, a root directory record, a file with resident
/// <c>$DATA</c> and a file with non-resident <c>$DATA</c>) is opened, listed, resolved and extracted end to end.
/// </summary>
public class NtfsTests
{
    private static void WU16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void WU32(byte[] b, int o, uint v) { for (int i = 0; i < 4; i++) b[o + i] = (byte)(v >> (8 * i)); }
    private static void WU64(byte[] b, int o, ulong v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i)); }

    // ---- pure primitives ---------------------------------------------------

    [Fact]
    public void DecodeDataRuns_decodes_two_runs_with_delta_offsets()
    {
        // Run 1: length 0x30 (48) clusters at LCN 0x60 (96).
        // Run 2: length 0x10 (16) clusters at LCN 96 + 0x0100 (256) = 352.
        byte[] runs = { 0x11, 0x30, 0x60, 0x21, 0x10, 0x00, 0x01, 0x00 };
        var decoded = Ntfs.DecodeDataRuns(runs);

        Assert.Equal(2, decoded.Count);
        Assert.Equal(48, decoded[0].LengthClusters);
        Assert.Equal(96, decoded[0].Lcn);
        Assert.False(decoded[0].Sparse);
        Assert.Equal(16, decoded[1].LengthClusters);
        Assert.Equal(352, decoded[1].Lcn);
    }

    [Fact]
    public void DecodeDataRuns_sign_extends_a_negative_delta()
    {
        // Single run, length 8, offset byte 0x80 = -128 as a signed 1-byte delta.
        byte[] runs = { 0x11, 0x08, 0x80, 0x00 };
        var decoded = Ntfs.DecodeDataRuns(runs);
        var run = Assert.Single(decoded);
        Assert.Equal(8, run.LengthClusters);
        Assert.Equal(-128, run.Lcn);
    }

    [Fact]
    public void DecodeDataRuns_marks_a_zero_offset_run_sparse()
    {
        byte[] runs = { 0x01, 0x05, 0x00 };   // length 5, no offset field → sparse
        var run = Assert.Single(Ntfs.DecodeDataRuns(runs));
        Assert.True(run.Sparse);
        Assert.Equal(5, run.LengthClusters);
    }

    [Fact]
    public void ApplyFixups_restores_each_sector_tail()
    {
        // Two 512-byte sectors, USA at offset 48: [USN, fixup0, fixup1].
        var rec = new byte[1024];
        WU16(rec, 48, 0xAAAA);   // USN
        WU16(rec, 50, 0x2211);   // real bytes for the end of sector 0
        WU16(rec, 52, 0x4433);   // real bytes for the end of sector 1
        WU16(rec, 510, 0xAAAA);  // sector 0 tail held the USN
        WU16(rec, 1022, 0xAAAA); // sector 1 tail held the USN

        Ntfs.ApplyFixups(rec, usaOffset: 48, usaCount: 3, bytesPerSector: 512);

        Assert.Equal(0x11, rec[510]); Assert.Equal(0x22, rec[511]);
        Assert.Equal(0x33, rec[1022]); Assert.Equal(0x44, rec[1023]);
    }

    // ---- synthetic volume round-trip ---------------------------------------

    private const int Bps = 512;
    private const int RecSize = 1024;
    private const int MftLcn = 4;
    private const int MftClusters = 16;      // 16 * 512 = 8192 = 8 records
    private const int BigDataLcn = 20;

    // Write one attribute header + value (resident). Returns the offset past it.
    private static int WriteResident(byte[] rec, int o, uint type, byte[] value)
    {
        const int valOff = 24;
        int total = Align8(valOff + value.Length);
        WU32(rec, o + 0, type);
        WU32(rec, o + 4, (uint)total);
        rec[o + 8] = 0;                       // resident
        WU32(rec, o + 16, (uint)value.Length);
        WU16(rec, o + 20, valOff);
        value.CopyTo(rec, o + valOff);
        return o + total;
    }

    private static int WriteNonResident(byte[] rec, int o, uint type, byte[] runList, long realSize)
    {
        const int runsOff = 64;
        int total = Align8(runsOff + runList.Length);
        WU32(rec, o + 0, type);
        WU32(rec, o + 4, (uint)total);
        rec[o + 8] = 1;                       // non-resident
        WU16(rec, o + 32, runsOff);
        WU16(rec, o + 34, 0);                 // compression unit = 0
        WU64(rec, o + 40, (ulong)realSize);   // allocated
        WU64(rec, o + 48, (ulong)realSize);   // real
        WU64(rec, o + 56, (ulong)realSize);   // initialized
        runList.CopyTo(rec, o + runsOff);
        return o + total;
    }

    private static int Align8(int n) => (n + 7) & ~7;

    private static byte[] FileNameValue(long parentMft, string name, byte ns)
    {
        var v = new byte[66 + name.Length * 2];
        WU64(v, 0, (ulong)parentMft);         // parent reference (low 48 bits = MFT number)
        v[64] = (byte)name.Length;
        v[65] = ns;
        for (int i = 0; i < name.Length; i++) WU16(v, 66 + i * 2, name[i]);
        return v;
    }

    private static void InitRecord(byte[] mft, int recNum, ushort flags)
    {
        int b = recNum * RecSize;
        Encoding.ASCII.GetBytes("FILE").CopyTo(mft, b);
        WU16(mft, b + 4, 48);                 // USA offset
        WU16(mft, b + 6, 3);                  // USA count (1 USN + 2 sector fixups)
        WU16(mft, b + 20, 56);                // first attribute offset (aligned past USA)
        WU16(mft, b + 22, flags);             // 0x01 in-use, 0x02 directory
        WU32(mft, b + 24, (uint)RecSize);     // used size
        WU32(mft, b + 28, (uint)RecSize);     // allocated size
        WU32(mft, b + 56, 0xFFFFFFFF);        // provisional end marker; overwritten as attrs are added
    }

    private static byte[] BuildVolume(out byte[] helloContent, out byte[] bigContent)
    {
        helloContent = Encoding.ASCII.GetBytes("Hello, NTFS!");
        bigContent = new byte[400];
        for (int i = 0; i < bigContent.Length; i++) bigContent[i] = (byte)(i * 7 + 1);

        var mft = new byte[8 * RecSize];

        // Record 0 — $MFT: one non-resident $DATA describing the whole MFT.
        InitRecord(mft, 0, 0x01);
        {
            int o = 0 * RecSize + 56;
            byte[] runList = { 0x11, (byte)MftClusters, (byte)MftLcn, 0x00 };
            o = WriteNonResident(mft, o, 0x80, runList, (long)MftClusters * Bps);
            WU32(mft, o, 0xFFFFFFFF);
        }

        // Record 5 — root directory.
        InitRecord(mft, 5, 0x03);
        {
            int o = 5 * RecSize + 56;
            o = WriteResident(mft, o, 0x30, FileNameValue(Ntfs.RootMft, ".", ns: 3));
            WU32(mft, o, 0xFFFFFFFF);
        }

        // Record 6 — hello.txt: resident $DATA.
        InitRecord(mft, 6, 0x01);
        {
            int o = 6 * RecSize + 56;
            o = WriteResident(mft, o, 0x30, FileNameValue(Ntfs.RootMft, "hello.txt", ns: 1));
            o = WriteResident(mft, o, 0x80, helloContent);
            WU32(mft, o, 0xFFFFFFFF);
        }

        // Record 7 — big.bin: non-resident $DATA in one cluster at LCN 20.
        InitRecord(mft, 7, 0x01);
        {
            int o = 7 * RecSize + 56;
            o = WriteResident(mft, o, 0x30, FileNameValue(Ntfs.RootMft, "big.bin", ns: 1));
            byte[] runList = { 0x11, 0x01, (byte)BigDataLcn, 0x00 };
            o = WriteNonResident(mft, o, 0x80, runList, bigContent.Length);
            WU32(mft, o, 0xFFFFFFFF);
        }

        // Lay the pieces onto a 24-cluster disk.
        var disk = new byte[24 * Bps];

        // Boot sector.
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(disk, 3);
        WU16(disk, 11, Bps);
        disk[13] = 1;                         // sectors per cluster
        WU64(disk, 48, MftLcn);
        disk[64] = 2;                         // clusters per MFT record → 2 * 512 = 1024

        Array.Copy(mft, 0, disk, MftLcn * Bps, mft.Length);
        Array.Copy(bigContent, 0, disk, BigDataLcn * Bps, bigContent.Length);
        return disk;
    }

    [Fact]
    public void Opens_lists_resolves_and_extracts_resident_and_nonresident_files()
    {
        var disk = BuildVolume(out var hello, out var big);
        using var s = new MemoryStream(disk);

        var vol = Ntfs.Open(s);
        Assert.Equal(512, vol.Info.BytesPerSector);
        Assert.Equal(1, vol.Info.SectorsPerCluster);
        Assert.Equal(1024, vol.Info.MftRecordSize);

        var entries = vol.List(Ntfs.RootMft);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "hello.txt" && e.Size == hello.Length && !e.IsDirectory);
        Assert.Contains(entries, e => e.Name == "big.bin" && e.Size == big.Length && !e.IsDirectory);

        var resolved = vol.Resolve("hello.txt");
        Assert.NotNull(resolved);
        using (var outMs = new MemoryStream())
        {
            long n = vol.Extract(resolved!, outMs);
            Assert.Equal(hello.Length, n);
            Assert.Equal(hello, outMs.ToArray());
        }

        var bigNode = vol.Resolve("big.bin");
        Assert.NotNull(bigNode);
        using (var outMs = new MemoryStream())
        {
            long n = vol.Extract(bigNode!, outMs);
            Assert.Equal(big.Length, n);
            Assert.Equal(big, outMs.ToArray());
        }
    }

    [Fact]
    public void Rejects_a_non_ntfs_image()
    {
        using var s = new MemoryStream(new byte[512]);
        Assert.Throws<InvalidDataException>(() => Ntfs.Open(s));
    }
}
