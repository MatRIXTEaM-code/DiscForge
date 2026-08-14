// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.GameCube;

/// <summary>One localized comment block from a GameCube banner (opening.bnr).</summary>
public sealed record GcBannerComment(string ShortName, string ShortMaker, string LongName, string LongMaker, string Description)
{
    /// <summary>The best display title: the long name if present, else the short one.</summary>
    public string Title => LongName.Length > 0 ? LongName : ShortName;
    public string Maker => LongMaker.Length > 0 ? LongMaker : ShortMaker;
}

/// <summary>A parsed GameCube banner: the on-disc title/developer/description and the 96×32 icon.</summary>
public sealed record GcBanner
{
    /// <summary>"BNR1" (one language) or "BNR2" (six European languages).</summary>
    public required string Magic { get; init; }
    public required IReadOnlyList<GcBannerComment> Comments { get; init; }

    /// <summary>The primary (or English) comment.</summary>
    public GcBannerComment Primary => Comments[0];
    public const int ImageWidth = 96;
    public const int ImageHeight = 32;
}

/// <summary>
/// GameCube banner reader — decodes opening.bnr, the file every GameCube disc carries to name itself on
/// the console's memory-card manager: the game's display title, its developer, and a short description,
/// in one language for a BNR1 banner or six for a BNR2, plus the 96×32 icon. The icon is stored as
/// RGB5A3 texels in the console's 4×4-tile order, which this de-tiles and decodes to straight RGBA. It is
/// the human-facing identity of a disc, richer than the six-character game code. Reads the disc's own
/// unencrypted metadata; parses and reports, and changes nothing.
/// </summary>
public static class GcBannerReader
{
    private const int ImageOffset = 0x20;
    private const int ImageBytes = 0x1800;          // 96×32 RGB5A3 = 6144 bytes
    private const int CommentOffset = 0x1820;
    private const int CommentSize = 0x140;          // 320 bytes per comment

    /// <summary>True if these bytes open with a GameCube banner magic.</summary>
    public static bool IsBanner(ReadOnlySpan<byte> bnr)
        => bnr.Length >= 4 && (Ascii(bnr[..4]) is "BNR1" or "BNR2");

    /// <summary>Parse an opening.bnr.</summary>
    public static GcBanner Parse(byte[] bnr)
    {
        ArgumentNullException.ThrowIfNull(bnr);
        if (!IsBanner(bnr))
            throw new GameCubeFormatException("Not a GameCube banner (missing the BNR1/BNR2 magic).");

        string magic = Ascii(bnr.AsSpan(0, 4));
        int count = magic == "BNR2" ? 6 : 1;

        var comments = new List<GcBannerComment>(count);
        for (int i = 0; i < count; i++)
        {
            int o = CommentOffset + i * CommentSize;
            if (o + CommentSize > bnr.Length) break;
            comments.Add(new GcBannerComment(
                ShortName: Str(bnr, o + 0x00, 32),
                ShortMaker: Str(bnr, o + 0x20, 32),
                LongName: Str(bnr, o + 0x40, 64),
                LongMaker: Str(bnr, o + 0x80, 64),
                Description: Str(bnr, o + 0xC0, 128)));
        }
        if (comments.Count == 0)
            throw new GameCubeFormatException("Banner is truncated — no comment block.");

        return new GcBanner { Magic = magic, Comments = comments };
    }

    /// <summary>Decode the 96×32 banner icon to straight RGBA8888 (row-major, top-left origin).</summary>
    public static byte[] DecodeIconRgba(byte[] bnr)
    {
        ArgumentNullException.ThrowIfNull(bnr);
        if (bnr.Length < ImageOffset + ImageBytes)
            throw new GameCubeFormatException("Banner is too small to hold the 96×32 icon.");

        const int w = GcBanner.ImageWidth, h = GcBanner.ImageHeight;
        var rgba = new byte[w * h * 4];
        int blocksAcross = w / 4;   // 24

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int blockX = x / 4, blockY = y / 4, px = x % 4, py = y % 4;
                int blockIndex = blockY * blocksAcross + blockX;
                int src = ImageOffset + (blockIndex * 16 + py * 4 + px) * 2;
                ushort v = (ushort)((bnr[src] << 8) | bnr[src + 1]);   // big-endian texel
                (byte r, byte g, byte b, byte a) = DecodeRgb5A3(v);
                int dst = (y * w + x) * 4;
                rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = b; rgba[dst + 3] = a;
            }
        return rgba;
    }

    public static string Render(GcBanner banner)
    {
        ArgumentNullException.ThrowIfNull(banner);
        var c = banner.Primary;
        var sb = new StringBuilder();
        sb.AppendLine($"Banner ({banner.Magic}): {c.Title}");
        if (c.Maker.Length > 0) sb.AppendLine($"  Developer:   {c.Maker}");
        if (c.Description.Length > 0) sb.AppendLine($"  Description: {c.Description}");
        return sb.ToString().TrimEnd();
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>RGB5A3: top bit set → opaque RGB555; clear → ARGB3444.</summary>
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

    private static string Ascii(ReadOnlySpan<byte> b) => Encoding.ASCII.GetString(b);

    private static string Str(byte[] b, int o, int len)
    {
        int end = 0;
        for (int i = 0; i < len && o + i < b.Length; i++) { if (b[o + i] == 0) break; end = i + 1; }
        return Encoding.Latin1.GetString(b, o, end).Trim();
    }
}

/// <summary>Decodes a GameCube region from its game code.</summary>
public static class GameCubeRegion
{
    /// <summary>The region a game code declares (its fourth character), e.g. "GALE" → USA.</summary>
    public static string Decode(string gameCode)
    {
        if (string.IsNullOrEmpty(gameCode) || gameCode.Length < 4) return "unknown";
        return gameCode[3] switch
        {
            'E' => "USA (NTSC-U)",
            'J' => "Japan (NTSC-J)",
            'P' => "Europe (PAL)",
            'U' => "Australia (PAL)",
            'D' => "Germany (PAL)",
            'F' => "France (PAL)",
            'I' => "Italy (PAL)",
            'S' => "Spain (PAL)",
            'H' => "Netherlands (PAL)",
            'K' => "Korea (NTSC)",
            'X' or 'Y' => "Europe (PAL, region-free/alt)",
            _ => $"region '{gameCode[3]}'",
        };
    }
}
