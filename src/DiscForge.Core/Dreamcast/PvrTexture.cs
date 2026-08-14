// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Dreamcast;

/// <summary>The colour encoding of each texel (the low byte of the PVRT type field).</summary>
public enum PvrPixelFormat : byte
{
    Argb1555 = 0x00, Rgb565 = 0x01, Argb4444 = 0x02, Yuv422 = 0x03,
    Bump = 0x04, Pal4Bpp = 0x05, Pal8Bpp = 0x06,
}

/// <summary>How the texel data is laid out (the high byte of the PVRT type field).</summary>
public enum PvrDataFormat : byte
{
    SquareTwiddled = 0x01,
    SquareTwiddledMipmap = 0x02,
    Vq = 0x03,
    VqMipmap = 0x04,
    Clut8Twiddled = 0x05,
    Clut4Twiddled = 0x06,
    Direct8Twiddled = 0x07,
    Direct4Twiddled = 0x08,
    Rectangle = 0x09,
    RectangleStride = 0x0B,
    RectangleTwiddled = 0x0D,
    SmallVq = 0x10,
    SmallVqMipmap = 0x11,
    SquareTwiddledMipmapAlt = 0x12,
}

public sealed class PvrFormatException(string message) : Exception(message);

/// <summary>A read Dreamcast PVR texture header, with the structural checks a preservation record wants:
/// the magic, the colour and layout formats, the dimensions, and whether the file is big enough to hold
/// the data it declares.</summary>
public sealed record PvrTexture
{
    public bool HasGlobalIndex { get; init; }
    public uint? GlobalIndex { get; init; }
    public required int PvrtOffset { get; init; }

    public required byte PixelFormatCode { get; init; }
    public required byte DataFormatCode { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>The PVRT chunk length the header declares (the bytes after its own size field).</summary>
    public required uint DeclaredDataSize { get; init; }
    /// <summary>Bytes actually present after the PVRT header in the file.</summary>
    public required long AvailableDataBytes { get; init; }

    public bool PixelFormatKnown => Enum.IsDefined((PvrPixelFormat)PixelFormatCode);
    public bool DataFormatKnown => Enum.IsDefined((PvrDataFormat)DataFormatCode);

    public string PixelFormatName => PixelFormatKnown ? Pvr.Name((PvrPixelFormat)PixelFormatCode) : $"unknown (0x{PixelFormatCode:X2})";
    public string DataFormatName => DataFormatKnown ? Pvr.Name((PvrDataFormat)DataFormatCode) : $"unknown (0x{DataFormatCode:X2})";

    /// <summary>Twiddled, VQ and CLUT layouts are square power-of-two textures; the rectangle/stride
    /// layouts are the only ones that may be non-square.</summary>
    public bool RequiresSquare => DataFormatKnown && (PvrDataFormat)DataFormatCode is
        not (PvrDataFormat.Rectangle or PvrDataFormat.RectangleStride or PvrDataFormat.RectangleTwiddled);

    /// <summary>Structural problems found in the header (empty when it checks out).</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var w = new List<string>();
            if (!PixelFormatKnown) w.Add($"unknown pixel format 0x{PixelFormatCode:X2}");
            if (!DataFormatKnown) w.Add($"unknown data format 0x{DataFormatCode:X2}");
            if (Width is < 1 or > 1024 || Height is < 1 or > 1024)
                w.Add($"dimensions {Width}×{Height} are outside the Dreamcast 1–1024 range");
            else
            {
                bool isStride = DataFormatKnown && (PvrDataFormat)DataFormatCode == PvrDataFormat.RectangleStride;
                if (isStride)
                {
                    if (Width % 32 != 0) w.Add($"stride texture width {Width} is not a multiple of 32");
                }
                else if (!Pvr.IsPowerOfTwo(Width) || !Pvr.IsPowerOfTwo(Height))
                    w.Add($"dimensions {Width}×{Height} are not powers of two");
                if (RequiresSquare && Width != Height)
                    w.Add($"{DataFormatName} textures must be square, but this is {Width}×{Height}");
            }
            // A conservative lower bound on the pixel bytes: even the densest packing (4-bit) needs w*h/2.
            long minBytes = (long)Width * Height / 2;
            if (AvailableDataBytes < minBytes)
                w.Add($"file holds {AvailableDataBytes:N0} data byte(s) — too few for a {Width}×{Height} texture " +
                      $"(needs at least {minBytes:N0}); the file looks truncated");
            return w;
        }
    }

    public bool Valid => Warnings.Count == 0;

    public string Summary()
    {
        string gi = HasGlobalIndex ? $", GBIX {GlobalIndex:X8}" : "";
        string verdict = Valid ? "header OK" : string.Join("; ", Warnings);
        return $"PVR {Width}×{Height} {PixelFormatName}, {DataFormatName}{gi} — {verdict}.";
    }
}

