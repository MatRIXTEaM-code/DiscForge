// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Util;

namespace DiscForge.Core.Media;

public sealed class TimFormatException(string message) : Exception(message);

/// <summary>
/// Reads the PlayStation TIM texture format — the images "TIM RIP" / "Tim File
/// Ripper" pull out of a game's data — and decodes them to RGBA so DiscForge can
/// export a PNG. TIM is a plain, unencrypted image container (a pixel-mode flag,
/// an optional colour lookup table, and a pixel block); nothing here decrypts or
/// defeats anything.
///
/// Clean-room, from the public TIM description:
///
///   0x00  4  0x00000010 (id 0x10, version 0)
///   0x04  4  flags: bits0-2 pmode (0=4bpp,1=8bpp,2=16bpp,3=24bpp), bit3 = has CLUT
///   [if CLUT] block: u32 byteLength, u16 x, u16 y, u16 w(entries), u16 h(count),
///                    then h*w little-endian 16-bit BGR555+STP entries
///   image block:     u32 byteLength, u16 x, u16 y, u16 w, u16 h, then pixel data
///
///   A 16-bit colour is STP(bit15) B(14-10) G(9-5) R(4-0). By PlayStation
///   convention a fully-zero entry (0x0000) is transparent; everything else is
///   opaque here (semi-transparency/STP is preserved on read but flattened to
///   opaque on RGBA export).
/// </summary>
public static class Tim
{
    public enum Bpp { Bpp4, Bpp8, Bpp16, Bpp24 }

    public sealed record TimImage
    {
        public required Bpp Mode { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        /// <summary>Palettes (CLUTs): each is an array of 16-bit BGR555+STP entries.
        /// Empty for the direct-colour 16/24bpp modes.</summary>
        public required IReadOnlyList<ushort[]> Cluts { get; init; }
        /// <summary>Raw pixel payload as stored (indices for 4/8bpp, 16-bit or
        /// packed 24-bit colour for the direct modes).</summary>
        public required byte[] Pixels { get; init; }

        public int PaletteCount => Cluts.Count;
    }

    public static bool IsTim(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 0x00000010;

    public static TimImage Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsTim(data))
            throw new TimFormatException("Missing the 0x00000010 TIM id — not a TIM image.");

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        int pmode = (int)(flags & 0x7);
        bool hasClut = (flags & 0x8) != 0;
        Bpp mode = pmode switch
        {
            0 => Bpp.Bpp4,
            1 => Bpp.Bpp8,
            2 => Bpp.Bpp16,
            3 => Bpp.Bpp24,
            _ => throw new TimFormatException($"Unsupported TIM pixel mode {pmode}."),
        };

