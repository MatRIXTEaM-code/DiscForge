// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.PlayStation;

/// <summary>The header at the start of a demuxed PlayStation MDEC frame bitstream.</summary>
public sealed record MdecFrameHeader
{
    /// <summary>Number of MDEC codes the frame decodes to (the value the console DMAs).</summary>
    public required int CodeCount { get; init; }
    /// <summary>The 0x3800 marker that confirms this is an MDEC frame.</summary>
    public required int Marker { get; init; }
    /// <summary>Quantization scale applied to the AC coefficients.</summary>
    public required int QuantScale { get; init; }
    /// <summary>Bitstream version (2 is the common STR video codec; 3 adds DC prediction).</summary>
    public required int Version { get; init; }
    /// <summary>True when the 0x3800 marker is present.</summary>
    public bool MarkerOk => Marker == 0x3800;
}

/// <summary>
/// The parts of the PlayStation <b>MDEC</b> (Macroblock Decoder) video path that decode a frame's
/// coefficients into pixels: the frame-header parse, the inverse zig-zag and PSX de-quantisation, the 8×8
/// inverse DCT, and the 4:2:0 YCbCr→RGB macroblock assembly. STR video is stored as MDEC-coded 16×16
/// macroblocks (six 8×8 blocks: Cr, Cb, then four luma) whose coefficients are run-length + variable-length
/// coded; <c>str-demux</c> already reassembles the coded bitstream, and this is the maths that turns decoded
/// coefficient blocks into an image. Clean-room, from the public MDEC description; decoding only.
/// </summary>
public static class Mdec
{
    /// <summary>The zig-zag scan order MDEC shares with JPEG/MPEG (natural index for each scan position).</summary>
    public static readonly int[] ZigZag =
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    };

    /// <summary>The standard PlayStation intra quantization table (natural order).</summary>
    public static readonly int[] QuantTable =
    {
         2, 16, 19, 22, 26, 27, 29, 34,
        16, 16, 22, 24, 27, 29, 34, 37,
        19, 22, 26, 27, 29, 34, 34, 38,
        22, 22, 26, 27, 29, 34, 37, 40,
        22, 26, 27, 29, 32, 35, 40, 48,
        26, 27, 29, 32, 35, 40, 48, 58,
        26, 27, 29, 34, 38, 46, 56, 69,
        27, 29, 35, 38, 46, 56, 69, 83,
    };

    /// <summary>Parse the 8-byte MDEC frame header from the start of a demuxed frame bitstream.</summary>
    public static MdecFrameHeader ParseFrameHeader(ReadOnlySpan<byte> bitstream)
    {
        if (bitstream.Length < 8)
            throw new ArgumentException("MDEC frame bitstream is too short to hold an 8-byte header.", nameof(bitstream));
        return new MdecFrameHeader
        {
            CodeCount = U16(bitstream, 0),
            Marker = U16(bitstream, 2),
            QuantScale = U16(bitstream, 4),
            Version = U16(bitstream, 6),
        };
    }

    /// <summary>
    /// De-quantise a block of 64 coefficients given in zig-zag scan order, returning the 64 coefficients in
    /// natural (row-major) order. The DC term uses the quant table directly; AC terms are scaled by the
    /// frame's quant scale (MDEC's <c>(coeff · q[i] · scale + 4) / 8</c>).
    /// </summary>
    public static double[] Dequantize(ReadOnlySpan<short> zigzagCoeffs, int quantScale)
    {
        if (zigzagCoeffs.Length < 64)
            throw new ArgumentException("Need 64 coefficients.", nameof(zigzagCoeffs));
        var natural = new double[64];
        for (int i = 0; i < 64; i++)
        {
            int n = ZigZag[i];
            int q = QuantTable[n];
            natural[n] = i == 0
                ? zigzagCoeffs[0] * q
                : (zigzagCoeffs[i] * q * quantScale + 4) / 8.0;
        }
        return natural;
    }

    /// <summary>In-place separable 8×8 inverse DCT over a natural-order block.</summary>
    public static void Idct8x8(double[] block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.Length != 64) throw new ArgumentException("Block must be 64 samples.", nameof(block));

        var tmp = new double[64];
        // Rows.
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                double s = 0;
                for (int u = 0; u < 8; u++)
                    s += Cu(u) * block[y * 8 + u] * CosTab[x, u];
                tmp[y * 8 + x] = s * 0.5;
            }
        // Columns.
        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
            {
                double s = 0;
                for (int v = 0; v < 8; v++)
                    s += Cu(v) * tmp[v * 8 + x] * CosTab[y, v];
                block[y * 8 + x] = s * 0.5;
            }
    }

    /// <summary>Convert one MDEC YCbCr sample to clamped 8-bit RGB (BT.601-style, the PSX convention).</summary>
    public static (byte R, byte G, byte B) YcbcrToRgb(double y, double cb, double cr)
    {
        double r = y + 1.402 * cr;
        double g = y - 0.344136 * cb - 0.714136 * cr;
        double b = y + 1.772 * cb;
        return (Clamp(r + 128), Clamp(g + 128), Clamp(b + 128));
    }

    private static byte Clamp(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);

    private static double Cu(int u) => u == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;

    // Precomputed cos((2x+1)uπ/16).
    private static readonly double[,] CosTab = BuildCos();
    private static double[,] BuildCos()
    {
        var t = new double[8, 8];
        for (int x = 0; x < 8; x++)
            for (int u = 0; u < 8; u++)
                t[x, u] = Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
        return t;
    }

    private static int U16(ReadOnlySpan<byte> b, int off) => b[off] | (b[off + 1] << 8);   // little-endian
}