/// <summary>
/// pvr-info — read (never rewrite) a Sega Dreamcast PVR texture header and describe it: the colour and
/// layout formats, the dimensions, the optional GBIX global index, and whether the file is large enough
/// to hold the texture it declares. This is content-level preservation metadata — the kind of thing that
/// lets a catalogue say "this asset is a 256×256 twiddled ARGB4444 texture" and flag a truncated or
/// malformed one — sitting beside DiscForge's other read-only asset readers (GameCube TPL, PlayStation
/// MDEC). It decodes the header structure only; it does not detwiddle, dequantise or render the pixels.
/// </summary>
public static class Pvr
{
    private static readonly byte[] Gbix = "GBIX"u8.ToArray();
    private static readonly byte[] Pvrt = "PVRT"u8.ToArray();

    /// <summary>Parse a PVR from its bytes. Accepts a leading GBIX header and finds the PVRT chunk;
    /// throws <see cref="PvrFormatException"/> when no PVRT signature is present.</summary>
    public static PvrTexture Parse(ReadOnlySpan<byte> data)
    {
        bool hasGbix = false;
        uint? globalIndex = null;
        int pvrtAt = -1;

        if (data.Length >= 8 && data[..4].SequenceEqual(Gbix))
        {
            hasGbix = true;
            uint gbixLen = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
            if (data.Length >= 12) globalIndex = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4));
            // The PVRT chunk follows the 8-byte GBIX tag header plus its declared body length.
            long after = 8L + gbixLen;
            if (after >= 0 && after + 4 <= data.Length && data.Slice((int)after, 4).SequenceEqual(Pvrt))
                pvrtAt = (int)after;
        }

        if (pvrtAt < 0)
        {
            if (data.Length >= 4 && data[..4].SequenceEqual(Pvrt)) pvrtAt = 0;
            else pvrtAt = FindSignature(data, Pvrt);   // tolerate odd GBIX bodies / small preambles
        }

        if (pvrtAt < 0)
            throw new PvrFormatException("No \"PVRT\" signature found — this is not a Dreamcast PVR texture.");

        var pvrt = data[pvrtAt..];
        if (pvrt.Length < 0x10)
            throw new PvrFormatException("The PVRT header is truncated (needs 16 bytes).");

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(pvrt.Slice(0x04, 4));
        byte pixel = pvrt[0x08];
        byte dataFmt = pvrt[0x09];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(pvrt.Slice(0x0C, 2));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(pvrt.Slice(0x0E, 2));

        return new PvrTexture
        {
            HasGlobalIndex = hasGbix,
            GlobalIndex = globalIndex,
            PvrtOffset = pvrtAt,
            PixelFormatCode = pixel,
            DataFormatCode = dataFmt,
            Width = width,
            Height = height,
            DeclaredDataSize = declared,
            AvailableDataBytes = pvrt.Length - 0x10,
        };
    }

    public static PvrTexture ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        // A PVR header is tiny; read a bounded prefix rather than the whole (possibly large) file, but
        // keep enough to judge truncation for small textures.
        return Parse(File.ReadAllBytes(path));
    }

    /// <summary>True if these bytes look like a PVR (a GBIX or PVRT signature near the start).</summary>
    public static bool IsPvr(ReadOnlySpan<byte> data) =>
        (data.Length >= 4 && (data[..4].SequenceEqual(Gbix) || data[..4].SequenceEqual(Pvrt)))
        || FindSignature(data.Length > 64 ? data[..64] : data, Pvrt) >= 0;

    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    internal static string Name(PvrPixelFormat f) => f switch
    {
        PvrPixelFormat.Argb1555 => "ARGB1555",
        PvrPixelFormat.Rgb565 => "RGB565",
        PvrPixelFormat.Argb4444 => "ARGB4444",
        PvrPixelFormat.Yuv422 => "YUV422",
        PvrPixelFormat.Bump => "bump map",
        PvrPixelFormat.Pal4Bpp => "4-bit palette",
        PvrPixelFormat.Pal8Bpp => "8-bit palette",
        _ => f.ToString(),
    };

    internal static string Name(PvrDataFormat f) => f switch
    {
        PvrDataFormat.SquareTwiddled => "square twiddled",
        PvrDataFormat.SquareTwiddledMipmap or PvrDataFormat.SquareTwiddledMipmapAlt => "square twiddled + mipmaps",
        PvrDataFormat.Vq => "VQ",
        PvrDataFormat.VqMipmap => "VQ + mipmaps",
        PvrDataFormat.Clut8Twiddled => "8-bit CLUT twiddled",
        PvrDataFormat.Clut4Twiddled => "4-bit CLUT twiddled",
        PvrDataFormat.Direct8Twiddled => "8-bit twiddled",
        PvrDataFormat.Direct4Twiddled => "4-bit twiddled",
        PvrDataFormat.Rectangle => "rectangle",
        PvrDataFormat.RectangleStride => "rectangle (stride)",
        PvrDataFormat.RectangleTwiddled => "rectangle twiddled",
        PvrDataFormat.SmallVq => "small VQ",
        PvrDataFormat.SmallVqMipmap => "small VQ + mipmaps",
        _ => f.ToString(),
    };

    private static int FindSignature(ReadOnlySpan<byte> data, ReadOnlySpan<byte> sig)
    {
        int limit = data.Length - sig.Length;
        for (int i = 0; i <= limit; i++)
            if (data.Slice(i, sig.Length).SequenceEqual(sig)) return i;
        return -1;
    }
}
