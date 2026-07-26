// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using DiscForge.Core.Media;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the TIM texture reader. TIM has no external oracle here, so images
/// are built byte-by-byte to the public layout and decoded back: the checks pin
/// the pixel-mode → width mapping, the BGR555 → RGBA colour maths (red lives in
/// the LOW five bits, which is the easy thing to get backwards), the 4bpp nibble
/// order, and the CLUT lookup.
/// </summary>
public class TimTests
{
    private static void WriteU32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
    private static void WriteU16(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
    private static ushort Bgr555(int r, int g, int b) => (ushort)((r & 0x1F) | ((g & 0x1F) << 5) | ((b & 0x1F) << 10));

    [Fact]
    public void A_16bpp_image_decodes_its_pixels_and_colours()
    {
        // 2x1 direct-colour image: pure red then pure blue.
        var img = new byte[8 + 12 + 2 * 2];
        WriteU32(img, 0, 0x00000010);
        WriteU32(img, 4, 0x02);                 // pmode 2 (16bpp), no CLUT
        int block = 8;
        WriteU32(img, block, (uint)(12 + 4));   // block byte length
        WriteU16(img, block + 8, 2);            // width = 2 words = 2 pixels
        WriteU16(img, block + 10, 1);           // height
        WriteU16(img, block + 12, Bgr555(31, 0, 0));   // red
        WriteU16(img, block + 14, Bgr555(0, 0, 31));   // blue

        var tim = Tim.Parse(img);
        Assert.Equal(Tim.Bpp.Bpp16, tim.Mode);
        Assert.Equal(2, tim.Width);
        Assert.Equal(1, tim.Height);

        var rgba = Tim.ToRgba(tim);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[..4]);       // red, opaque
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba[4..8]);      // blue, opaque
    }

    [Fact]
    public void A_zero_colour_is_transparent()
    {
        var img = new byte[8 + 12 + 2];
        WriteU32(img, 0, 0x00000010);
        WriteU32(img, 4, 0x02);
        WriteU32(img, 8, 12 + 2);
        WriteU16(img, 8 + 8, 1);
        WriteU16(img, 8 + 10, 1);
        WriteU16(img, 8 + 12, 0x0000);          // transparent

        var rgba = Tim.ToRgba(Tim.Parse(img));
        Assert.Equal(0, rgba[3]);               // alpha 0
    }

    [Fact]
    public void A_4bpp_image_uses_the_clut_and_low_nibble_first()
    {
        // 4bpp, one CLUT of 16 entries; one data byte = two pixels (index 1, index 2).
        var img = new byte[8 + (12 + 32) + (12 + 2)];
        WriteU32(img, 0, 0x00000010);
        WriteU32(img, 4, 0x08 | 0x00);          // pmode 0 (4bpp) + CLUT flag

        int clut = 8;
        WriteU32(img, clut, 12 + 32);           // CLUT block length (16 entries)
        WriteU16(img, clut + 8, 16);            // entries per CLUT
        WriteU16(img, clut + 10, 1);            // one CLUT
        WriteU16(img, clut + 12 + 1 * 2, Bgr555(31, 0, 0));   // index 1 = red
        WriteU16(img, clut + 12 + 2 * 2, Bgr555(0, 31, 0));   // index 2 = green

        int image = clut + 12 + 32;
        WriteU32(img, image, 12 + 2);
        WriteU16(img, image + 8, 1);            // width = 1 word = 4 pixels
        WriteU16(img, image + 10, 1);
        img[image + 12] = 0x21;                 // low nibble 1, high nibble 2

        var tim = Tim.Parse(img);
        Assert.Equal(Tim.Bpp.Bpp4, tim.Mode);
        Assert.Equal(4, tim.Width);             // one word => 4 pixels
        Assert.Equal(1, tim.PaletteCount);

        var rgba = Tim.ToRgba(tim);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[..4]);       // pixel 0 = index 1 = red
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, rgba[4..8]);      // pixel 1 = index 2 = green
    }

    [Fact]
    public void A_non_tim_is_refused()
    {
        Assert.False(Tim.IsTim(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Throws<TimFormatException>(() => Tim.Parse(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public void The_png_export_has_a_valid_signature_and_dimensions()
    {
        var img = new byte[8 + 12 + 2 * 2];
        WriteU32(img, 0, 0x00000010);
        WriteU32(img, 4, 0x02);
        WriteU32(img, 8, 12 + 4);
        WriteU16(img, 8 + 8, 2);
        WriteU16(img, 8 + 10, 1);
        WriteU16(img, 8 + 12, Bgr555(31, 31, 31));
        WriteU16(img, 8 + 14, Bgr555(0, 0, 0));

        var png = Tim.ToPng(Tim.Parse(img));

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        // IHDR width/height are the first two big-endian u32s of the IHDR data at 0x10.
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(0x10, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(0x14, 4)));
    }
}
