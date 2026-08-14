// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Chd;

/// <summary>
/// A FLAC decoder for the CHD "cdfl" codec — how chdman stores CD audio tracks.
/// Unlike cdzl/cdlz, a cdfl hunk carries no length prefix: the raw FLAC frames
/// (16-bit, two channels, mid/side or left/right decorrelation) sit directly at
/// the hunk offset and are self-delimiting, so this decodes frames until the
/// hunk's worth of audio is produced, byte-aligns, and hands the caller the
/// offset where the subcode stream begins. Samples are written back big-endian to
/// match the on-disc sector bytes.
///
/// Clean-room, from the public FLAC format specification. Validated byte-for-byte
/// against a real chdman cdfl image (its decompressed data matches the SHA-1 the
/// CHD stores of itself).
/// </summary>
internal static class ChdFlac
{
    /// <summary>Decode FLAC frames from <paramref name="offset"/> until
    /// <paramref name="wantBytes"/> bytes of big-endian interleaved 16-bit stereo
    /// are produced; returns those bytes and the byte offset just past the frames.</summary>
    public static (byte[] Bytes, int Next) Decode(byte[] data, int offset, int wantBytes)
    {
        var br = new Bits(data, offset);
        var outb = new List<byte>(wantBytes);

        while (outb.Count < wantBytes)
        {
            if (br.Read(14) != 0x3FFE) throw new ChdFormatException("Bad FLAC frame sync in a CHD hunk.");
            br.Read(1);                       // reserved
            int blockingStrategy = (int)br.Read(1);
            int bsCode = (int)br.Read(4);
            int srCode = (int)br.Read(4);
            int chAssign = (int)br.Read(4);
            int ssCode = (int)br.Read(3);
            br.Read(1);                       // reserved
            SkipCodedNumber(br);

            int blockSize = bsCode switch
            {
                6 => (int)br.Read(8) + 1,
                7 => (int)br.Read(16) + 1,
                1 => 192,
                >= 2 and <= 5 => 576 << (bsCode - 2),
                _ => 256 << (bsCode - 8),
            };
            if (srCode == 12) br.Read(8); else if (srCode is 13 or 14) br.Read(16);
            br.Read(8);                       // header CRC-8

            int bps = ssCode switch { 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, _ => 16 };
            int channels = chAssign < 8 ? chAssign + 1 : 2;

            var chans = new int[channels][];
            for (int c = 0; c < channels; c++)
            {
                int cbps = bps;
                if ((chAssign == 8 && c == 1) || (chAssign == 9 && c == 0) || (chAssign == 10 && c == 1))
                    cbps++;                    // the "side" channel carries a difference: one extra bit
                chans[c] = DecodeSubframe(br, blockSize, cbps);
            }

            for (int i = 0; i < blockSize; i++)
            {
                int l, r;
                switch (chAssign)
                {
                    case 8: l = chans[0][i]; r = l - chans[1][i]; break;                 // left/side
                    case 9: r = chans[1][i]; l = r + chans[0][i]; break;                 // right/side
                    case 10:                                                             // mid/side
                        int mid = chans[0][i], side = chans[1][i];
                        int m2 = (mid << 1) | (side & 1);
                        l = (m2 + side) >> 1; r = (m2 - side) >> 1; break;
                    default: l = chans[0][i]; r = channels > 1 ? chans[1][i] : l; break; // independent
                }
                outb.Add((byte)((l >> 8) & 0xFF)); outb.Add((byte)(l & 0xFF));
                outb.Add((byte)((r >> 8) & 0xFF)); outb.Add((byte)(r & 0xFF));
            }

            br.Align();
            br.Read(16);                       // frame CRC-16
        }

        return (outb.ToArray(), br.BytePos);
    }

    private static void SkipCodedNumber(Bits br)
    {
        int first = (int)br.Read(8);
        int extra = first < 0x80 ? 0 : first < 0xE0 ? 1 : first < 0xF0 ? 2 : first < 0xF8 ? 3 : first < 0xFC ? 4 : first < 0xFE ? 5 : 6;
        for (int i = 0; i < extra; i++) br.Read(8);
    }

