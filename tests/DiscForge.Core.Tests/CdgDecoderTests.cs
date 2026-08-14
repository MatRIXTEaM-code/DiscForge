// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The CD+G decoder, and the claim that matters: graphics authored into a
/// .sub sidecar come back out of a generated DAO image with an IDENTICAL
/// framebuffer. Symbols surviving is syntax; the same picture is semantics.
/// </summary>
public class CdgDecoderTests
{
    private static List<byte[]> AuthorTestGraphics(out CdgDecoder reference)
    {
        var packets = new List<byte[]>
        {
            CdgDecoder.LoadPaletteLow(new[]
            {
                ((byte)0, (byte)0, (byte)0), ((byte)255, (byte)255, (byte)255),
                ((byte)255, (byte)0, (byte)0), ((byte)0, (byte)255, (byte)0),
                ((byte)0, (byte)0, (byte)255), ((byte)255, (byte)255, (byte)0),
                ((byte)0, (byte)255, (byte)255), ((byte)255, (byte)0, (byte)255),
            }),
            CdgDecoder.MemoryPreset(4),
        };
        var checker = new byte[12];
        for (int i = 0; i < 12; i++) checker[i] = (byte)(i % 2 == 0 ? 0x2A : 0x15);
        var solid = new byte[12];
        Array.Fill(solid, (byte)0x3F);
        packets.Add(CdgDecoder.Tile(0, 1, 5, 10, checker));
        packets.Add(CdgDecoder.Tile(0, 2, 5, 11, solid));
        packets.Add(CdgDecoder.Tile(0, 3, 5, 10, solid, xor: true));

        reference = new CdgDecoder();
        foreach (var p in packets) reference.FeedPacket(p);
        return packets;
    }

    [Fact]
    public void Decoder_PaletteAndPresetAndTiles()
    {
        AuthorTestGraphics(out var d);

        Assert.Equal(((byte)0, (byte)0, (byte)255), d.Palette[4]);
        Assert.Equal(4, d.Screen[0]);                          // preset colour
        Assert.Equal(2, d.Screen[5 * 12 * CdgDecoder.Width + 11 * 6]);  // solid tile
        // XOR: solid colour-3 tile over the checker's colour 1 => 1^3 = 2.
        Assert.Equal(1 ^ 3, d.Screen[5 * 12 * CdgDecoder.Width + 10 * 6]);
        Assert.Equal(3, d.TileCount);
        Assert.Equal(1, d.PresetCount);
        Assert.Equal(1, d.PaletteLoads);
    }

    [Fact]
    public void Decoder_IgnoresNonGraphicsPackets()
    {
        var d = new CdgDecoder();
        var noise = new byte[24];
        noise[0] = 0x05;                                       // not command 9
        d.FeedPacket(noise);
        Assert.Equal(1, d.PacketsSeen);
        Assert.Equal(0, d.GraphicsPackets);
    }

    [Fact]
    public void EndToEnd_FramebufferSurvivesTheDiscImage()
    {
        var packets = AuthorTestGraphics(out var reference);

        // .sub sidecar: 4 packets/sector, raw interleaved with garbage P/Q
        // bits that the pipeline must strip.
        const int sectors = 20;
        var sub = new byte[sectors * 96];
        for (int i = 0; i < packets.Count; i++)
            for (int j = 0; j < 24; j++)
                sub[i * 24 + j] = (byte)(0xC0 | packets[i][j]);

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "k.bin"), new byte[sectors * 2352]);
            File.WriteAllBytes(Path.Combine(dir, "k.sub"), sub);
            File.WriteAllText(Path.Combine(dir, "k.cue"),
                "FILE \"k.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");

            using var layout = DiscLayout.FromCueFile(Path.Combine(dir, "k.cue"));
            using var img = new MemoryStream();
            RawImageGenerator.Generate(layout, RawSubcodeForm.Packed96, img);

            var fromDisc = new CdgDecoder();
            var rw = new byte[96];
            var subBytes = new byte[96];
            for (long s = 0; s < sectors; s++)
            {
                img.Position = (RawImageGenerator.LeadInSectors + 150 + s) * 2448L + 2352;
                img.ReadExactly(subBytes, 0, 96);
                SubcodeFrame.ExtractRw(subBytes, RawSubcodeForm.Packed96, rw);
                fromDisc.FeedSector(rw);
            }

            Assert.Equal(packets.Count, fromDisc.GraphicsPackets);
            Assert.Equal(reference.Screen, fromDisc.Screen);
            Assert.Equal(reference.Palette, fromDisc.Palette);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Ppm_IsWellFormed()
    {
        AuthorTestGraphics(out var d);
        var ppm = d.ToPpm(scale: 1);
        Assert.Equal("P6", System.Text.Encoding.ASCII.GetString(ppm, 0, 2));
        Assert.True(ppm.Length > CdgDecoder.Width * CdgDecoder.Height * 3);
    }
}
