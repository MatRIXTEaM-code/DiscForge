// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.GameCube;

/// <summary>One texture inside a TPL: its dimensions, GX pixel format and decoded RGBA pixels.</summary>
public sealed record TplTexture
{
    public required int Index { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>The raw GX texture-format id.</summary>
    public required int Format { get; init; }
    /// <summary>A readable format name, e.g. "RGB5A3", "CMPR", "CI8".</summary>
    public required string FormatName { get; init; }
    /// <summary>Straight RGBA8888 pixels, row-major, width×height×4 bytes.</summary>
    public required byte[] Rgba { get; init; }
}

/// <summary>
/// Decoder for the GameCube / Wii <b>TPL</b> (Texture Palette Library) container — the format that holds a
/// disc's UI, banner and asset textures in the console's native GX pixel formats. Those formats are all
/// <i>tiled</i> (stored as small blocks, not scanlines) and several are palette-indexed or block-compressed,
/// so the bytes are meaningless to an ordinary image viewer. This unpacks the container's image table and
/// decodes every texture to straight RGBA — the full GX set: I4, I8, IA4, IA8, RGB565, RGB5A3, RGBA8, the
/// palette formats CI4 / CI8 / CI14X2, and the S3TC-style block format CMPR. Reading and decoding only.
/// </summary>
public static class Tpl
{
    /// <summary>The 32-bit magic every TPL starts with.</summary>
    public const uint Magic = 0x0020AF30;

    public static bool IsTpl(ReadOnlySpan<byte> data)
        => data.Length >= 4 && U32(data, 0) == Magic;

    /// <summary>Parse a TPL and decode every texture it contains.</summary>
    public static IReadOnlyList<TplTexture> Read(byte[] tpl)
    {
        ArgumentNullException.ThrowIfNull(tpl);
        if (tpl.Length < 12 || U32(tpl, 0) != Magic)
            throw new GameCubeFormatException("Not a TPL: missing the 0x0020AF30 magic.");

        uint count = U32(tpl, 4);
        uint tableOff = U32(tpl, 8);
        if (count == 0 || count > 4096)
            throw new GameCubeFormatException($"Implausible TPL texture count {count}.");
        if ((long)tableOff + (long)count * 8 > tpl.Length)
            throw new GameCubeFormatException("TPL image table runs past the end of the file.");

        var textures = new List<TplTexture>((int)count);
        for (int i = 0; i < count; i++)
        {
            int entry = (int)tableOff + i * 8;
            uint imgHdr = U32(tpl, entry);
            uint palHdr = U32(tpl, entry + 4);
            if (imgHdr == 0 || imgHdr + 0x0C > (uint)tpl.Length) continue;

            int height = U16(tpl, (int)imgHdr);
            int width = U16(tpl, (int)imgHdr + 2);
            int format = (int)U32(tpl, (int)imgHdr + 4);
            uint dataOff = U32(tpl, (int)imgHdr + 8);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) continue;
            if (dataOff >= (uint)tpl.Length) continue;

            (byte[] pal, _) = ReadPalette(tpl, palHdr);
            var rgba = Decode(tpl, (int)dataOff, width, height, format, pal);

            textures.Add(new TplTexture
            {
                Index = i, Width = width, Height = height,
                Format = format, FormatName = FormatName(format), Rgba = rgba,
            });
        }
        return textures;
    }

    public static string FormatName(int fmt) => fmt switch
    {
        0x0 => "I4", 0x1 => "I8", 0x2 => "IA4", 0x3 => "IA8",
        0x4 => "RGB565", 0x5 => "RGB5A3", 0x6 => "RGBA8",
        0x8 => "CI4", 0x9 => "CI8", 0xA => "CI14X2", 0xE => "CMPR",
        _ => $"0x{fmt:X}",
    };

    // ---- palette --------------------------------------------------------------

