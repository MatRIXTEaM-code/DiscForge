// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The CHD v5 hard-disk writer, verified by round-tripping through DiscForge's own
/// reader: an image encoded to a CHD and read back must be byte-identical, and the
/// CHD's stored SHA-1 (checked by the reader) must match. Content mixes compressible
/// (zlib), incompressible (NONE) and duplicate (SELF) hunks. The output is also
/// accepted by chdman out of band (chdman verify passes both SHA-1s).
/// </summary>
public class ChdWriterTests
{
    private static byte[] MixedImage()
    {
        // 8 hunks of 4096: ramps (zlib), random (NONE), a duplicate (SELF), zeros.
        var img = new byte[8 * 4096];
        var rng = new Random(4242);
        for (int h = 0; h < 8; h++)
        {
            var span = img.AsSpan(h * 4096, 4096);
            if (h == 2) rng.NextBytes(span);                       // incompressible -> NONE
            else if (h == 5) img.AsSpan(0, 4096).CopyTo(span);     // duplicate of hunk 0 -> SELF
            else if (h == 6) span.Clear();                          // zeros
            else for (int i = 0; i < 4096; i++) span[i] = (byte)((i + h * 7) % 200);  // ramp -> zlib
        }
        return img;
    }

    [Fact]
    public void A_written_hard_disk_chd_reads_back_identically()
    {
        var img = MixedImage();
        byte[] chd = ChdWriter.CreateHd(img);
        byte[] back = ChdHdExtractor.Extract(chd);   // throws if the stored SHA-1 mismatches
        Assert.Equal(img, back);
    }

    [Fact]
    public void A_written_chd_has_a_valid_self_checking_map()
    {
        var chd = ChdWriter.CreateHd(MixedImage());
        long mapOffset = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(chd.AsSpan(0x28));
        // Decode throws if the map fails its own CRC-16.
        var map = ChdMap.Decode(chd, mapOffset, 8, 4096, 512);
        Assert.Equal(8, map.Length);
        Assert.Contains(map, e => e.Type == ChdHunkType.Self);
        Assert.Contains(map, e => e.Type == ChdHunkType.None);
    }

    [Fact]
    public void An_empty_and_a_tiny_image_round_trip()
    {
        foreach (int size in new[] { 0, 1, 100, 4096, 4097 })
        {
            var img = new byte[size];
            new Random(size).NextBytes(img);
            var back = ChdHdExtractor.Extract(ChdWriter.CreateHd(img));
            Assert.Equal(img, back);
        }
    }

    [Fact]
    public void A_written_cd_chd_reads_back_to_the_same_bin()
    {
        // Two tracks: a MODE1 data track and an audio track (exercises the byte-swap).
        var rng = new Random(77);
        var data = new byte[2352 * 60]; for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 3) % 251);
        var audio = new byte[2352 * 40]; rng.NextBytes(audio);
        var chd = ChdWriter.CreateCd(new[]
        {
            new ChdWriter.CdTrackInput(1, "MODE1_RAW", "NONE", data, 60, 0),
            new ChdWriter.CdTrackInput(2, "AUDIO", "RW", audio, 40, 0),
        });
        var r = ChdExtractor.ExtractCd(chd);
        Assert.True(r.Verified);
        Assert.Equal(2, r.Tracks);
        // The extracted bin is track1 then track2, each at its little-endian bytes.
        var expect = new byte[data.Length + audio.Length];
        data.CopyTo(expect, 0); audio.CopyTo(expect, data.Length);
        Assert.Equal(expect, r.Bin);
    }

    [Fact]
    public void A_written_cd_chd_from_a_bincue_round_trips()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_chdwrite_").FullName;
        try
        {
            var bin = new byte[2352 * 50];
            for (int i = 0; i < bin.Length; i++) bin[i] = (byte)((i + 7) % 240);
            File.WriteAllBytes(Path.Combine(dir, "disc.bin"), bin);
            File.WriteAllText(Path.Combine(dir, "disc.cue"),
                "FILE \"disc.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");

            var chd = ChdWriter.CreateCdFromBinCue(File.ReadAllText(Path.Combine(dir, "disc.cue")), dir);
            var r = ChdExtractor.ExtractCd(chd);
            Assert.True(r.Verified);
            Assert.Equal(bin, r.Bin);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