    private static int[] DecodeSubframe(Bits br, int blockSize, int bps)
    {
        br.Read(1);                            // padding
        int type = (int)br.Read(6);
        int wasted = 0;
        if (br.Read(1) == 1) wasted = br.Unary() + 1;
        int ebps = bps - wasted;
        var outv = new int[blockSize];

        if (type == 0)                         // constant
        {
            int v = br.ReadSigned(ebps);
            for (int i = 0; i < blockSize; i++) outv[i] = v;
        }
        else if (type == 1)                    // verbatim
        {
            for (int i = 0; i < blockSize; i++) outv[i] = br.ReadSigned(ebps);
        }
        else if (type is >= 8 and <= 12)       // fixed predictor, order 0..4
        {
            int order = type - 8;
            for (int i = 0; i < order; i++) outv[i] = br.ReadSigned(ebps);
            Residual(br, outv, order, blockSize);
            RestoreFixed(outv, order, blockSize);
        }
        else if (type >= 32)                   // LPC, order 1..32
        {
            int order = (type & 0x1f) + 1;
            for (int i = 0; i < order; i++) outv[i] = br.ReadSigned(ebps);
            int prec = (int)br.Read(4) + 1;
            int shift = br.ReadSigned(5);
            var coef = new int[order];
            for (int i = 0; i < order; i++) coef[i] = br.ReadSigned(prec);
            Residual(br, outv, order, blockSize);
            for (int i = order; i < blockSize; i++)
            {
                long sum = 0;
                for (int j = 0; j < order; j++) sum += (long)coef[j] * outv[i - 1 - j];
                outv[i] += (int)(sum >> shift);
            }
        }
        else throw new ChdFormatException($"Unsupported FLAC subframe type {type} in a CHD hunk.");

        if (wasted > 0) for (int i = 0; i < blockSize; i++) outv[i] <<= wasted;
        return outv;
    }

    private static void Residual(Bits br, int[] outv, int order, int blockSize)
    {
        int method = (int)br.Read(2);
        int paramBits = method == 0 ? 4 : 5;
        int escape = method == 0 ? 0xF : 0x1F;
        int partOrder = (int)br.Read(4);
        int partitions = 1 << partOrder;
        int idx = order;
        for (int p = 0; p < partitions; p++)
        {
            int count = (blockSize >> partOrder) - (p == 0 ? order : 0);
            int param = (int)br.Read(paramBits);
            if (param == escape)
            {
                int rawbits = (int)br.Read(5);
                for (int i = 0; i < count; i++) outv[idx++] = br.ReadSigned(rawbits);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    int q = br.Unary();
                    uint rem = br.Read(param);
                    uint u = ((uint)q << param) | rem;
                    outv[idx++] = (int)((u >> 1) ^ (uint)(-(int)(u & 1)));   // zigzag
                }
            }
        }
    }

    private static void RestoreFixed(int[] x, int order, int n)
    {
        switch (order)
        {
            case 1: for (int i = 1; i < n; i++) x[i] += x[i - 1]; break;
            case 2: for (int i = 2; i < n; i++) x[i] += 2 * x[i - 1] - x[i - 2]; break;
            case 3: for (int i = 3; i < n; i++) x[i] += 3 * x[i - 1] - 3 * x[i - 2] + x[i - 3]; break;
            case 4: for (int i = 4; i < n; i++) x[i] += 4 * x[i - 1] - 6 * x[i - 2] + 4 * x[i - 3] - x[i - 4]; break;
        }
    }

    // MSB-first bit reader with byte-alignment and byte-position tracking.
    private sealed class Bits
    {
        private readonly byte[] _d;
        private int _pos;
        private ulong _buf;
        private int _bits;

        public Bits(byte[] d, int pos) { _d = d; _pos = pos; }

        public uint Read(int n)
        {
            if (n == 0) return 0;
            while (_bits < n)
            {
                // Past the end of the file the stream is corrupt or truncated — throw
                // rather than feed zero bits forever (a unary/Rice read would spin).
                if (_pos >= _d.Length)
                    throw new ChdFormatException("FLAC stream ran past the end of the CHD hunk (corrupt or truncated).");
                _buf = (_buf << 8) | _d[_pos++]; _bits += 8;
            }
            _bits -= n;
            return (uint)((_buf >> _bits) & ((1UL << n) - 1));
        }

        public int ReadSigned(int n)
        {
            if (n == 0) return 0;
            uint u = Read(n);
            return (u & (1u << (n - 1))) != 0 ? (int)(u | (~0u << n)) : (int)u;
        }

        public int Unary() { int c = 0; while (Read(1) == 0) c++; return c; }
        public void Align() { _bits &= ~7; }
        public int BytePos => _pos - (_bits / 8);
    }
}