    private static (byte[] Rgba, int Format) ReadPalette(byte[] tpl, uint palHdr)
    {
        if (palHdr == 0 || palHdr + 0x0C > (uint)tpl.Length) return (Array.Empty<byte>(), -1);
        int entries = U16(tpl, (int)palHdr);
        int palFmt = (int)U32(tpl, (int)palHdr + 4);
        uint palDataOff = U32(tpl, (int)palHdr + 8);
        if (entries <= 0 || entries > 16384 || (long)palDataOff + (long)entries * 2 > tpl.Length)
            return (Array.Empty<byte>(), -1);

        var rgba = new byte[entries * 4];
        for (int i = 0; i < entries; i++)
        {
            ushort v = U16(tpl, (int)palDataOff + i * 2);
            (byte r, byte g, byte b, byte a) = palFmt switch
            {
                0 => DecodeIa8(v),
                1 => DecodeRgb565(v),
                _ => DecodeRgb5A3(v),   // 2 = RGB5A3
            };
            rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a;
        }
        return (rgba, palFmt);
    }

    // ---- decode by format -----------------------------------------------------

    private static byte[] Decode(byte[] d, int off, int w, int h, int fmt, byte[] pal)
    {
        var rgba = new byte[w * h * 4];
        switch (fmt)
        {
            case 0x0: TiledBits(d, off, w, h, 8, 8, 4, rgba, I4); break;
            case 0x1: TiledBits(d, off, w, h, 8, 4, 8, rgba, I8); break;
            case 0x2: TiledBits(d, off, w, h, 8, 4, 8, rgba, IA4); break;
            case 0x3: Tiled16(d, off, w, h, 4, 4, rgba, DecodeIa8); break;
            case 0x4: Tiled16(d, off, w, h, 4, 4, rgba, DecodeRgb565); break;
            case 0x5: Tiled16(d, off, w, h, 4, 4, rgba, DecodeRgb5A3); break;
            case 0x6: DecodeRgba8(d, off, w, h, rgba); break;
            case 0x8: DecodeCI(d, off, w, h, 8, 8, 4, pal, rgba); break;
            case 0x9: DecodeCI(d, off, w, h, 8, 4, 8, pal, rgba); break;
            case 0xA: DecodeCI(d, off, w, h, 4, 4, 14, pal, rgba); break;
            case 0xE: DecodeCmpr(d, off, w, h, rgba); break;
            default: break;   // unknown format -> transparent image
        }
        return rgba;
    }

    // Bit-packed intensity/index formats (I4=4bpp, I8/IA4=8bpp), tiled.
    private static void TiledBits(byte[] d, int off, int w, int h, int tw, int th, int bpt,
                                  byte[] rgba, Func<int, (byte, byte, byte, byte)> decode)
    {
        int cursorBits = off * 8;
        for (int ty = 0; ty < h; ty += th)
            for (int tx = 0; tx < w; tx += tw)
                for (int py = 0; py < th; py++)
                    for (int px = 0; px < tw; px++)
                    {
                        int value = ReadBits(d, ref cursorBits, bpt);
                        int x = tx + px, y = ty + py;
                        if (x >= w || y >= h) continue;
                        var (r, g, b, a) = decode(value);
                        int dst = (y * w + x) * 4;
                        rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = b; rgba[dst + 3] = a;
                    }
    }

    // 16-bit-per-texel tiled formats (IA8, RGB565, RGB5A3).
    private static void Tiled16(byte[] d, int off, int w, int h, int tw, int th,
                                byte[] rgba, Func<ushort, (byte, byte, byte, byte)> decode)
    {
        int cursor = off;
        for (int ty = 0; ty < h; ty += th)
            for (int tx = 0; tx < w; tx += tw)
                for (int py = 0; py < th; py++)
                    for (int px = 0; px < tw; px++)
                    {
                        ushort v = SafeU16(d, cursor); cursor += 2;
                        int x = tx + px, y = ty + py;
                        if (x >= w || y >= h) continue;
                        var (r, g, b, a) = decode(v);
                        int dst = (y * w + x) * 4;
                        rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = b; rgba[dst + 3] = a;
                    }
    }

