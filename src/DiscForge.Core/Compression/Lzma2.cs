// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Compression;

/// <summary>
/// Clean-room LZMA2 decoder, from the public .xz file-format specification's description of the
/// LZMA2 chunk layer on top of the LZMA model already implemented in <see cref="Lzma1"/>. LZMA2 is
/// LZMA split into chunks, each either stored raw or LZMA-compressed; a compressed chunk restarts the
/// range coder and may keep, reset, or re-parameterise the model state (probabilities, state machine,
/// rep distances) carried over from the previous one.
///
/// This is a streaming decoder: the dictionary is a circular buffer of the stream's stated size, and
/// decoding can be fed piece by piece, each piece holding whole chunks and producing a known number of
/// bytes. That is exactly how xdelta3 uses it — one LZMA2 stream per section kind, sync-flushed at the
/// end of every window and continued (dictionary and model intact) in the next.
/// </summary>
public sealed class Lzma2Decoder
{
    private readonly LzmaModel _model;
    private bool _needProps = true, _needStateReset = true, _ended;

    /// <param name="dictionarySize">Dictionary size in bytes (from the stream's properties). Capped at
    /// 1.5 GB; real streams use a few MB.</param>
    public Lzma2Decoder(long dictionarySize)
    {
        if (dictionarySize <= 0) throw new ArgumentOutOfRangeException(nameof(dictionarySize));
        _model = new LzmaModel((int)Math.Min(dictionarySize, 1_500_000_000L));
    }

    /// <summary>LZMA2's one-byte dictionary-size property → bytes.</summary>
    public static long DictionarySizeFromProperty(byte prop)
    {
        if (prop > 40) throw new InvalidDataException("Invalid LZMA2 dictionary-size property.");
        return prop == 40 ? uint.MaxValue : (2L | (prop & 1)) << (prop / 2 + 11);
    }

    /// <summary>True once the end-of-stream control byte has been seen.</summary>
    public bool Ended => _ended;

    /// <summary>
    /// Decode chunks from <paramref name="input"/> into <paramref name="output"/>, which must end up
    /// exactly full. Decoding stops at the end-of-stream byte, or when the output is full and the input
    /// is used up (a sync-flushed stream that continues in a later call). Returns the input consumed.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int inPos = 0, outPos = 0;
        while (true)
        {
            if (inPos >= input.Length)
            {
                if (outPos == output.Length) break;
                throw new InvalidDataException(
                    $"LZMA2 data ran out after {outPos:N0} of {output.Length:N0} bytes.");
            }
            if (_ended) throw new InvalidDataException("LZMA2 data continues after its end marker.");
            int control = input[inPos++];
            if (control == 0x00) { _ended = true; break; }

            if (control is 0x01 or 0x02)
            {
                if (inPos + 2 > input.Length) throw new InvalidDataException("LZMA2 stored chunk header truncated.");
                int size = ((input[inPos] << 8) | input[inPos + 1]) + 1;
                inPos += 2;
                if (inPos + size > input.Length) throw new InvalidDataException("LZMA2 stored chunk truncated.");
                if (outPos + size > output.Length) throw new InvalidDataException("LZMA2 data is larger than declared.");
                if (control == 0x01) _model.ResetDictionary();
                _model.Store(input.Slice(inPos, size), output.Slice(outPos, size));
                inPos += size; outPos += size;
                _needStateReset = true; // the next LZMA chunk must reset state
                continue;
            }

            if (control < 0x80) throw new InvalidDataException($"Invalid LZMA2 control byte 0x{control:X2}.");
            if (inPos + 4 > input.Length) throw new InvalidDataException("LZMA2 chunk header truncated.");
            int unpacked = (((control & 0x1F) << 16) | (input[inPos] << 8) | input[inPos + 1]) + 1;
            int packed = ((input[inPos + 2] << 8) | input[inPos + 3]) + 1;
            inPos += 4;
            int reset = (control >> 5) & 3;
            if (reset == 3) _model.ResetDictionary();
            if (reset >= 2)
            {
                if (inPos >= input.Length) throw new InvalidDataException("LZMA2 properties byte missing.");
                _model.SetProperties(input[inPos++]);
                _needProps = false;
            }
            else if (_needProps) throw new InvalidDataException("LZMA2 chunk uses properties before any were set.");
            if (reset >= 1) { _model.ResetState(); _needStateReset = false; }
            else if (_needStateReset) throw new InvalidDataException("LZMA2 chunk continues a state that was never set.");

            if (inPos + packed > input.Length) throw new InvalidDataException("LZMA2 chunk data truncated.");
            if (outPos + unpacked > output.Length) throw new InvalidDataException("LZMA2 data is larger than declared.");
            _model.DecodeChunk(input.Slice(inPos, packed), output.Slice(outPos, unpacked));
            inPos += packed; outPos += unpacked;
        }

