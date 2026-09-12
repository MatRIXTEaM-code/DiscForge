// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.Saves;

/// <summary>Pixel format of a decoded save banner/icon image (as opposed to the disc-level opening.bnr,
/// which is always RGB5A3).</summary>
public enum GcSaveImageFormat { None, Rgb5A3, Ci8 }

/// <summary>A single decoded save banner or icon image, straight RGBA8888 (row-major, top-left origin).</summary>
public sealed record GcSaveImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required GcSaveImageFormat Format { get; init; }
    public required byte[] Rgba { get; init; }
}

/// <summary>
/// Decodes the banner and icon embedded in a GameCube save's OWN data — distinct from the disc's
/// opening.bnr (see GcBannerReader): this is the small image the console's memory-card manager shows
/// next to one save file.
///
/// Clean-room, from the public GameCube memory-card directory-entry description: byte 0x07 bit 0 of the
/// directory entry selects the BANNER's pixel format (0 = CI8, 1 = RGB5A3); the banner is always 96×32.
/// A 16-bit field at 0x30 packs the ICON's format 2 bits per frame (up to 8 frames): 00 = no icon,
/// 01 = CI8 with one palette shared after the last frame, 10 = RGB5A3 (no palette), 11 = CI8 with its
/// own palette immediately after each frame. Icons are 32×32. Both images live in the save's own payload
/// (the bytes after the .gci/card header), starting at the u32 "banner offset" field at 0x2C.
///
/// Multi-frame icon ANIMATION is intentionally NOT decoded here: this project could not find a public,
/// non-confidential source pinning down the frame count and the exact per-frame layout for the two CI8
/// animated sub-formats with enough confidence to implement — see the DTK/ADP disc-audio decline in
/// NEXT.md for the same "found only in a leaked Nintendo document" situation, which this project declines
/// to use as a clean-room basis. Only the FIRST icon frame is decoded here, a case that is provably
/// correct regardless of that ambiguity: with exactly one frame there is exactly one palette either way,
/// immediately following that frame's pixel data, so the shared-vs-unique distinction never applies.
///
/// Both formats reuse the tiling this project already decodes elsewhere: RGB5A3 in 4×4 texel tiles (as
/// in the opening.bnr banner decoder), CI8 in 8×4 index tiles against a 256-entry RGB5A3 palette (as in
/// the TPL texture decoder). Reads only; writes nothing back to the save.
/// </summary>
public static class GcSaveBannerReader
{
    public const int BannerWidth = 96, BannerHeight = 32;
    public const int IconWidth = 32, IconHeight = 32;
    private const int PaletteEntries = 256;
    private const int PaletteBytes = PaletteEntries * 2;