    private static void DecodeRgba8(byte[] d, int off, int w, int h, byte[] rgba)
    {
        // 4×4 tiles, 64 bytes each: first 32 bytes are 16 (A,R) pairs, next 32 are 16 (G,B) pairs.
        int cursor = off;
        for (int ty = 0; ty < h; ty += 4)
            for (int tx = 0; tx < w; tx += 4)
            {
                for (int k = 0; k < 16; k++)
                {
                    int x = tx + k % 4, y = ty + k / 4;
                    byte a = SafeByte(d, cursor + k * 2), r = SafeByte(d, cursor + k * 2 + 1);
                    if (x < w && y < h) { int dst = (y * w + x) * 4; rgba[dst] = r; rgba[dst + 3] = a; }
                }
                for (int k = 0; k < 16; k++)
                {
                    int x = tx + k % 4, y = ty + k / 4;
                    byte g = SafeByte(d, cursor + 32 + k * 2), b = SafeByte(d, cursor + 32 + k * 2 + 1);
                    if (x < w && y < h) { int dst = (y * w + x) * 4; rgba[dst + 1] = g; rgba[dst + 2] = b; }
                }
                cursor += 64;
            }
    }

    private static void DecodeCI(byte[] d, int off, int w, int h, int tw, int th, int indexBits, byte[] pal, byte[] rgba)
    {
        int palEntries = pal.Length / 4;
        void Put(int x, int y, int idx)
        {
            if (x >= w || y >= h) return;
            int dst = (y * w + x) * 4;
            if (idx >= 0 && idx < palEntries)
            {
                rgba[dst] = pal[idx * 4]; rgba[dst + 1] = pal[idx * 4 + 1];
                rgba[dst + 2] = pal[idx * 4 + 2]; rgba[dst + 3] = pal[idx * 4 + 3];
            }
            else rgba[dst + 3] = 0;   // no palette / out of range -> transparent
        }

        if (indexBits == 14)
        {
            int cursor = off;
            for (int ty = 0; ty < h; ty += th)
                for (int tx = 0; tx < w; tx += tw)
                    for (int py = 0; py < th; py++)
                        for (int px = 0; px < tw; px++)
                        { int v = SafeU16(d, cursor) & 0x3FFF; cursor += 2; Put(tx + px, ty + py, v); }
            return;
        }

        int cursorBits = off * 8;
        for (int ty = 0; ty < h; ty += th)
            for (int tx = 0; tx < w; tx += tw)
                for (int py = 0; py < th; py++)
                    for (int px = 0; px < tw; px++)
                        Put(tx + px, ty + py, ReadBits(d, ref cursorBits, indexBits));
    }

    private static void DecodeCmpr(byte[] d, int off, int w, int h, byte[] rgba)
    {
        // 8×8 tiles; each holds four 4×4 DXT1-style sub-blocks in row-major 2×2 order.
        int cursor = off;
        for (int ty = 0; ty < h; ty += 8)
            for (int tx = 0; tx < w; tx += 8)
                for (int sb = 0; sb < 4; sb++)
                {
                    int bx = tx + (sb % 2) * 4, by = ty + (sb / 2) * 4;
                    DecodeCmprBlock(d, cursor, bx, by, w, h, rgba);
                    cursor += 8;
                }
    }

