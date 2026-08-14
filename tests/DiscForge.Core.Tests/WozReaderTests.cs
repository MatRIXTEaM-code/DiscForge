// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Floppy;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// WOZ (Apple II) container reader — proven by round-trip: build a spec-shaped WOZ2 image
/// (header + INFO + TMAP + TRKS + META with a real CRC-32) and confirm the reader recovers
/// every field and validates the CRC, plus that it catches a corrupted CRC. WOZ is
/// self-checksummed, so this round-trip is a genuine proof of the parse.
/// </summary>
public class WozReaderTests
{
    private static byte[] BuildWoz2()
    {
        var body = new List<byte>();

        void Chunk(string id, byte[] data)
        {
            body.AddRange(Encoding.ASCII.GetBytes(id));
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)data.Length);
            body.AddRange(len);
            body.AddRange(data);
        }

        // INFO (60 bytes).
        var info = new byte[60];
        info[0] = 2;                 // info version
        info[1] = 1;                 // 5.25"
        info[2] = 1;                 // write protected
        info[3] = 0;                 // synchronized
        info[4] = 1;                 // cleaned
        var creator = Encoding.UTF8.GetBytes("DiscForge test".PadRight(32, ' '));
        Array.Copy(creator, 0, info, 5, 32);
        info[37] = 1;                // sides
        info[38] = 1;                // boot: 16-sector
        info[39] = 32;               // optimal bit timing (4 µs)
        BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(44, 2), 13);   // largest track blocks
        Chunk("INFO", info);

        // TMAP (160 bytes): map the first 4 quarter-tracks to track index 0, rest empty.
        var tmap = new byte[160];
        Array.Fill(tmap, (byte)0xFF);
        for (int i = 0; i < 4; i++) tmap[i] = 0;
        Chunk("TMAP", tmap);

        // TRKS: 160 × 8-byte metadata entries; one real track at index 0.
        var trks = new byte[160 * 8];
        BinaryPrimitives.WriteUInt16LittleEndian(trks.AsSpan(0, 2), 3);        // start block
        BinaryPrimitives.WriteUInt16LittleEndian(trks.AsSpan(2, 2), 13);       // block count
        BinaryPrimitives.WriteUInt32LittleEndian(trks.AsSpan(4, 4), 50000);    // bit count
        Chunk("TRKS", trks);

        // META.
        Chunk("META", Encoding.UTF8.GetBytes("title\tApple Panic\nlanguage\tEnglish\n"));

        // Header: WOZ2 + integrity bytes + CRC32(body).
        var head = new byte[12];
        Encoding.ASCII.GetBytes("WOZ2").CopyTo(head, 0);
        head[4] = 0xFF; head[5] = 0x0A; head[6] = 0x0D; head[7] = 0x0A;
        uint crc = Crc32.Compute(body.ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8, 4), crc);

        return head.Concat(body).ToArray();
    }

    [Fact]
    public void Reads_every_info_field_and_validates_the_crc()
    {
        var disk = WozReader.Parse(BuildWoz2());

        Assert.Equal(2, disk.FormatVersion);
        Assert.True(disk.CrcPresent);
        Assert.True(disk.CrcValid);
        Assert.Equal("5.25\"", disk.Info.DiskTypeName);
        Assert.True(disk.Info.WriteProtected);
        Assert.True(disk.Info.Cleaned);
        Assert.Equal("DiscForge test", disk.Info.Creator);
        Assert.Equal(1, disk.Info.BootSectorFormat);
        Assert.Equal(32, disk.Info.OptimalBitTiming);
        Assert.Equal(13, disk.Info.LargestTrackBlocks);
    }

    [Fact]
    public void Reads_the_track_map_and_track_table()
    {
        var disk = WozReader.Parse(BuildWoz2());

        Assert.Equal(4, disk.MappedPositions);
        Assert.Single(disk.Tracks);
        var t = disk.Tracks[0];
        Assert.Equal(0, t.Index);
        Assert.Equal(3, t.StartBlock);
        Assert.Equal(13, t.BlockCount);
        Assert.Equal(50000, t.BitCount);
    }

    [Fact]
    public void Reads_meta_key_values()
    {
        var disk = WozReader.Parse(BuildWoz2());
        Assert.Equal("Apple Panic", disk.Meta["title"]);
        Assert.Equal("English", disk.Meta["language"]);
    }

    [Fact]
    public void A_corrupted_body_fails_the_crc()
    {
        var img = BuildWoz2();
        img[^1] ^= 0xFF;                             // flip a byte in META
        var disk = WozReader.Parse(img);
        Assert.False(disk.CrcValid);
    }

    [Fact]
    public void Rejects_non_woz_and_recognises_the_signature()
    {
        Assert.False(WozReader.IsWoz(Encoding.ASCII.GetBytes("NOPE")));
        Assert.True(WozReader.IsWoz(Encoding.ASCII.GetBytes("WOZ2")));
        Assert.Throws<InvalidDataException>(() => WozReader.Parse(Encoding.ASCII.GetBytes("not a woz image")));
    }
}