        int p = 8;
        var cluts = new List<ushort[]>();
        if (hasClut)
        {
            if (p + 12 > data.Length) throw new TimFormatException("Truncated CLUT header.");
            uint clutBytes = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
            int clutW = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 8));
            int clutH = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 10));
            int entriesStart = p + 12;
            int entryCount = clutW * clutH;
            if (entriesStart + entryCount * 2 > data.Length)
                throw new TimFormatException("CLUT extends past the end of the file.");

            for (int c = 0; c < clutH; c++)
            {
                var pal = new ushort[clutW];
                for (int i = 0; i < clutW; i++)
                    pal[i] = BinaryPrimitives.ReadUInt16LittleEndian(
                        data.AsSpan(entriesStart + (c * clutW + i) * 2));
                cluts.Add(pal);
            }
            p += (int)clutBytes;
        }

        if (p + 12 > data.Length) throw new TimFormatException("Truncated image header.");
        uint imgBytes = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
        int imgW = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 8));  // width in 16-bit words
        int imgH = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 10));
        int pixelStart = p + 12;
        int pixelLen = (int)imgBytes - 12;
        if (pixelStart + pixelLen > data.Length || pixelLen < 0)
            throw new TimFormatException("Image data extends past the end of the file.");

        // Convert the "width in 16-bit words" to actual pixel width per mode.
        int width = mode switch
        {
            Bpp.Bpp4 => imgW * 4,
            Bpp.Bpp8 => imgW * 2,
            Bpp.Bpp16 => imgW,
            Bpp.Bpp24 => imgW * 2 / 3,
            _ => imgW,
        };

        var pixels = new byte[pixelLen];
        Array.Copy(data, pixelStart, pixels, 0, pixelLen);

        return new TimImage
        {
            Mode = mode,
            Width = width,
            Height = imgH,
            Cluts = cluts,
            Pixels = pixels,
        };
    }

    /// <summary>Decode to top-left-origin RGBA (4 bytes/pixel). For paletted modes
    /// <paramref name="palette"/> selects which CLUT to use.</summary>
    public static byte[] ToRgba(TimImage img, int palette = 0)
    {
        ArgumentNullException.ThrowIfNull(img);
        int w = img.Width, h = img.Height;
        var px = img.Pixels;
        var rgba = new byte[w * h * 4];

        ushort[] pal = img.Cluts.Count > 0
            ? img.Cluts[Math.Clamp(palette, 0, img.Cluts.Count - 1)]
            : Array.Empty<ushort>();

        // Bytes per stored row, from the pixel mode.
        int strideBytes = img.Mode switch
        {
            Bpp.Bpp4 => w / 2,
            Bpp.Bpp8 => w,
            Bpp.Bpp16 => w * 2,
            Bpp.Bpp24 => w * 3,
            _ => w * 2,
        };

        for (int y = 0; y < h; y++)
        {
            int rowStart = y * strideBytes;
            for (int x = 0; x < w; x++)
            {
                int dst = (y * w + x) * 4;
                if (img.Mode == Bpp.Bpp24)
                {
                    int at = rowStart + x * 3;
                    rgba[dst] = At(px, at);
                    rgba[dst + 1] = At(px, at + 1);
                    rgba[dst + 2] = At(px, at + 2);
                    rgba[dst + 3] = 255;
                    continue;
                }

                ushort colour = img.Mode switch
                {
                    Bpp.Bpp4 => LookUp(pal, Nibble(px, rowStart, x)),
                    Bpp.Bpp8 => LookUp(pal, At(px, rowStart + x)),
                    _ => Read16(px, rowStart + x * 2),   // Bpp16
                };
                WriteBgr555(rgba, dst, colour);
            }
        }
        return rgba;
    }

    /// <summary>Decode and encode straight to a PNG file image.</summary>
    public static byte[] ToPng(TimImage img, int palette = 0) =>
        PngWriter.EncodeRgba(ToRgba(img, palette), img.Width, img.Height);

    // ---- pixel helpers ------------------------------------------------------

    private static byte At(byte[] px, int index) => index >= 0 && index < px.Length ? px[index] : (byte)0;

    // 4bpp: two indices per byte, low nibble is the left (even) pixel.
    private static int Nibble(byte[] px, int rowStart, int x)
    {
        byte b = At(px, rowStart + x / 2);
        return (x & 1) == 0 ? b & 0x0F : (b >> 4) & 0x0F;
    }

    private static ushort Read16(byte[] px, int at) =>
        (ushort)(At(px, at) | (At(px, at + 1) << 8));

    private static ushort LookUp(ushort[] pal, int index) =>
        index >= 0 && index < pal.Length ? pal[index] : (ushort)0;

    // A 16-bit BGR555+STP colour to RGBA; a fully-zero entry is transparent.
    private static void WriteBgr555(byte[] rgba, int at, ushort colour)
    {
        int r5 = colour & 0x1F, g5 = (colour >> 5) & 0x1F, b5 = (colour >> 10) & 0x1F;
        rgba[at] = (byte)((r5 << 3) | (r5 >> 2));
        rgba[at + 1] = (byte)((g5 << 3) | (g5 >> 2));
        rgba[at + 2] = (byte)((b5 << 3) | (b5 >> 2));
        rgba[at + 3] = (byte)(colour == 0 ? 0 : 255);
    }
}