        if (outPos != output.Length)
            throw new InvalidDataException($"LZMA2 data decoded to {outPos:N0} bytes; {output.Length:N0} were declared.");
        return inPos;
    }

    /// <summary>The LZMA model with its state held in fields so it can span chunks and calls, over a
    /// circular dictionary. Same algorithm and constants as <see cref="Lzma1"/>'s decoder.</summary>
    private sealed class LzmaModel
    {
        private readonly byte[] _dict;
        private int _dictPos;     // next write index in the circular buffer
        private long _total;      // bytes produced since the last dictionary reset

        private int _lc, _lp, _pb;
        private uint _state, _rep0, _rep1, _rep2, _rep3;

        private ushort[] _isMatch = [], _isRep = [], _isRepG0 = [], _isRepG1 = [], _isRepG2 = [],
            _isRep0Long = [], _posSlot = [], _specPos = [], _align = [], _literals = [];
        private LenDecoder _len = new(), _repLen = new();

        private byte[] _in = [];
        private int _inPos;
        private uint _range, _code;

        public LzmaModel(int dictSize) { _dict = new byte[Math.Max(dictSize, 4096)]; }

        public void ResetDictionary() { _total = 0; _dictPos = 0; }

        public void SetProperties(byte props)
        {
            int d = props;
            if (d >= 9 * 5 * 5) throw new InvalidDataException("Invalid LZMA2 properties byte.");
            _lc = d % 9; d /= 9;
            _lp = d % 5;
            _pb = d / 5;
            if (_lc + _lp > 4) throw new InvalidDataException("LZMA2 requires lc + lp <= 4.");
        }

        public void ResetState()
        {
            _isMatch = NewProbs(12 << 4);
            _isRep = NewProbs(12);
            _isRepG0 = NewProbs(12);
            _isRepG1 = NewProbs(12);
            _isRepG2 = NewProbs(12);
            _isRep0Long = NewProbs(12 << 4);
            _posSlot = NewProbs(4 * 64);
            _specPos = NewProbs(128);
            _align = NewProbs(16);
            _literals = NewProbs(0x300 << (_lc + _lp));
            _len = new LenDecoder();
            _repLen = new LenDecoder();
            _state = 0; _rep0 = _rep1 = _rep2 = _rep3 = 0;
        }

        public void Store(ReadOnlySpan<byte> raw, Span<byte> output)
        {
            raw.CopyTo(output);
            foreach (byte b in raw) Put(b);
        }

        private void Put(byte b)
        {
            _dict[_dictPos] = b;
            if (++_dictPos == _dict.Length) _dictPos = 0;
            _total++;
        }

        /// <summary>Byte <paramref name="dist"/>+1 back (dist 0 = the previous byte).</summary>
        private byte Get(uint dist)
        {
            int i = _dictPos - (int)dist - 1;
            if (i < 0) i += _dict.Length;
            return _dict[i];
        }

        private static ushort[] NewProbs(int n)
        {
            var p = new ushort[n];
            Array.Fill(p, (ushort)1024);
            return p;
        }

        private byte NextByte()
        {
            if (_inPos >= _in.Length) throw new InvalidDataException("LZMA2 chunk ended early.");
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
            if (DecodeBit(len.Choice, 0) == 0) return 2 + DecodeBitTree(len.Low, posState * 8, 3);
            if (DecodeBit(len.Choice, 1) == 0) return 10 + DecodeBitTree(len.Mid, posState * 8, 3);
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

        private bool Reaches(uint dist) => dist < _total && dist < (uint)_dict.Length;

        /// <summary>Decode one LZMA chunk, filling <paramref name="output"/> exactly.</summary>
        public void DecodeChunk(ReadOnlySpan<byte> chunk, Span<byte> output)
        {
            _in = chunk.ToArray();
            _inPos = 0;
            _range = 0xFFFFFFFF;
            _code = 0;
            if (NextByte() != 0) throw new InvalidDataException("LZMA2 chunk does not start with a zero byte.");
            for (int i = 0; i < 4; i++) _code = (_code << 8) | NextByte();

            uint pbMask = (1u << _pb) - 1, lpMask = (1u << _lp) - 1;
            int o = 0, end = output.Length;
            while (o < end)
            {
                int posState = (int)((uint)_total & pbMask);

                if (DecodeBit(_isMatch, (int)((_state << 4) + posState)) == 0)
                {
                    byte prev = _total > 0 ? Get(0) : (byte)0;
                    int litState = (int)(((((uint)_total & lpMask) << _lc) + ((uint)prev >> (8 - _lc))) * 0x300);
                    uint sym = 1;
                    if (_state >= 7)
                    {
                        if (!Reaches(_rep0)) throw new InvalidDataException("Corrupted LZMA2 stream (bad match byte).");
                        uint matchByte = Get(_rep0);
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
                    output[o++] = (byte)sym;
                    Put((byte)sym);
                    _state = _state < 4 ? 0u : _state < 10 ? _state - 3 : _state - 6;
                    continue;
                }

                uint len;
                if (DecodeBit(_isRep, (int)_state) != 0)
                {
                    if (_total == 0) throw new InvalidDataException("Corrupted LZMA2 stream (rep at start).");
                    if (DecodeBit(_isRepG0, (int)_state) == 0)
                    {
                        if (DecodeBit(_isRep0Long, (int)((_state << 4) + posState)) == 0)
                        {
                            _state = _state < 7 ? 9u : 11u;
                            if (!Reaches(_rep0)) throw new InvalidDataException("Corrupted LZMA2 stream (bad short rep).");
                            byte b = Get(_rep0);
                            output[o++] = b;
                            Put(b);
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (DecodeBit(_isRepG1, (int)_state) == 0) dist = _rep1;
                        else
                        {
                            if (DecodeBit(_isRepG2, (int)_state) == 0) dist = _rep2;
                            else { dist = _rep3; _rep3 = _rep2; }
                            _rep2 = _rep1;
                        }
                        _rep1 = _rep0;
                        _rep0 = dist;
                    }
                    len = DecodeLen(_repLen, posState);
                    _state = _state < 7 ? 8u : 11u;
                }
                else
                {
                    _rep3 = _rep2; _rep2 = _rep1; _rep1 = _rep0;
                    len = DecodeLen(_len, posState);
                    _state = _state < 7 ? 7u : 10u;
                    _rep0 = DecodeDistance(len);
                    if (_rep0 == 0xFFFFFFFF) throw new InvalidDataException("LZMA2 chunks may not carry an end marker.");
                }

                if (!Reaches(_rep0)) throw new InvalidDataException("Corrupted LZMA2 stream (distance beyond dictionary).");
                if (o + (int)len > end) throw new InvalidDataException("Corrupted LZMA2 stream (match runs past the chunk).");
                for (uint i = 0; i < len; i++)
                {
                    byte b = Get(_rep0);
                    output[o++] = b;
                    Put(b);
                }
            }
        }
    }
}

/// <summary>One-shot LZMA2 decode of a whole stream whose decoded size is known.</summary>
public static class Lzma2
{
    public static byte[] Decode(ReadOnlySpan<byte> input, int outputSize, out int consumed, long dictionarySize = 0)
    {
        var output = new byte[outputSize];
        var dec = new Lzma2Decoder(dictionarySize > 0 ? dictionarySize : Math.Max(outputSize, 4096));
        consumed = dec.Decode(input, output);
        return output;
    }
}

/// <summary>
/// Clean-room decoder for the .xz container (public .xz file-format specification) restricted to
/// what DiscForge meets in practice: a stream whose blocks use LZMA2 as their only filter. The
/// stream header's and block headers' CRC-32s are verified; block checks are verified when they are
/// CRC-32 and skipped otherwise (callers verify decoded bytes by their own checksum).
///
/// Streaming, for xdelta3: its "LZMA" secondary compression keeps one .xz stream per section kind
/// for the whole patch, sync-flushed at the end of every window. The first window's section starts
/// with the stream and block headers; every later window's section is just the next LZMA2 chunks, and
/// the stream is never finished (no index or footer). <see cref="Decode"/> handles both: call it once
/// per section, in order.
/// </summary>
public sealed class XzStreamDecoder
{
    private static readonly byte[] Magic = { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 };
    private Lzma2Decoder? _block;
    private int _checkSize;
    private int _check;

    public static bool HasMagic(ReadOnlySpan<byte> data) => data.Length >= 6 && data[..6].SequenceEqual(Magic);

    /// <summary>Decode the next piece of the stream into exactly <paramref name="output"/>.</summary>
    public void Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int pos = 0, outPos = 0;
        if (HasMagic(input))
        {
            if (input.Length < 12) throw new InvalidDataException(".xz stream header truncated.");
            if (input[6] != 0 || (input[7] & 0xF0) != 0) throw new InvalidDataException("Unsupported .xz stream flags.");
            _check = input[7] & 0x0F;
            _checkSize = _check == 0 ? 0 : 4 << ((_check - 1) / 3);
            if (Crc.Crc32(input.Slice(6, 2)) != ReadLe32(input, 8)) throw new InvalidDataException(".xz stream header CRC mismatch.");
            _block = null;
            pos = 12;
        }
        else if (_block is null && outPos < output.Length)
        {
            throw new InvalidDataException("Not an .xz stream, and no earlier stream to continue.");
        }

        while (outPos < output.Length || (pos < input.Length && _block is null))
        {
            if (_block is null)
            {
                if (pos >= input.Length) throw new InvalidDataException(".xz stream ran out before its data.");
                if (input[pos] == 0x00) throw new InvalidDataException(".xz stream reached its index with output still owed.");
                pos += ReadBlockHeader(input[pos..]);
            }

            pos += _block!.Decode(input[pos..], output[outPos..]);
            outPos = output.Length;
            if (_block.Ended)
            {
                // Block finished: padding, check, then either another block or the index.
                while ((pos & 3) != 0)
                {
                    if (pos >= input.Length || input[pos] != 0) throw new InvalidDataException(".xz block padding is not zero.");
                    pos++;
                }
                if (pos + _checkSize > input.Length) throw new InvalidDataException(".xz block check truncated.");
                pos += _checkSize;
                _block = null;
                if (pos >= input.Length || input[pos] == 0x00) break; // index (or nothing) follows
            }
            else break; // sync-flushed: the stream continues in the next call
        }
        if (outPos != output.Length) throw new InvalidDataException(".xz data ran out before the declared size.");
    }

    private int ReadBlockHeader(ReadOnlySpan<byte> input)
    {
        int headerSize = (input[0] + 1) * 4;
        if (headerSize > input.Length) throw new InvalidDataException(".xz block header truncated.");
        var header = input[..headerSize];
        if (Crc.Crc32(header[..^4]) != ReadLe32(header, headerSize - 4))
            throw new InvalidDataException(".xz block header CRC mismatch.");
        int flags = header[1];
        int filters = (flags & 0x03) + 1;
        if ((flags & 0x3C) != 0) throw new InvalidDataException("Unsupported .xz block flags.");
        int hp = 2;
        if ((flags & 0x40) != 0) ReadMultibyte(header, ref hp);
        if ((flags & 0x80) != 0) ReadMultibyte(header, ref hp);
        long dict = 0;
        for (int f = 0; f < filters; f++)
        {
            long id = ReadMultibyte(header, ref hp);
            long propsSize = ReadMultibyte(header, ref hp);
            if (id != 0x21 || filters != 1 || propsSize != 1)
                throw new InvalidDataException($".xz filter 0x{id:X} is not supported (only a lone LZMA2 filter is).");
            dict = Lzma2Decoder.DictionarySizeFromProperty(header[hp++]);
        }
        _block = new Lzma2Decoder(dict);
        return headerSize;
    }

    private static long ReadMultibyte(ReadOnlySpan<byte> buf, ref int pos)
    {
        long value = 0;
        for (int i = 0; i < 9; i++)
        {
            if (pos >= buf.Length) throw new InvalidDataException(".xz integer truncated.");
            byte b = buf[pos++];
            value |= (long)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException(".xz integer too long.");
    }

    private static uint ReadLe32(ReadOnlySpan<byte> b, int at) =>
        (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

    private static class Crc
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        public static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            return ~c;
        }
    }
}
