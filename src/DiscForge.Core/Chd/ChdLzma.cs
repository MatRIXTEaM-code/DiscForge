// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Chd;

/// <summary>
/// A raw LZMA1 decompressor for the CHD "cdlz" codec — the compressor chdman
/// reaches for by default on data, and the reason most real game CHDs need this
/// rather than the simpler cdzl (zlib) path. chdman stores a bare LZMA stream:
/// no 13-byte properties/size header, fixed lc=3 / lp=0 / pb=2, and a known
/// output size, so the decoder is primed with those constants and decodes exactly
/// the expected number of bytes.
///
/// Clean-room, from the public LZMA specification. Validated byte-for-byte
/// against a real chdman cdlz image (the decompressed data matches the SHA-1 the
/// CHD stores of itself).
/// </summary>
internal static class ChdLzma
{
    /// <summary>Decode <paramref name="outSize"/> bytes of a raw LZMA stream starting
    /// at <paramref name="offset"/> in <paramref name="input"/>.</summary>
    public static byte[] Decode(byte[] input, int offset, int outSize)
        => new Decoder(input, offset).Run(outSize);

    private sealed class Decoder
    {
        private const int NumStates = 12, PosBitsMax = 4, LenToPosStates = 4, AlignBits = 4;
        private const int EndPosModelIndex = 14, FullDistances = 1 << (EndPosModelIndex >> 1), MatchMinLen = 2;
        private const uint Top = 1u << 24, BitModelTotal = 1 << 11;
        private const int MoveBits = 5, Lc = 3, Lp = 0, Pb = 2;

        private readonly byte[] _in;
        private int _ip;
        private uint _range, _code;

        private readonly ushort[] _isMatch = new ushort[NumStates << PosBitsMax];
        private readonly ushort[] _isRep = new ushort[NumStates];
        private readonly ushort[] _isRepG0 = new ushort[NumStates];
        private readonly ushort[] _isRepG1 = new ushort[NumStates];
        private readonly ushort[] _isRepG2 = new ushort[NumStates];
        private readonly ushort[] _isRep0Long = new ushort[NumStates << PosBitsMax];
        private readonly ushort[] _posSlot = new ushort[LenToPosStates * 64];
        private readonly ushort[] _specPos = new ushort[FullDistances - EndPosModelIndex];
        private readonly ushort[] _align = new ushort[1 << AlignBits];
        private ushort[] _litProbs = Array.Empty<ushort>();
        private readonly ushort[] _lenChoice = new ushort[1], _lenChoice2 = new ushort[1];
        private readonly ushort[] _lenLow = new ushort[(1 << PosBitsMax) * 8], _lenMid = new ushort[(1 << PosBitsMax) * 8], _lenHigh = new ushort[256];
        private readonly ushort[] _rlenChoice = new ushort[1], _rlenChoice2 = new ushort[1];
        private readonly ushort[] _rlenLow = new ushort[(1 << PosBitsMax) * 8], _rlenMid = new ushort[(1 << PosBitsMax) * 8], _rlenHigh = new ushort[256];

        public Decoder(byte[] input, int offset) { _in = input; _ip = offset; }

        private static void Fill(ushort[] a) { for (int i = 0; i < a.Length; i++) a[i] = (ushort)(BitModelTotal >> 1); }
        private byte RB() => _ip < _in.Length ? _in[_ip++] : (byte)0;
        private void RcInit() { RB(); _code = 0; _range = 0xFFFFFFFF; for (int i = 0; i < 4; i++) _code = (_code << 8) | RB(); }
        private void Norm() { if (_range < Top) { _range <<= 8; _code = (_code << 8) | RB(); } }

        private int DecBit(ushort[] p, int i)
        {
            uint bound = (_range >> 11) * p[i];
            if (_code < bound) { _range = bound; p[i] += (ushort)((BitModelTotal - p[i]) >> MoveBits); Norm(); return 0; }
            _range -= bound; _code -= bound; p[i] -= (ushort)(p[i] >> MoveBits); Norm(); return 1;
        }
        private uint DecDirect(int n)
        {
            uint res = 0;
            do { _range >>= 1; _code -= _range; uint t = 0 - (_code >> 31); _code += _range & t; Norm(); res = (res << 1) + t + 1; } while (--n > 0);
            return res;
        }
        private int Tree(ushort[] p, int off, int nb) { int m = 1; for (int i = 0; i < nb; i++) m = (m << 1) + DecBit(p, off + m); return m - (1 << nb); }
        private int TreeRev(ushort[] p, int off, int nb) { int m = 1, sym = 0; for (int i = 0; i < nb; i++) { int b = DecBit(p, off + m); m = (m << 1) + b; sym |= b << i; } return sym; }
        private int LenDec(ushort[] ch, ushort[] ch2, ushort[] low, ushort[] mid, ushort[] high, int posState)
        {
            if (DecBit(ch, 0) == 0) return Tree(low, posState * 8, 3);
            if (DecBit(ch2, 0) == 0) return 8 + Tree(mid, posState * 8, 3);
            return 16 + Tree(high, 0, 8);
        }

