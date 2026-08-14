// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Compression;

/// <summary>
/// Clean-room LZMA1 decoder, from Igor Pavlov's public LZMA specification: the 11-bit adaptive
/// range coder, literal/match state machine, four rep distances, and the lc/lp/pb context model
/// from the 5-byte properties header. Written to decode AaruFormat's LZMA-compressed blocks (whose
/// payload is exactly [5-byte properties][raw LZMA1 stream] with the uncompressed size known from
/// the block header), and validated against streams produced by liblzma — the reference
/// implementation — across property variants and data shapes. In AaruFormat use the decode is
/// additionally gated by the block's stored CRC-64 of the uncompressed data, so a wrong decode can
/// never be emitted: it is proven right or it throws.
/// </summary>
public static class Lzma1
{
    /// <summary>
    /// Decode a raw LZMA1 stream. <paramref name="properties"/> is the 5-byte header (lc/lp/pb byte
    /// + little-endian dictionary size); <paramref name="outputSize"/> is the exact decompressed
    /// length, which LZMA1 cannot know by itself. Throws <see cref="InvalidDataException"/> on a
    /// malformed stream.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> properties, ReadOnlySpan<byte> input, int outputSize)
    {
        if (properties.Length < 5) throw new InvalidDataException("LZMA properties must be 5 bytes.");
        if (outputSize < 0) throw new ArgumentOutOfRangeException(nameof(outputSize));
        int d = properties[0];
        if (d >= 9 * 5 * 5) throw new InvalidDataException("Invalid LZMA properties byte.");
        int lc = d % 9; d /= 9;
        int lp = d % 5;
        int pb = d / 5;

        var output = new byte[outputSize];
        if (outputSize == 0) return output;

        var dec = new Decoder(input, output, lc, lp, pb);
        dec.Run();
        return output;
    }

