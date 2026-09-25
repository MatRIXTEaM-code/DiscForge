// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdg;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// CD+G decoding, rendering and extraction. Every test synthesises a known
/// packet stream by hand and asserts exact decoded output — the project
/// standard for a format decoder.
/// </summary>
public class CdgGraphicsTests
{
    // ---- packet builders -------------------------------------------------

    private static byte[] Packet(int instruction, params byte[] data16)
    {
        var p = new byte[24];
        p[0] = 0x09;                       // TV_GRAPHICS
        p[1] = (byte)(instruction & 0x3F);
        for (int i = 0; i < data16.Length && i < 16; i++)
            p[4 + i] = (byte)(data16[i] & 0x3F);
        return p;
    }

    private static byte[] NoOpPacket()
    {
        var p = new byte[24];
        p[0] = 0x00;                       // audio-only, not a graphics command
        return p;
    }

    /// <summary>Encode a 12-bit RGB value into the two 6-bit CLUT symbols.</summary>
    private static (byte hi, byte lo) EncodeColor(int rgb12)
    {
        int red = (rgb12 >> 8) & 0x0F;
        int green = (rgb12 >> 4) & 0x0F;
        int blue = rgb12 & 0x0F;
        int hi = (red << 2) | ((green >> 2) & 0x03);
        int lo = ((green & 0x03) << 4) | (blue & 0x0F);
        return ((byte)(hi & 0x3F), (byte)(lo & 0x3F));
    }

    private static byte[] LoadClutLow(params int[] rgb12s)
    {
        var d = new byte[16];
        for (int i = 0; i < 8 && i < rgb12s.Length; i++)
        {
            var (hi, lo) = EncodeColor(rgb12s[i]);
            d[i * 2] = hi;
            d[i * 2 + 1] = lo;
        }
        return Packet(30, d);
    }

    private static byte[] MemoryPreset(int colorIndex) => Packet(1, (byte)colorIndex, 0);

    private static byte[] BorderPreset(int colorIndex) => Packet(2, (byte)colorIndex);

    private static byte[] Tile(int c0, int c1, int row, int col, byte[] rows12, bool xor)
    {
        var d = new byte[16];
        d[0] = (byte)c0;
        d[1] = (byte)c1;
        d[2] = (byte)row;
        d[3] = (byte)col;
        for (int i = 0; i < 12; i++) d[4 + i] = rows12[i];
        return Packet(xor ? 38 : 6, d);
    }

    private static (byte r, byte g, byte b) PixelAt(byte[] rgba, int x, int y)
    {
        int o = (y * CdgDecoder.Width + x) * 4;
        return (rgba[o], rgba[o + 1], rgba[o + 2]);
    }

    // ---- colour table ----------------------------------------------------

    [Fact]
    public void LoadColorTableLow_DecodesTwelveBitColorsWithScaling()
    {
        var d = new CdgDecoder();
        // index0 = 0x000 black, 1 = 0xF00 red, 2 = 0x0F0 green,
        // 3 = 0x00F blue, 4 = 0xFFF white, 5 = 0x888 mid grey.
        d.ApplyPacket(LoadClutLow(0x000, 0xF00, 0x0F0, 0x00F, 0xFFF, 0x888, 0, 0));

        Assert.Equal(((byte)0, (byte)0, (byte)0), d.Clut[0]);
        Assert.Equal(((byte)255, (byte)0, (byte)0), d.Clut[1]);
        Assert.Equal(((byte)0, (byte)255, (byte)0), d.Clut[2]);
        Assert.Equal(((byte)0, (byte)0, (byte)255), d.Clut[3]);
        Assert.Equal(((byte)255, (byte)255, (byte)255), d.Clut[4]);
        // 0x8 * 17 = 136 — proves the 4-bit -> 8-bit (v*17) scaling.
        Assert.Equal(((byte)136, (byte)136, (byte)136), d.Clut[5]);
    }

    [Fact]
    public void LoadColorTableHigh_LoadsUpperEightEntries()
    {
        var d = new CdgDecoder();
        var packet = LoadClutLow(0xF00, 0, 0, 0, 0, 0, 0, 0);
        packet[1] = 31;                      // retarget as LOAD_COLOR_TABLE_HIGH
        d.ApplyPacket(packet);
        Assert.Equal(((byte)255, (byte)0, (byte)0), d.Clut[8]);
    }

    // ---- memory preset ---------------------------------------------------

