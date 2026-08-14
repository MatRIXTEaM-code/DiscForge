// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Chd;

/// <summary>
/// Decoder for the CHD 'huff' hunk codec — order-0 (static) Huffman over bytes. A hunk
/// begins with a Huffman tree for a 256-symbol, max-16-bit code (the tree's code
/// lengths are themselves compactly encoded via a 24-symbol "small tree"), then the
/// hunk's bytes as codes. This is the same tree encoding the CHD map's Huffman uses,
/// in its huffman-of-lengths form.
///
/// Clean-room implementation of the public CHD huff format; validated by decoding
/// chdman-produced huff hunks back to their exact source bytes (confirmed by the CHD
/// SHA-1 in <see cref="ChdHdExtractor"/>).
/// </summary>
internal static class ChdHuff
{
    public static byte[] Decode(byte[] input, int offset, int length, int outSize)
    {
        var bits = new BitReader(input, offset, length);
        var tree = ImportTreeHuffman(bits, numCodes: 256, maxBits: 16);
        var outBuf = new byte[outSize];
        for (int i = 0; i < outSize; i++) outBuf[i] = (byte)tree.DecodeOne(bits);
        return outBuf;
    }

    // import_tree_huffman: a 24-symbol, max-6-bit "small tree" (its lengths read with a
    // start/run scheme), then the main tree's code lengths decoded through it — a value
    // v>0 is a length v-1; v==0 is a run of the last length, count read as (3-bit)+2 and,
    // if that hits the cap, extended by rlefullbits more.
    private static Huffman ImportTreeHuffman(BitReader bits, int numCodes, int maxBits)
    {
        var smallLen = new int[24];
        smallLen[0] = bits.Read(3);
        int start = bits.Read(3) + 1;
        int count = 0;
        for (int index = 1; index < 24; index++)
        {
            if (index < start || count == 7) smallLen[index] = 0;
            else { count = bits.Read(3); smallLen[index] = count == 7 ? 0 : count; }
        }
        var small = new Huffman(smallLen, 6);

        int rleFullBits = 0;
        for (uint t = (uint)(numCodes - 9); t != 0; t >>= 1) rleFullBits++;

        var lengths = new int[numCodes];
        int last = 0, cur = 0;
        while (cur < numCodes)
        {
            int value = small.DecodeOne(bits);
            if (value != 0) lengths[cur++] = last = value - 1;
            else
            {
                int rep = bits.Read(3) + 2;
                if (rep == 9) rep += bits.Read(rleFullBits);
                while (rep-- > 0 && cur < numCodes) lengths[cur++] = last;
            }
        }
        return new Huffman(lengths, maxBits);
    }

    // Canonical Huffman decoder matching MAME's assign_canonical_codes (lengths
    // processed longest-to-shortest) with a bit-at-a-time longest-prefix decode.
    private sealed class Huffman
    {
        private readonly Dictionary<(int len, int code), int> _map = new();
        private readonly int _maxBits;

        public Huffman(int[] lengths, int maxBits)
        {
            _maxBits = maxBits;
            var histo = new int[33];
            foreach (int l in lengths) if (l > 0 && l <= 32) histo[l]++;
            var startCode = new int[33];
            long curStart = 0;
            for (int codeLen = 32; codeLen > 0; codeLen--)
            {
                startCode[codeLen] = (int)curStart;
                curStart = (curStart + histo[codeLen]) >> 1;
            }
            var counter = (int[])startCode.Clone();
            for (int sym = 0; sym < lengths.Length; sym++)
            {
                int l = lengths[sym];
                if (l > 0) _map[(l, counter[l]++)] = sym;
            }
        }

        public int DecodeOne(BitReader bits)
        {
            int code = 0;
            for (int l = 1; l <= _maxBits + 1; l++)
            {
                code = (code << 1) | bits.Read(1);
                if (_map.TryGetValue((l, code), out int sym)) return sym;
            }
            throw new ChdFormatException("CHD huff stream held an invalid Huffman code.");
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _d;
        private readonly long _end;
        private long _bitPos;

        public BitReader(byte[] data, long start, long lengthBytes)
        {
            _d = data; _bitPos = start * 8; _end = (start + lengthBytes) * 8;
        }

        public int Read(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                if (_bitPos >= _end) throw new ChdFormatException("CHD huff stream ended early.");
                int bit = (_d[(int)(_bitPos >> 3)] >> (7 - (int)(_bitPos & 7))) & 1;
                v = (v << 1) | bit; _bitPos++;
            }
            return v;
        }
    }
}
