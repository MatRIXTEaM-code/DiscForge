// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DiscForge.Core.Preservation;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the PAR2 reader/verifier, built by writing a minimal but spec-accurate PAR2 set (Main,
/// FileDescription, Input-File-Slice-Checksum, one recovery slice, Creator) byte for byte and verifying
/// against files on disk. The packet MD5s and per-slice checksums are computed exactly as par2cmdline
/// does, so the parse, the packet-integrity check and the slice-level damage count are all pinned. The
/// reader is additionally validated against real par2cmdline output in the validation harness.
/// </summary>
public class Par2Tests
{
    private static readonly byte[] SetId = Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray();
    private const int SliceSize = 16;

    private static byte[] Type(string s)
    {
        var t = new byte[16];
        Encoding.ASCII.GetBytes(s).CopyTo(t, 0);
        return t;
    }

    private static byte[] Packet(byte[] type16, byte[] body)
    {
        long len = 64 + body.Length;
        var pkt = new byte[len];
        "PAR2\0PKT"u8.ToArray().CopyTo(pkt, 0);
        BinaryPrimitives.WriteInt64LittleEndian(pkt.AsSpan(8, 8), len);
        SetId.CopyTo(pkt, 32);
        type16.CopyTo(pkt, 48);
        body.CopyTo(pkt, 64);
        // Packet MD5 covers set-id + type + body (offset 0x20 to end).
        MD5.HashData(pkt.AsSpan(32)).CopyTo(pkt, 16);
        return pkt;
    }

    // Write a one-file PAR2 set with one recovery slice into a temp dir; returns (dir, par2Path).
    private static (string dir, string par2) BuildSet(byte[] content, string name = "a.bin")
    {
        string dir = Path.Combine(Path.GetTempPath(), "par2t_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), content);

        var fileId = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        int sliceCount = (content.Length + SliceSize - 1) / SliceSize;

        // Main: sliceSize + numFiles + fileId
        var main = new byte[8 + 4 + 16];
        BinaryPrimitives.WriteInt64LittleEndian(main.AsSpan(0, 8), SliceSize);
        BinaryPrimitives.WriteInt32LittleEndian(main.AsSpan(8, 4), 1);
        fileId.CopyTo(main, 12);

        // FileDesc: fileId + fullMD5 + md5-16k + length + name
        byte[] fullMd5 = MD5.HashData(content);
        byte[] md5_16k = MD5.HashData(content.AsSpan(0, Math.Min(16384, content.Length)));
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var fd = new byte[16 + 16 + 16 + 8 + nameBytes.Length];
        fileId.CopyTo(fd, 0); fullMd5.CopyTo(fd, 16); md5_16k.CopyTo(fd, 32);
        BinaryPrimitives.WriteInt64LittleEndian(fd.AsSpan(48, 8), content.Length);
        nameBytes.CopyTo(fd, 56);

        // IFSC: fileId + per-slice (md5 + crc32), each slice zero-padded to SliceSize
        var ifsc = new List<byte>();
        ifsc.AddRange(fileId);
        for (int k = 0; k < sliceCount; k++)
        {
            var slice = new byte[SliceSize];
            int have = Math.Min(SliceSize, content.Length - k * SliceSize);
            Array.Copy(content, k * SliceSize, slice, 0, have);
            ifsc.AddRange(MD5.HashData(slice));
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.Compute(slice));
            ifsc.AddRange(crc);
        }

        // One recovery slice (exponent 0 + slice-sized data) so the repair budget is 1.
        var recv = new byte[4 + SliceSize];

        using var fs = File.Create(Path.Combine(dir, "a.par2"));
        foreach (var p in new[]
        {
            Packet(Type("PAR 2.0\0Main"), main),
            Packet(Type("PAR 2.0\0FileDesc"), fd),
            Packet(Type("PAR 2.0\0IFSC"), ifsc.ToArray()),
            Packet(Type("PAR 2.0\0RecvSlic"), recv),
            Packet(Type("PAR 2.0\0Creator"), Encoding.ASCII.GetBytes("DiscForge test")),
        }) fs.Write(p);
        fs.Flush();
        return (dir, Path.Combine(dir, "a.par2"));
    }

    [Fact]
    public void Reads_and_verifies_an_intact_set()
    {
        var (_, par2) = BuildSet(Encoding.ASCII.GetBytes("hello preservation!"));  // 19 bytes -> 2 slices
        var r = Par2.Verify(par2);

        Assert.Equal(16, r.SliceSize);
        Assert.Single(r.Files);
        Assert.Equal(2, r.TotalDataSlices);
        Assert.Equal(1, r.RecoverySlices);
        Assert.Equal(0, r.BadPackets);
        Assert.True(r.AllOk);
        Assert.Equal(Par2FileStatus.Ok, r.Files[0].Status);
        Assert.Equal("DiscForge test", r.Creator);
    }

    [Fact]
    public void Detects_a_corrupted_slice_and_reports_it_repairable()
    {
        var (dir, par2) = BuildSet(new byte[16 + 16], "a.bin");   // exactly 2 slices
        // Corrupt one byte in the first slice.
        var path = Path.Combine(dir, "a.bin");
        var b = File.ReadAllBytes(path); b[3] ^= 0xFF; File.WriteAllBytes(path, b);

        var r = Par2.Verify(par2);
        Assert.False(r.AllOk);
        Assert.Equal(Par2FileStatus.Corrupt, r.Files[0].Status);
        Assert.Equal(1, r.Files[0].DamagedSlices);
        Assert.True(r.Repairable);       // 1 damaged <= 1 recovery slice
    }

    [Fact]
    public void Reports_a_missing_file_and_its_whole_slice_span()
    {
        var (dir, par2) = BuildSet(new byte[10], "a.bin");        // 1 slice
        File.Delete(Path.Combine(dir, "a.bin"));

        var r = Par2.Verify(par2);
        Assert.Equal(Par2FileStatus.Missing, r.Files[0].Status);
        Assert.Equal(1, r.Files[0].DamagedSlices);
        Assert.True(r.Repairable);       // 1 missing <= 1 recovery slice
    }

    [Fact]
    public void A_damaged_PAR2_packet_is_counted_as_bad()
    {
        var (dir, par2) = BuildSet(new byte[10]);
        // Flip a byte inside the Creator packet body (after the first packets) to break its own MD5.
        var raw = File.ReadAllBytes(par2);
        raw[^2] ^= 0xFF;
        File.WriteAllBytes(par2, raw);

        var r = Par2.Verify(par2);
        Assert.True(r.BadPackets >= 1);
        Assert.False(r.AllOk);
    }
}
