// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.PlayStation;

/// <summary>A decoded MDEC frame as an RGBA raster (row-major, top-left origin).</summary>
public sealed record MdecImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>width·height·4 bytes, R,G,B,A per pixel (A always 255).</summary>
    public required byte[] Rgba { get; init; }
}

/// <summary>
/// Decodes a PlayStation STR <b>version 2</b> video frame bitstream to pixels — the
/// front half of the MDEC path that <see cref="Mdec"/> does not cover: the 16-bit
/// little-endian, MSB-first bit reader, the DC value, and the AC run/level
/// variable-length codes (the standard MPEG-1 intra table with a trailing sign bit,
/// plus the <c>000001</c> escape and the <c>10</c> end-of-block code). It feeds the
/// decoded coefficient blocks through <see cref="Mdec.Dequantize"/>,
/// <see cref="Mdec.Idct8x8"/> and <see cref="Mdec.YcbcrToRgb"/> and assembles the
/// 16×16 macroblocks (Cr, Cb, Y1..Y4; 4:2:0) in the console's column-major order.
///
/// Clean-room, reimplemented from the public STR/MDEC format description and the
/// MPEG-1 Table B-14 VLC (an ISO standard). Version 3 (differential DC with separate
/// luma/chroma DC-size tables) is reported, not mis-decoded: its tables cannot be verified
/// in-tree (a v3 frame carries no self-check and there is no reference oracle here), so a
/// guessed table is deliberately not shipped — the v3 path unblocks the moment a real v3
/// .str fixture is available (docs/FIXTURES.md), which is the seam to slot validated tables in.
/// </summary>
public static class MdecFrameDecoder
{
    /// <summary>Thrown when a frame can't be decoded (bad marker, unsupported version, bad bitstream).</summary>
    public sealed class MdecDecodeException(string message) : Exception(message);

    /// <summary>
    /// Decode a demuxed STR frame bitstream (including its 8-byte MDEC header) to RGBA.
    /// Dimensions come from the STR sector header, which the MDEC header does not carry.
    /// </summary>
    public static MdecImage DecodeFrame(byte[] bitstream, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bitstream);
        if (width <= 0 || height <= 0) throw new ArgumentException("Frame dimensions must be positive.");

        var hdr = Mdec.ParseFrameHeader(bitstream);
        if (!hdr.MarkerOk)
            throw new MdecDecodeException($"Not an MDEC frame: marker 0x{hdr.Marker:X4} (expected 0x3800).");
        if (hdr.Version != 2)
            throw new MdecDecodeException(
                $"STR video version {hdr.Version} is not yet supported (only version 2). Version 3 uses " +
                "differential DC coding with separate luma/chroma DC-size VLC tables. Those tables cannot be " +
                "verified without a real v3 .str, and a v3 frame carries no self-check, so — per 'provably " +
                "correct or declined' — a guessed table is not shipped. Drop a sample at " +
                "$DFORGE_FIXTURES/mdec/reference.str to validate an implementation (see docs/FIXTURES.md).");
        int qscale = hdr.QuantScale;

        int mbCols = (width + 15) / 16;
        int mbRows = (height + 15) / 16;
        var rgba = new byte[width * height * 4];

        var reader = new BitReader16(bitstream, startByte: 8);

        // MDEC decodes macroblocks in column-major order: down each 16-px column,
        // then the next column to the right.
        var blocks = new double[6][];
        for (int mbX = 0; mbX < mbCols; mbX++)
        for (int mbY = 0; mbY < mbRows; mbY++)
        {
            for (int b = 0; b < 6; b++)
                blocks[b] = DecodeBlock(reader, qscale);
            PaintMacroblock(rgba, width, height, mbX * 16, mbY * 16, blocks);
        }