    /// <summary>The banner's pixel format, from bit 0 of the directory entry's flags byte (0x07).</summary>
    public static GcSaveImageFormat BannerFormat(GcSave save)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (save.Entry.Length <= 0x07) return GcSaveImageFormat.None;
        return (save.Entry[0x07] & 0x01) != 0 ? GcSaveImageFormat.Rgb5A3 : GcSaveImageFormat.Ci8;
    }

    /// <summary>The first icon frame's format, from the low 2 bits of the u16 field at 0x30.</summary>
    public static GcSaveImageFormat IconFormat(GcSave save)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (save.Entry.Length < 0x32) return GcSaveImageFormat.None;
        int code = BinaryPrimitives.ReadUInt16BigEndian(save.Entry.AsSpan(0x30)) & 0x3;
        return code switch
        {
            0 => GcSaveImageFormat.None,
            2 => GcSaveImageFormat.Rgb5A3,
            _ => GcSaveImageFormat.Ci8,   // 1 (shared palette) and 3 (unique palette) decode identically for frame 0
        };
    }

    private static long BannerOffset(GcSave save) =>
        save.Entry.Length >= 0x30 ? BinaryPrimitives.ReadUInt32BigEndian(save.Entry.AsSpan(0x2C)) : -1;

    /// <summary>Decode the save's 96×32 banner from its own payload (the bytes after the .gci/card header),
    /// or null if this save declares no banner image or the offset doesn't fit the payload.</summary>
    public static GcSaveImage? DecodeBanner(GcSave save, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        long off = BannerOffset(save);
        if (off < 0) return null;
        return BannerFormat(save) switch
        {
            GcSaveImageFormat.Rgb5A3 => DecodeRgb5A3Tiled(payload, off, BannerWidth, BannerHeight),
            GcSaveImageFormat.Ci8 => DecodeCi8Tiled(payload, off, BannerWidth, BannerHeight),
            _ => null,
        };
    }

    /// <summary>Decode the save's 32×32 FIRST icon frame, or null if this save has no icon (or the data
    /// doesn't fit the payload).</summary>
    public static GcSaveImage? DecodeIcon(GcSave save, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var iconFmt = IconFormat(save);
        if (iconFmt == GcSaveImageFormat.None) return null;

        long bannerOff = BannerOffset(save);
        if (bannerOff < 0) return null;
        long bannerSize = BannerFormat(save) switch
        {
            GcSaveImageFormat.Rgb5A3 => (long)BannerWidth * BannerHeight * 2,
            GcSaveImageFormat.Ci8 => (long)BannerWidth * BannerHeight + PaletteBytes,
            _ => 0,
        };
        long iconOff = bannerOff + bannerSize;

        return iconFmt switch
        {
            GcSaveImageFormat.Rgb5A3 => DecodeRgb5A3Tiled(payload, iconOff, IconWidth, IconHeight),
            GcSaveImageFormat.Ci8 => DecodeCi8Tiled(payload, iconOff, IconWidth, IconHeight),
            _ => null,
        };
    }

    // ---- tiled decode ---------------------------------------------------

    private static GcSaveImage? DecodeRgb5A3Tiled(byte[] data, long off, int w, int h)
    {
        long need = off + (long)w * h * 2;
        if (off < 0 || need > data.Length) return null;

        var rgba = new byte[w * h * 4];
        int blocksAcross = w / 4;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int blockX = x / 4, blockY = y / 4, px = x % 4, py = y % 4;
                int blockIndex = blockY * blocksAcross + blockX;
                long src = off + (blockIndex * 16 + py * 4 + px) * 2;
                ushort v = (ushort)((data[src] << 8) | data[src + 1]);
                var (r, g, b, a) = DecodeRgb5A3(v);
                int dst = (y * w + x) * 4;
                rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = b; rgba[dst + 3] = a;
            }
        return new GcSaveImage { Width = w, Height = h, Format = GcSaveImageFormat.Rgb5A3, Rgba = rgba };
    }

    private static GcSaveImage? DecodeCi8Tiled(byte[] data, long off, int w, int h)
    {
        long pixelBytes = (long)w * h;
        long need = off + pixelBytes + PaletteBytes;
        if (off < 0 || need > data.Length) return null;

        // Palette immediately follows the pixel data: 256 × RGB5A3 entries, big-endian.
        long palOff = off + pixelBytes;
        var palette = new (byte r, byte g, byte b, byte a)[PaletteEntries];
        for (int i = 0; i < PaletteEntries; i++)
        {
            ushort v = (ushort)((data[palOff + i * 2] << 8) | data[palOff + i * 2 + 1]);
            palette[i] = DecodeRgb5A3(v);
        }

        var rgba = new byte[w * h * 4];
        const int tw = 8, th = 4;
        int blocksAcross = w / tw;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int blockX = x / tw, blockY = y / th, px = x % tw, py = y % th;
                int blockIndex = blockY * blocksAcross + blockX;
                long src = off + blockIndex * (tw * th) + py * tw + px;
                byte idx = data[src];
                var (r, g, b, a) = palette[idx];
                int dst = (y * w + x) * 4;
                rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = b; rgba[dst + 3] = a;
            }
        return new GcSaveImage { Width = w, Height = h, Format = GcSaveImageFormat.Ci8, Rgba = rgba };
    }

    /// <summary>RGB5A3: top bit set → opaque RGB555; clear → ARGB3444. (A small, self-contained duplicate
    /// of the same six lines of bit math that live in the opening.bnr and TPL decoders — not worth a
    /// cross-namespace dependency for.)</summary>
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
}