    [Fact]
    public void MemoryPreset_FillsWholeScreenWithColor()
    {
        var d = new CdgDecoder();
        d.ApplyPacket(LoadClutLow(0x000, 0xF00, 0x0F0, 0x00F, 0, 0, 0, 0));
        d.ApplyPacket(MemoryPreset(1));      // fill with index 1 = red

        var rgba = d.RenderRgba();
        Assert.Equal((byte)1, d.Screen[0]);
        Assert.Equal((byte)1, d.Screen[CdgDecoder.Width * CdgDecoder.Height - 1]);
        Assert.Equal(((byte)255, (byte)0, (byte)0), PixelAt(rgba, 0, 0));
        Assert.Equal(((byte)255, (byte)0, (byte)0), PixelAt(rgba, 150, 108));
        Assert.Equal(((byte)255, (byte)0, (byte)0), PixelAt(rgba, 299, 215));
    }

    [Fact]
    public void RenderRgba_IsFullyOpaqueAndCorrectSize()
    {
        var d = new CdgDecoder();
        var rgba = d.RenderRgba();
        Assert.Equal(CdgDecoder.Width * CdgDecoder.Height * 4, rgba.Length);
        Assert.Equal((byte)0xFF, rgba[3]);
        Assert.Equal((byte)0xFF, rgba[rgba.Length - 1]);
    }

    // ---- border ----------------------------------------------------------

    [Fact]
    public void BorderPreset_StoresBorderColorIndex()
    {
        var d = new CdgDecoder();
        Assert.Equal(0, d.BorderColor);
        d.ApplyPacket(BorderPreset(7));
        Assert.Equal(7, d.BorderColor);
    }

    // ---- tile block ------------------------------------------------------

    [Fact]
    public void TileBlock_DrawsBitPatternAsColor1VsColor0()
    {
        var d = new CdgDecoder();
        d.ApplyPacket(LoadClutLow(0x000, 0xF00, 0x0F0, 0, 0, 0, 0, 0));

        // colour0 = 2 (green), colour1 = 1 (red). Row bits 0b100000 -> only the
        // left-most pixel of each row is colour1. Place at row 1, col 2:
        // top-left pixel = (col*6, row*12) = (12, 12).
        var rows = new byte[12];
        for (int i = 0; i < 12; i++) rows[i] = 0b100000;
        d.ApplyPacket(Tile(c0: 2, c1: 1, row: 1, col: 2, rows, xor: false));

        int baseX = 2 * 6, baseY = 1 * 12;
        Assert.Equal((byte)1, d.Screen[baseY * CdgDecoder.Width + baseX]);         // set bit -> c1
        Assert.Equal((byte)2, d.Screen[baseY * CdgDecoder.Width + baseX + 1]);     // clear bit -> c0
        Assert.Equal((byte)2, d.Screen[baseY * CdgDecoder.Width + baseX + 5]);     // clear bit -> c0
        // bottom row of the tile, still left-most pixel set.
        Assert.Equal((byte)1, d.Screen[(baseY + 11) * CdgDecoder.Width + baseX]);
        // outside the tile is untouched.
        Assert.Equal((byte)0, d.Screen[0]);
    }

    [Fact]
    public void TileBlock_BitOrderIsMsbFirst()
    {
        var d = new CdgDecoder();
        var rows = new byte[12];
        rows[0] = 0b000001;                 // only the right-most pixel (x=5) set
        d.ApplyPacket(Tile(c0: 0, c1: 3, row: 0, col: 0, rows, xor: false));

        Assert.Equal((byte)0, d.Screen[0]);   // x=0 clear
        Assert.Equal((byte)3, d.Screen[5]);   // x=5 set
    }

    [Fact]
    public void TileBlockXor_XorsExistingPixelIndex()
    {
        var d = new CdgDecoder();
        d.ApplyPacket(MemoryPreset(0b0011));  // whole screen = index 3

        // XOR tile: set bits use colour1 = 0b0110, clear bits use colour0 = 0.
        var rows = new byte[12];
        rows[0] = 0b100000;                   // x=0 set, others clear
        d.ApplyPacket(Tile(c0: 0, c1: 0b0110, row: 0, col: 0, rows, xor: true));

        // x=0: 3 ^ 6 = 5.  x=1: 3 ^ 0 = 3.
        Assert.Equal((byte)0b0101, d.Screen[0]);
        Assert.Equal((byte)0b0011, d.Screen[1]);
    }

    // ---- packet dispatch / no-ops ---------------------------------------

    [Fact]
    public void NonGraphicsPacket_IsIgnored()
    {
        var d = new CdgDecoder();
        d.ApplyPacket(MemoryPreset(5));
        d.ApplyPacket(NoOpPacket());          // audio-only, must not change state
        Assert.Equal((byte)5, d.Screen[0]);
        Assert.Equal(2, d.PacketsSeen);
    }