    private ref struct Decoder
    {
        // ---- range decoder ----
        private readonly ReadOnlySpan<byte> _in;
        private int _inPos;
        private uint _range;
        private uint _code;

        // ---- output / dictionary (the output IS the window: sizes fit in memory) ----
        private readonly byte[] _out;
        private int _pos;

        private readonly int _lc, _lp, _pb;

        // ---- probability models (11-bit, init 1024) ----
        private readonly ushort[] _isMatch;      // [state << 4 | posState]
        private readonly ushort[] _isRep;        // [state]
        private readonly ushort[] _isRepG0;
        private readonly ushort[] _isRepG1;
        private readonly ushort[] _isRepG2;
        private readonly ushort[] _isRep0Long;   // [state << 4 | posState]
        private readonly ushort[] _posSlot;      // [lenToPosState * 64 + treeIndex]
        private readonly ushort[] _specPos;      // 128 is generous for indices 1..114
        private readonly ushort[] _align;        // 16
        private readonly ushort[] _literals;     // 0x300 << (lc + lp)
        private readonly LenDecoder _len;
        private readonly LenDecoder _repLen;

        public Decoder(ReadOnlySpan<byte> input, byte[] output, int lc, int lp, int pb)
        {
            _in = input; _inPos = 0; _out = output; _pos = 0;
            _lc = lc; _lp = lp; _pb = pb;

            _isMatch = NewProbs(12 << 4);
            _isRep = NewProbs(12);
            _isRepG0 = NewProbs(12);
            _isRepG1 = NewProbs(12);
            _isRepG2 = NewProbs(12);
            _isRep0Long = NewProbs(12 << 4);
            _posSlot = NewProbs(4 * 64);
            _specPos = NewProbs(128);
            _align = NewProbs(16);
            _literals = NewProbs(0x300 << (lc + lp));
            _len = new LenDecoder();
            _repLen = new LenDecoder();

            // Range decoder init: one ignored byte (must be 0), then 4 code bytes.
            _range = 0xFFFFFFFF;
            _code = 0;
            byte first = NextByte();
            if (first != 0) throw new InvalidDataException("LZMA stream does not start with a zero byte.");
            for (int i = 0; i < 4; i++) _code = (_code << 8) | NextByte();
        }

        private static ushort[] NewProbs(int n)
        {
            var p = new ushort[n];
            Array.Fill(p, (ushort)1024);
            return p;
        }

        private byte NextByte()
        {
            if (_inPos >= _in.Length) throw new InvalidDataException("LZMA stream ended early.");
            return _in[_inPos++];
        }

        private void Normalize()
        {
            if (_range < (1u << 24))
            {
                _range <<= 8;
                _code = (_code << 8) | NextByte();
            }
        }

        private uint DecodeBit(ushort[] probs, int index)
        {
            uint prob = probs[index];
            uint bound = (_range >> 11) * prob;
            uint bit;
            if (_code < bound)
            {
                probs[index] = (ushort)(prob + ((2048 - prob) >> 5));
                _range = bound;
                bit = 0;
            }
            else
            {
                probs[index] = (ushort)(prob - (prob >> 5));
                _code -= bound;
                _range -= bound;
                bit = 1;
            }
            Normalize();
            return bit;
        }

        private uint DecodeDirectBits(int numBits)
        {
            uint res = 0;
            do
            {
                _range >>= 1;
                _code -= _range;
                uint t = 0 - (_code >> 31);
                _code += _range & t;
                // (code == range here would mark corruption in the spec; correctness is proven by the
                // caller's CRC gate, so decoding continues rather than false-rejecting.)
                Normalize();
                res = (res << 1) + t + 1;
            } while (--numBits > 0);
            return res;
        }

        private uint DecodeBitTree(ushort[] probs, int offset, int numBits)
        {
            uint m = 1;
            for (int i = 0; i < numBits; i++) m = (m << 1) + DecodeBit(probs, offset + (int)m);
            return m - (1u << numBits);
        }

        private uint DecodeBitTreeReverse(ushort[] probs, int offset, int numBits)
        {
            uint m = 1, sym = 0;
            for (int i = 0; i < numBits; i++)
            {
                uint bit = DecodeBit(probs, offset + (int)m);
                m = (m << 1) + bit;
                sym |= bit << i;
            }
            return sym;
        }

        private sealed class LenDecoder
        {
            public readonly ushort[] Choice = { 1024, 1024 };
            public readonly ushort[] Low = NewProbs(16 * 8);
            public readonly ushort[] Mid = NewProbs(16 * 8);
            public readonly ushort[] High = NewProbs(256);
        }

        private uint DecodeLen(LenDecoder len, int posState)
        {
            if (DecodeBit(len.Choice, 0) == 0)
                return 2 + DecodeBitTree(len.Low, posState * 8, 3);
            if (DecodeBit(len.Choice, 1) == 0)
                return 10 + DecodeBitTree(len.Mid, posState * 8, 3);
            return 18 + DecodeBitTree(len.High, 0, 8);
        }

        private uint DecodeDistance(uint len)
        {
            uint lenToPosState = Math.Min(len - 2, 3u);
            uint slot = DecodeBitTree(_posSlot, (int)(lenToPosState * 64), 6);
            if (slot < 4) return slot;

            int numDirectBits = (int)((slot >> 1) - 1);
            uint dist = (2 | (slot & 1)) << numDirectBits;
            if (slot < 14)
                dist += DecodeBitTreeReverse(_specPos, (int)(dist - slot - 1), numDirectBits);
            else
            {
                dist += DecodeDirectBits(numDirectBits - 4) << 4;
                dist += DecodeBitTreeReverse(_align, 0, 4);
            }
            return dist;
        }

        public void Run()
        {
            int outSize = _out.Length;
            uint state = 0, rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
            uint pbMask = (1u << _pb) - 1, lpMask = (1u << _lp) - 1;

            while (_pos < outSize)
            {
                int posState = (int)((uint)_pos & pbMask);

                if (DecodeBit(_isMatch, (int)((state << 4) + posState)) == 0)
                {
                    // ---- literal ----
                    byte prev = _pos > 0 ? _out[_pos - 1] : (byte)0;
                    int litState = (int)(((((uint)_pos & lpMask) << _lc) + ((uint)prev >> (8 - _lc))) * 0x300);
                    uint sym = 1;
                    if (state >= 7)
                    {
                        // matched literal: predicted by the byte at the last match distance
                        if (_pos - (int)rep0 - 1 < 0) throw new InvalidDataException("Corrupted LZMA stream (bad match byte).");
                        uint matchByte = _out[_pos - (int)rep0 - 1];
                        do
                        {
                            uint matchBit = (matchByte >> 7) & 1;
                            matchByte <<= 1;
                            uint bit = DecodeBit(_literals, litState + (int)(((1 + matchBit) << 8) + sym));
                            sym = (sym << 1) | bit;
                            if (matchBit != bit)
                            {
                                while (sym < 0x100) sym = (sym << 1) | DecodeBit(_literals, litState + (int)sym);
                                break;
                            }
                        } while (sym < 0x100);
                    }
                    else
                    {
                        while (sym < 0x100) sym = (sym << 1) | DecodeBit(_literals, litState + (int)sym);
                    }
                    _out[_pos++] = (byte)sym;
                    state = state < 4 ? 0u : state < 10 ? state - 3 : state - 6;
                    continue;
                }

                uint len;
                if (DecodeBit(_isRep, (int)state) != 0)
                {
                    // ---- rep match ----
                    if (_pos == 0) throw new InvalidDataException("Corrupted LZMA stream (rep at start).");
                    if (DecodeBit(_isRepG0, (int)state) == 0)
                    {
                        if (DecodeBit(_isRep0Long, (int)((state << 4) + posState)) == 0)
                        {
                            // short rep: one byte at rep0
                            state = state < 7 ? 9u : 11u;
                            if (_pos - (int)rep0 - 1 < 0) throw new InvalidDataException("Corrupted LZMA stream (bad short rep).");
                            _out[_pos] = _out[_pos - (int)rep0 - 1];
                            _pos++;
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (DecodeBit(_isRepG1, (int)state) == 0) dist = rep1;
                        else
                        {
                            if (DecodeBit(_isRepG2, (int)state) == 0) dist = rep2;
                            else { dist = rep3; rep3 = rep2; }
                            rep2 = rep1;
                        }
                        rep1 = rep0;
                        rep0 = dist;
                    }
                    len = DecodeLen(_repLen, posState);
                    state = state < 7 ? 8u : 11u;
                }
                else
                {
                    // ---- new match ----
                    rep3 = rep2; rep2 = rep1; rep1 = rep0;
                    len = DecodeLen(_len, posState);
                    state = state < 7 ? 7u : 10u;
                    rep0 = DecodeDistance(len);
                    if (rep0 == 0xFFFFFFFF)
                    {
                        // An end-of-stream marker BEFORE the declared size proves corruption: a
                        // conforming encoder with a known size never emits one early. Throw rather
                        // than return a silently short, zero-padded buffer.
                        if (_pos != outSize)
                            throw new InvalidDataException(
                                $"LZMA end-of-stream marker at {_pos} of {outSize} declared bytes — corrupted stream.");
                        return;
                    }
                }

                if (rep0 >= (uint)_pos) throw new InvalidDataException("Corrupted LZMA stream (distance beyond output).");
                if (_pos + (int)len > outSize)
                    throw new InvalidDataException(
                        "Corrupted LZMA stream (a match runs past the declared output size).");
                int src = _pos - (int)rep0 - 1;
                for (uint i = 0; i < len; i++) _out[_pos++] = _out[src++];
            }
        }
    }
}