    private static void DecodeCmprBlock(byte[] d, int off, int bx, int by, int w, int h, byte[] rgba)
    {
        ushort c0 = SafeU16(d, off), c1 = SafeU16(d, off + 2);
        Span<(byte r, byte g, byte b, byte a)> pal = stackalloc (byte, byte, byte, byte)[4];
        var (r0, g0, b0, _) = DecodeRgb565(c0);
        var (r1, g1, b1, _) = DecodeRgb565(c1);
        pal[0] = (r0, g0, b0, 255);
        pal[1] = (r1, g1, b1, 255);
        if (c0 > c1)
        {
            pal[2] = ((byte)((2 * r0 + r1) / 3), (byte)((2 * g0 + g1) / 3), (byte)((2 * b0 + b1) / 3), 255);
            pal[3] = ((byte)((r0 + 2 * r1) / 3), (byte)((g0 + 2 * g1) / 3), (byte)((b0 + 2 * b1) / 3), 255);
        }
        else
        {
            pal[2] = ((byte)((r0 + r1) / 2), (byte)((g0 + g1) / 2), (byte)((b0 + b1) / 2), 255);
            pal[3] = (0, 0, 0, 0);   // 1-bit alpha
        }
        for (int row = 0; row < 4; row++)
        {
            byte bits = SafeByte(d, off + 4 + row);
            for (int col = 0; col < 4; col++)
            {
                int idx = (bits >> ((3 - col) * 2)) & 0x3;   // leftmost pixel in the high bits
                int x = bx + col, y = by + row;
                if (x >= w || y >= h) continue;
                var (r, g, b, a) = pal[idx];
                int dst = (y * w + x) * 4;
                rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = b; rgba[dst + 3] = a;
            }
        }
    }

    // ---- texel value decoders -------------------------------------------------

    private static (byte, byte, byte, byte) I4(int v) { byte i = (byte)((v << 4) | v); return (i, i, i, 255); }
    private static (byte, byte, byte, byte) I8(int v) { byte i = (byte)v; return (i, i, i, 255); }
    private static (byte, byte, byte, byte) IA4(int v)
    { byte a = (byte)((v & 0xF0) | (v >> 4)); byte i = (byte)((v << 4) | (v & 0x0F)); return (i, i, i, a); }

    private static (byte, byte, byte, byte) DecodeIa8(ushort v)
    { byte a = (byte)(v >> 8); byte i = (byte)(v & 0xFF); return (i, i, i, a); }

    private static (byte, byte, byte, byte) DecodeRgb565(ushort v)
    {
        int r5 = (v >> 11) & 0x1F, g6 = (v >> 5) & 0x3F, b5 = v & 0x1F;
        return ((byte)((r5 << 3) | (r5 >> 2)), (byte)((g6 << 2) | (g6 >> 4)), (byte)((b5 << 3) | (b5 >> 2)), 255);
    }

    private static (byte, byte, byte, byte) DecodeRgb5A3(ushort v)
    {
        if ((v & 0x8000) != 0)
        {
            int r5 = (v >> 10) & 0x1F, g5 = (v >> 5) & 0x1F, b5 = v & 0x1F;
            return ((byte)((r5 << 3) | (r5 >> 2)), (byte)((g5 << 3) | (g5 >> 2)), (byte)((b5 << 3) | (b5 >> 2)), 255);
        }
        int a3 = (v >> 12) & 0x7, r4 = (v >> 8) & 0xF, g4 = (v >> 4) & 0xF, b4 = v & 0xF;
        return ((byte)((r4 << 4) | r4), (byte)((g4 << 4) | g4), (byte)((b4 << 4) | b4),
                (byte)((a3 << 5) | (a3 << 2) | (a3 >> 1)));
    }

    // ---- byte helpers ---------------------------------------------------------

    private static int ReadBits(byte[] d, ref int cursorBits, int bits)
    {
        int v = 0;
        for (int i = 0; i < bits; i++)
        {
            int bytePos = cursorBits >> 3, bit = 7 - (cursorBits & 7);
            int one = bytePos < d.Length ? (d[bytePos] >> bit) & 1 : 0;
            v = (v << 1) | one;
            cursorBits++;
        }
        return v;
    }

    private static byte SafeByte(byte[] d, int i) => (uint)i < (uint)d.Length ? d[i] : (byte)0;
    private static ushort SafeU16(byte[] d, int i) => (ushort)((SafeByte(d, i) << 8) | SafeByte(d, i + 1)); // big-endian
    private static ushort U16(byte[] d, int i) => (ushort)((d[i] << 8) | d[i + 1]);
    private static uint U32(byte[] d, int i) => (uint)((d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3]);
    private static uint U32(ReadOnlySpan<byte> d, int i) => (uint)((d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3]);
}