        public byte[] Run(int outSize)
        {
            Fill(_isMatch); Fill(_isRep); Fill(_isRepG0); Fill(_isRepG1); Fill(_isRepG2); Fill(_isRep0Long);
            Fill(_posSlot); Fill(_specPos); Fill(_align);
            Fill(_lenChoice); Fill(_lenChoice2); Fill(_lenLow); Fill(_lenMid); Fill(_lenHigh);
            Fill(_rlenChoice); Fill(_rlenChoice2); Fill(_rlenLow); Fill(_rlenMid); Fill(_rlenHigh);
            _litProbs = new ushort[0x300 << (Lc + Lp)]; Fill(_litProbs);
            RcInit();

            var outb = new byte[outSize];
            int pos = 0, state = 0;
            uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
            uint pbMask = (1u << Pb) - 1, lpMask = (1u << Lp) - 1;

            while (pos < outSize)
            {
                int posState = (int)((uint)pos & pbMask);
                if (DecBit(_isMatch, (state << PosBitsMax) + posState) == 0)
                {
                    byte prev = pos > 0 ? outb[pos - 1] : (byte)0;
                    int litState = (int)((((uint)pos & lpMask) << Lc) + (uint)(prev >> (8 - Lc)));
                    int probOff = 0x300 * litState;
                    int sym = 1;
                    if (state >= 7)
                    {
                        int matchByte = outb[pos - (int)rep0 - 1];
                        while (sym < 0x100)
                        {
                            int matchBit = (matchByte >> 7) & 1; matchByte <<= 1;
                            int bit = DecBit(_litProbs, probOff + ((1 + matchBit) << 8) + sym);
                            sym = (sym << 1) | bit;
                            if (matchBit != bit) break;
                        }
                    }
                    while (sym < 0x100) sym = (sym << 1) | DecBit(_litProbs, probOff + sym);
                    outb[pos++] = (byte)sym;
                    state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                    continue;
                }

                int len;
                if (DecBit(_isRep, state) == 1)
                {
                    if (DecBit(_isRepG0, state) == 0)
                    {
                        if (DecBit(_isRep0Long, (state << PosBitsMax) + posState) == 0)
                        {
                            state = state < 7 ? 9 : 11;
                            outb[pos] = outb[pos - (int)rep0 - 1]; pos++;
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (DecBit(_isRepG1, state) == 0) dist = rep1;
                        else { if (DecBit(_isRepG2, state) == 0) dist = rep2; else { dist = rep3; rep3 = rep2; } rep2 = rep1; }
                        rep1 = rep0; rep0 = dist;
                    }
                    len = LenDec(_rlenChoice, _rlenChoice2, _rlenLow, _rlenMid, _rlenHigh, posState) + MatchMinLen;
                    state = state < 7 ? 8 : 11;
                }
                else
                {
                    rep3 = rep2; rep2 = rep1; rep1 = rep0;
                    len = LenDec(_lenChoice, _lenChoice2, _lenLow, _lenMid, _lenHigh, posState);
                    state = state < 7 ? 7 : 10;
                    int lenState = len < LenToPosStates ? len : LenToPosStates - 1;
                    int posSlot = Tree(_posSlot, lenState * 64, 6);
                    if (posSlot < 4) rep0 = (uint)posSlot;
                    else
                    {
                        int numDirect = (posSlot >> 1) - 1;
                        rep0 = (uint)((2 | (posSlot & 1)) << numDirect);
                        if (posSlot < EndPosModelIndex)
                            rep0 += (uint)TreeRev(_specPos, (int)rep0 - posSlot - 1, numDirect);
                        else
                        {
                            rep0 += DecDirect(numDirect - AlignBits) << AlignBits;
                            rep0 += (uint)TreeRev(_align, 0, AlignBits);
                        }
                    }
                    len += MatchMinLen;
                    if (rep0 == 0xFFFFFFFF) break;   // end marker (chdman doesn't emit one)
                }

                int src = pos - (int)rep0 - 1;
                if (src < 0) throw new ChdFormatException("Bad LZMA distance in a CHD hunk.");
                for (int i = 0; i < len && pos < outSize; i++) outb[pos++] = outb[src + i];
            }
            return outb;
        }
    }
}