    [Fact]
    public void DefineTransparent_IsStored()
    {
        var d = new CdgDecoder();
        Assert.Equal(-1, d.TransparentColor);
        d.ApplyPacket(Packet(28, 9));
        Assert.Equal(9, d.TransparentColor);
    }

    // ---- time / index replay --------------------------------------------

    [Fact]
    public void ApplyAtTime_MapsOneSecondTo300Packets()
    {
        var packets = new List<byte[]>();
        for (int i = 0; i < 600; i++) packets.Add(NoOpPacket());
        var d = new CdgDecoder(packets);

        d.ApplyAtTime(TimeSpan.FromSeconds(1));
        Assert.Equal(300, d.AppliedPackets);

        d.ApplyAtTime(TimeSpan.FromSeconds(2));
        Assert.Equal(600, d.AppliedPackets);
    }

    [Fact]
    public void ApplyThrough_AppliesEffectUpToIndexOnly()
    {
        var packets = new List<byte[]>
        {
            MemoryPreset(1),   // index 0
            MemoryPreset(2),   // index 1
            MemoryPreset(3),   // index 2
        };
        var d = new CdgDecoder(packets);

        d.ApplyThrough(2);                    // apply packets 0 and 1 only
        Assert.Equal(2, d.AppliedPackets);
        Assert.Equal((byte)2, d.Screen[0]);   // last applied preset was index 2's packet -> colour 2

        d.ApplyThrough(3);
        Assert.Equal((byte)3, d.Screen[0]);
    }

    // ---- extractor round trip -------------------------------------------

    [Fact]
    public void Extractor_RoundTripsPacketStreamFromSidecar()
    {
        // Build a couple of known packets, then a sidecar whose 96-byte frames
        // carry them in the low 6 bits with junk in the high two bits.
        var source = new List<byte[]>
        {
            LoadClutLow(0x000, 0xF00, 0x0F0, 0x00F, 0xFFF, 0x888, 0x0FF, 0xF0F),
            MemoryPreset(4),
            BorderPreset(2),
            Tile(2, 1, 3, 4, new byte[] { 1, 2, 4, 8, 16, 32, 1, 2, 4, 8, 16, 32 }, xor: false),
        };

        var cdg = new List<byte>();
        foreach (var p in source) cdg.AddRange(p);          // 4 packets = 96 bytes = 1 frame

        // Encode into a sidecar: same low 6 bits, high bits set to noise.
        var sidecar = new byte[cdg.Count];
        for (int i = 0; i < cdg.Count; i++)
            sidecar[i] = (byte)((cdg[i] & 0x3F) | 0xC0);    // force P/Q bits on

        var recovered = CdgExtractor.Extract(sidecar);
        Assert.Equal(cdg.Count, recovered.Length);
        for (int i = 0; i < cdg.Count; i++)
            Assert.Equal((byte)(cdg[i] & 0x3F), recovered[i]);

        // And the recovered stream decodes to the same picture as the source.
        var img = CdgRenderer.RenderFinalFrame(recovered);
        Assert.Equal(((byte)255, (byte)255, (byte)255), PixelAt(img.Rgba, 150, 108)); // index 4 = white
    }

    [Fact]
    public void Extractor_IgnoresTrailingPartialFrame()
    {
        var sidecar = new byte[96 + 40];      // one full frame + junk tail
        var recovered = CdgExtractor.Extract(sidecar);
        Assert.Equal(96, recovered.Length);
    }

    // ---- malformed / truncated ------------------------------------------

    [Fact]
    public void ShortAndEmptyInput_YieldEmptyPictureNotThrow()
    {
        var img = CdgRenderer.RenderFrameAt(Array.Empty<byte>(), TimeSpan.FromSeconds(5));
        Assert.Equal(CdgDecoder.Width * CdgDecoder.Height * 4, img.Rgba.Length);

        // A 10-byte partial packet is dropped by the chunker -> still empty.
        var img2 = CdgRenderer.RenderFrameAt(new byte[10], TimeSpan.FromSeconds(1));
        Assert.Equal(CdgDecoder.Width * CdgDecoder.Height * 4, img2.Rgba.Length);
    }

    [Fact]
    public void RenderToPng_ProducesPngSignature()
    {
        var cdg = new List<byte>();
        cdg.AddRange(LoadClutLow(0x000, 0xF00, 0, 0, 0, 0, 0, 0));
        cdg.AddRange(MemoryPreset(1));
        var png = CdgRenderer.RenderToPng(cdg.ToArray(), TimeSpan.FromSeconds(10));
        Assert.Equal((byte)0x89, png[0]);
        Assert.Equal((byte)0x50, png[1]);   // 'P'
        Assert.Equal((byte)0x4E, png[2]);   // 'N'
        Assert.Equal((byte)0x47, png[3]);   // 'G'
    }
}