        return new MdecImage { Width = width, Height = height, Rgba = rgba };
    }

    // Decode one 8×8 block: DC (10-bit signed), then AC run/level codes to EOB.
    // Dequantise ONLY the coefficients the stream actually emits — absent
    // coefficients are exactly zero on MDEC (the +4/8 rounding must never be
    // applied to a coefficient that was never coded, or flat regions gain a bias).
    // Returns the block's spatial samples (post-IDCT), centred on 0.
    private static double[] DecodeBlock(BitReader16 reader, int qscale)
    {
        var natural = new double[64];

        int dc = SignExtend(reader.ReadBits(10), 10);          // version 2 DC
        natural[0] = dc * Mdec.QuantTable[0];                  // DC uses the quant table directly

        int idx = 1;
        while (true)
        {
            var sym = ReadAc(reader);
            if (sym.IsEob) break;
            idx += sym.Run;
            if (idx >= 64)
                throw new MdecDecodeException("AC coefficient run overflowed the 64-sample block.");
            int n = Mdec.ZigZag[idx];                          // zig-zag → natural position
            natural[n] = (sym.Level * Mdec.QuantTable[n] * qscale + 4) / 8.0;
            idx++;
        }

        Mdec.Idct8x8(natural);
        return natural;
    }

    // Place a decoded macroblock (6 blocks: Cr, Cb, Y1..Y4) into the RGBA raster,
    // clipping at the frame edges. 4:2:0 — one chroma sample per 2×2 luma area.
    private static void PaintMacroblock(byte[] rgba, int width, int height, int px, int py, double[][] blocks)
    {
        double[] cr = blocks[0], cb = blocks[1];
        // Y1=top-left, Y2=top-right, Y3=bottom-left, Y4=bottom-right.
        for (int ly = 0; ly < 16; ly++)
        {
            int y = py + ly;
            if (y >= height) break;
            for (int lx = 0; lx < 16; lx++)
            {
                int x = px + lx;
                if (x >= width) continue;

                int yBlock = (ly / 8) * 2 + (lx / 8);        // 0..3 → Y1..Y4
                double yy = blocks[2 + yBlock][(ly % 8) * 8 + (lx % 8)];
                int ci = (ly / 2) * 8 + (lx / 2);            // chroma is half-res
                var (r, g, b) = Mdec.YcbcrToRgb(yy, cb[ci], cr[ci]);

                int o = (y * width + x) * 4;
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = 255;
            }
        }
    }

    private static int SignExtend(int value, int bits)
    {
        int sign = 1 << (bits - 1);
        return (value & sign) != 0 ? value - (1 << bits) : value;
    }

    // ---- AC variable-length codes -----------------------------------------

    private readonly record struct AcSymbol(bool IsEob, int Run, int Level);

    /// <summary>Test seam: decode a single AC symbol from <paramref name="data"/> at a bit boundary.</summary>
    internal static (bool Eob, int Run, int Level) ReadAcForTest(byte[] data, int startByte)
    {
        var sym = ReadAc(new BitReader16(data, startByte));
        return (sym.IsEob, sym.Run, sym.Level);
    }

    private static AcSymbol ReadAc(BitReader16 reader)
    {
        int code = 0, len = 0;
        while (true)
        {
            code = (code << 1) | reader.ReadBit();
            len++;

            if (len == 2 && code == 0b10) return new AcSymbol(true, 0, 0);      // end of block
            if (len == 6 && code == 0b000001)                                   // escape
            {
                int run = reader.ReadBits(6);
                int level = SignExtend(reader.ReadBits(10), 10);
                return new AcSymbol(false, run, level);
            }

            if (AcTable.TryGetValue(Key(len, code), out var rl))
            {
                int level = reader.ReadBit() == 0 ? rl.Level : -rl.Level;       // trailing sign bit
                return new AcSymbol(false, rl.Run, level);
            }

            if (len > 17) throw new MdecDecodeException("Invalid AC variable-length code.");
        }
    }

    // Canonical-Huffman key: the sentinel bit above the code preserves the length,
    // so (len,code) pairs never collide even when a shorter code's bits match.
    private static int Key(int len, int code) => code | (1 << len);

    // MPEG-1 Table B-14 (intra AC), run/level → code. The sign bit follows the code
    // and is read separately. Built once; asserted prefix-free at startup.
    private static readonly Dictionary<int, (int Run, int Level)> AcTable = BuildAcTable();

    private static Dictionary<int, (int, int)> BuildAcTable()
    {
        // (code, run, level) — codes verbatim from the format spec (Table B-14).
        (string Code, int Run, int Level)[] rows =
        {
            ("11", 0, 1),
            ("011", 1, 1),
            ("0100", 0, 2), ("0101", 2, 1),
            ("00101", 0, 3), ("00110", 4, 1), ("00111", 3, 1),
            ("000100", 7, 1), ("000101", 6, 1), ("000110", 1, 2), ("000111", 5, 1),
            ("0000100", 2, 2), ("0000101", 9, 1), ("0000110", 0, 4), ("0000111", 8, 1),
            ("00100000", 13, 1), ("00100001", 0, 6), ("00100010", 12, 1), ("00100011", 11, 1),
            ("00100100", 3, 2), ("00100101", 1, 3), ("00100110", 0, 5), ("00100111", 10, 1),
            ("0000001000", 16, 1), ("0000001001", 5, 2), ("0000001010", 0, 7), ("0000001011", 2, 3),
            ("0000001100", 1, 4), ("0000001101", 15, 1), ("0000001110", 14, 1), ("0000001111", 4, 2),
            ("000000010000", 0, 11), ("000000010001", 8, 2), ("000000010010", 4, 3), ("000000010011", 0, 10),
            ("000000010100", 2, 4), ("000000010101", 7, 2), ("000000010110", 21, 1), ("000000010111", 20, 1),
            ("000000011000", 0, 9), ("000000011001", 19, 1), ("000000011010", 18, 1), ("000000011011", 1, 5),
            ("000000011100", 3, 3), ("000000011101", 0, 8), ("000000011110", 6, 2), ("000000011111", 17, 1),
            ("0000000010000", 10, 2), ("0000000010001", 9, 2), ("0000000010010", 5, 3), ("0000000010011", 3, 4),
            ("0000000010100", 2, 5), ("0000000010101", 1, 7), ("0000000010110", 1, 6), ("0000000010111", 0, 15),
            ("0000000011000", 0, 14), ("0000000011001", 0, 13), ("0000000011010", 0, 12), ("0000000011011", 26, 1),
            ("0000000011100", 25, 1), ("0000000011101", 24, 1), ("0000000011110", 23, 1), ("0000000011111", 22, 1),
            ("00000000010000", 0, 31), ("00000000010001", 0, 30), ("00000000010010", 0, 29), ("00000000010011", 0, 28),
            ("00000000010100", 0, 27), ("00000000010101", 0, 26), ("00000000010110", 0, 25), ("00000000010111", 0, 24),
            ("00000000011000", 0, 23), ("00000000011001", 0, 22), ("00000000011010", 0, 21), ("00000000011011", 0, 20),
            ("00000000011100", 0, 19), ("00000000011101", 0, 18), ("00000000011110", 0, 17), ("00000000011111", 0, 16),
            ("000000000010000", 0, 40), ("000000000010001", 0, 39), ("000000000010010", 0, 38), ("000000000010011", 0, 37),
            ("000000000010100", 0, 36), ("000000000010101", 0, 35), ("000000000010110", 0, 34), ("000000000010111", 0, 33),
            ("000000000011000", 0, 32), ("000000000011001", 1, 14), ("000000000011010", 1, 13), ("000000000011011", 1, 12),
            ("000000000011100", 1, 11), ("000000000011101", 1, 10), ("000000000011110", 1, 9), ("000000000011111", 1, 8),
            ("0000000000010000", 1, 18), ("0000000000010001", 1, 17), ("0000000000010010", 1, 16), ("0000000000010011", 1, 15),
            ("0000000000010100", 6, 3), ("0000000000010101", 16, 2), ("0000000000010110", 15, 2), ("0000000000010111", 14, 2),
            ("0000000000011000", 13, 2), ("0000000000011001", 12, 2), ("0000000000011010", 11, 2), ("0000000000011011", 31, 1),
            ("0000000000011100", 30, 1), ("0000000000011101", 29, 1), ("0000000000011110", 28, 1), ("0000000000011111", 27, 1),
        };

        var table = new Dictionary<int, (int, int)>(rows.Length);
        var codes = new List<string>(rows.Length) { "10", "000001" };   // EOB and escape share the code space
        foreach (var (code, run, level) in rows)
        {
            int key = 1;
            foreach (char c in code) key = (key << 1) | (c - '0');
            table[key] = (run, level);
            codes.Add(code);
        }

        // Prefix-free integrity check: no code may be a prefix of another. A transcription
        // slip that broke this would make the stream undecodable, so fail loudly at load.
        codes.Sort();
        for (int i = 1; i < codes.Count; i++)
            if (codes[i].StartsWith(codes[i - 1], StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"MDEC AC VLC table is not prefix-free: \"{codes[i - 1]}\" prefixes \"{codes[i]}\".");

        return table;
    }

    // ---- bit reader --------------------------------------------------------

    /// <summary>
    /// Reads the MDEC bitstream as 16-bit little-endian words, MSB-first within each
    /// word — the console's DMA order. Past the end it yields zero bits, which covers
    /// the frame's trailing padding.
    /// </summary>
    private sealed class BitReader16
    {
        private readonly byte[] _data;
        private int _pos;
        private ushort _word;
        private int _bitsLeft;

        public BitReader16(byte[] data, int startByte)
        {
            _data = data;
            _pos = startByte;
        }

        public int ReadBit()
        {
            if (_bitsLeft == 0)
            {
                byte lo = _pos < _data.Length ? _data[_pos] : (byte)0;
                byte hi = _pos + 1 < _data.Length ? _data[_pos + 1] : (byte)0;
                _word = (ushort)(lo | (hi << 8));
                _pos += 2;
                _bitsLeft = 16;
            }
            int bit = (_word >> 15) & 1;
            _word = (ushort)(_word << 1);
            _bitsLeft--;
            return bit;
        }

        public int ReadBits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) v = (v << 1) | ReadBit();
            return v;
        }
    }
}
