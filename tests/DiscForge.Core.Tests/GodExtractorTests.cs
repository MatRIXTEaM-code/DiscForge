// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Xbox;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The GOD → ISO extractor's SELF-VALIDATING resolution of the block→offset ambiguity. A synthetic GOD
/// package is built by interleaving hash blocks per one convention around a real XDVDFS image; the
/// extractor must reconstruct it (the convention that yields a valid XDVDFS descriptor wins) and must
/// DECLINE — never emit a shifted, corrupt ISO — when the payload doesn't validate under either
/// convention. The synthetic writer proves the walk mechanics; a real GOD fixture pins the formula.
/// </summary>
public class GodExtractorTests
{
    private const int BlockSize = 0x1000;
    private const int GroupBlocks = 0xAA;

    // Convention-0 physical block layout (mirrors GodExtractor.PhysicalBlock convention 0).
    private static long Phys0(long db)
    {
        long L1 = (long)GroupBlocks * GroupBlocks, L2 = L1 * GroupBlocks;
        return db + (db / GroupBlocks + 1) + (db / L1 + 1) + (db / L2 + 1);
    }

    private static string BuildGod(byte[] iso, string dir)
    {
        // Pad the ISO to a whole number of 0x1000 blocks.
        long dataBytes = (iso.Length + BlockSize - 1) / BlockSize * BlockSize;
        var isoPadded = new byte[dataBytes];
        iso.CopyTo(isoPadded, 0);
        long dataBlocks = dataBytes / BlockSize;

        long maxPhys = 0;
        for (long db = 0; db < dataBlocks; db++) maxPhys = Math.Max(maxPhys, Phys0(db));
        var payload = new byte[(maxPhys + 1) * BlockSize];
        for (long db = 0; db < dataBlocks; db++)
            Array.Copy(isoPadded, db * BlockSize, payload, Phys0(db) * BlockSize, BlockSize);

        string header = Path.Combine(dir, "god");
        Directory.CreateDirectory(header + ".data");
        File.WriteAllBytes(Path.Combine(header + ".data", "Data0000"), payload);

        var hdr = new byte[0x360];
        Encoding.ASCII.GetBytes("CON ").CopyTo(hdr, 0);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(0x344), GodInfo.GamesOnDemandType);
        BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(0x34C), (ulong)dataBytes);
        File.WriteAllBytes(header, hdr);
        return header;
    }

    [Fact]
    public void Reconstructs_a_synthetic_god_and_validates_the_convention()
    {
        var iso = XdvdfsBuilder.Build(new[] { XdvdfsBuilder.Node.File("default.xbe", Encoding.ASCII.GetBytes("boot")) });
        string dir = Path.Combine(Path.GetTempPath(), "dforge_god_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string header = BuildGod(iso, dir);

            using var outMs = new MemoryStream();
            var result = GodExtractor.Extract(header, outMs);

            Assert.True(result.Succeeded);
            Assert.Equal(0, result.Convention);                 // built per convention 0
            // The reconstructed image is a valid XDVDFS volume carrying the file.
            using var read = new MemoryStream(outMs.ToArray());
            Assert.True(XdvdfsReader.IsXdvdfs(read));
            read.Position = 0;
            var vol = XdvdfsReader.Read(read);
            Assert.Contains(vol.Files, f => f.Path == "/default.xbe");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Declines_when_the_payload_is_not_a_valid_xdvdfs_image()
    {
        // A payload of random blocks validates under neither convention → the extractor must decline,
        // not emit a shifted, corrupt ISO.
        string dir = Path.Combine(Path.GetTempPath(), "dforge_god_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string header = Path.Combine(dir, "god");
            Directory.CreateDirectory(header + ".data");
            var junk = new byte[64 * BlockSize];
            new Random(5).NextBytes(junk);
            File.WriteAllBytes(Path.Combine(header + ".data", "Data0000"), junk);

            var hdr = new byte[0x360];
            Encoding.ASCII.GetBytes("CON ").CopyTo(hdr, 0);
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(0x344), GodInfo.GamesOnDemandType);
            BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(0x34C), (ulong)(32 * BlockSize));
            File.WriteAllBytes(header, hdr);

            using var outMs = new MemoryStream();
            var result = GodExtractor.Extract(header, outMs);

            Assert.False(result.Succeeded);
            Assert.Equal(-1, result.Convention);
            Assert.Equal(0, outMs.Length);                      // nothing written on a decline
            Assert.Contains("declined", result.Detail);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
