// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Compression;

/// <summary>
/// Backward bitstream reader for zstd's FSE and Huffman streams. The stream is read from the end
/// toward the start: the last byte carries a single "1" padding bit at its highest set position,
/// and reading begins just below it. A multi-bit read returns its bits most-significant-first
/// (the first bit taken from the stream is the high bit of the result), matching the encoder.
/// </summary>
internal sealed class ReverseBitReader
{
    private readonly byte[] _data;
    private int _bitPos;

    public ReverseBitReader(ReadOnlySpan<byte> s)
    {
        _data = s.ToArray();
        if (_data.Length == 0) { _bitPos = -1; return; }
        int last = _data[^1];
        if (last == 0) throw new InvalidDataException("zstd bitstream: last byte has no padding bit.");
        int highest = 7;
        while ((last & (1 << highest)) == 0) highest--;
        _bitPos = (_data.Length - 1) * 8 + highest - 1;
    }

    private bool _overflow;

    public int ReadBits(int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++)
        {
            int bit = 0;
            if (_bitPos >= 0) bit = (_data[_bitPos >> 3] >> (_bitPos & 7)) & 1;
            else _overflow = true;
            _bitPos--;
            v = (v << 1) | bit;
        }
        return v;
    }

    /// <summary>Return <paramref name="n"/> just-consumed bits to the stream (used by Huffman,
    /// which peeks <c>maxBits</c> but only spends the symbol's real bit length).</summary>
    public void GiveBack(int n) => _bitPos += n;

    /// <summary>True once every real bit has been consumed (only past-the-start padding remains).</summary>
    public bool Ended => _bitPos < 0;

    /// <summary>True once a read has consumed past the start of the stream — zstd's FSE termination
    /// signal (checked after a state update to flush the final symbol).</summary>
    public bool Overflowed => _overflow;
}

/// <summary>Forward bitstream reader (LSB-first within each byte, bytes in order). Used only for
/// the FSE table-description "NCount" fields, which are stored forwards.</summary>
internal sealed class ForwardBitReader
{
    private readonly byte[] _data;
    private int _bitPos;

    public ForwardBitReader(byte[] data, int startBit) { _data = data; _bitPos = startBit; }

    public int ReadBits(int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++)
        {
            int bit = 0;
            if ((_bitPos >> 3) < _data.Length)
                bit = (_data[_bitPos >> 3] >> (_bitPos & 7)) & 1;
            v |= bit << i;
            _bitPos++;
        }
        return v;
    }

    public int BitPos => _bitPos;
}

/// <summary>A built FSE decoding table (RFC 8878 §4.1). Each state yields a symbol and, to advance,
/// a baseline plus a number of freshly-read low bits.</summary>
internal sealed class FseTable
{
    public int AccuracyLog;
    public byte[] Symbol = Array.Empty<byte>();
    public byte[] NbBits = Array.Empty<byte>();
    public int[] Baseline = Array.Empty<int>();

    public int NextState(int state, ReverseBitReader br)
        => Baseline[state] + br.ReadBits(NbBits[state]);

    public static FseTable SingleSymbol(byte sym) => new()
    {
        AccuracyLog = 0,
        Symbol = new[] { sym },
        NbBits = new byte[] { 0 },
        Baseline = new[] { 0 },
    };

    public static FseTable FromNormalized(short[] norm, int accuracyLog)
    {
        int size = 1 << accuracyLog;
        var symbolTable = new byte[size];
        int highThreshold = size - 1;
        var symbolNext = new int[norm.Length];

        // Low-probability (-1) symbols occupy the top of the table in ascending symbol order
        // (RFC 8878 §4.1.1 / FSE_buildDTable): the first such symbol takes the highest slot.
        for (int s = 0; s < norm.Length; s++)
        {
            if (norm[s] == -1) { symbolTable[highThreshold--] = (byte)s; symbolNext[s] = 1; }
            else symbolNext[s] = norm[s];
        }

        int step = (size >> 1) + (size >> 3) + 3;
        int mask = size - 1;
        int pos = 0;
        for (int s = 0; s < norm.Length; s++)
        {
            int cnt = norm[s];
            for (int i = 0; i < cnt; i++)
            {
                symbolTable[pos] = (byte)s;
                pos = (pos + step) & mask;
                while (pos > highThreshold) pos = (pos + step) & mask;
            }
        }

        var symbol = new byte[size];
        var nbBits = new byte[size];
        var baseline = new int[size];
        for (int u = 0; u < size; u++)
        {
            byte sym = symbolTable[u];
            symbol[u] = sym;
            int nextState = symbolNext[sym]++;
            int nb = accuracyLog - HighBit(nextState);
            nbBits[u] = (byte)nb;
            baseline[u] = (nextState << nb) - size;
        }
        return new FseTable { AccuracyLog = accuracyLog, Symbol = symbol, NbBits = nbBits, Baseline = baseline };
    }

    /// <summary>Read an FSE table description (accuracy log + normalized counts) starting at
    /// <paramref name="p"/> (byte-aligned), advancing <paramref name="p"/> past it.</summary>
    public static FseTable ReadFromDescription(ReadOnlySpan<byte> block, ref int p, int maxLog)
    {
        var arr = block.Slice(p).ToArray();
        var fr = new ForwardBitReader(arr, 0);
        int accuracyLog = fr.ReadBits(4) + 5;
        if (accuracyLog > maxLog) throw new InvalidDataException($"FSE accuracy log {accuracyLog} exceeds max {maxLog}.");
        int tableSize = 1 << accuracyLog;

        var counts = new List<short>();
        int remaining = tableSize + 1;
        int threshold = tableSize;
        int nbBits = accuracyLog + 1;
        bool previousZero = false;

        while (remaining > 1 && counts.Count < 256)
        {
            if (previousZero)
            {
                int zeros = 0;
                int rep;
                do { rep = fr.ReadBits(2); zeros += rep; } while (rep == 3);
                for (int i = 0; i < zeros; i++) counts.Add(0);
                previousZero = false;
                continue;
            }

            int max = (2 * threshold - 1) - remaining;
            int value = fr.ReadBits(nbBits - 1);
            int count;
            if (value < max) { count = value; }
            else
            {
                int extra = fr.ReadBits(1);
                value += extra << (nbBits - 1);
                if (value >= threshold) value -= max;
                count = value;
            }

            count -= 1;                                   // proba-1 encoding: 0 stored → -1 low-prob
            remaining -= count < 0 ? -count : count;
            counts.Add((short)count);
            previousZero = count == 0;

            while (remaining < threshold) { nbBits--; threshold >>= 1; }
        }

        // advance p by the number of whole bytes consumed (round up)
        p += (fr.BitPos + 7) / 8;
        return FromNormalized(counts.ToArray(), accuracyLog);
    }

    private static int HighBit(int x)
    {
        int b = 0;
        while ((x >>= 1) != 0) b++;
        return b;
    }
}

/// <summary>A canonical Huffman decoding table for zstd literals (RFC 8878 §4.2). Built from the
/// per-symbol weights, it maps <c>maxBits</c> peeked bits to a symbol and its real bit length.</summary>
internal sealed class HuffmanTable
{
    private byte[] _symbol = Array.Empty<byte>();
    private byte[] _nbBits = Array.Empty<byte>();
    private int _maxBits;

    /// <summary>Read a Huffman tree description from the front of <paramref name="desc"/> and build
    /// the table; <paramref name="consumed"/> returns how many bytes the description occupied.</summary>
    public static HuffmanTable Read(ReadOnlySpan<byte> desc, out int consumed)
    {
        byte header = desc[0];
        int numWeights;
        var weights = new byte[256];

        if (header < 128)
        {
            // FSE-compressed weights: `header` is the compressed byte length that follows.
            int compLen = header;
            var fseDesc = desc.Slice(1, compLen);
            int q = 0;
            var wtable = FseTable.ReadFromDescription(fseDesc, ref q, 6);
            var br = new ReverseBitReader(fseDesc.Slice(q));
            numWeights = FseDecompressWeights(wtable, br, weights);
            consumed = 1 + compLen;
        }
        else
        {
            // Direct: (header-127) weights, 4 bits each, high nibble first.
            numWeights = header - 127;
            int bytes = (numWeights + 1) / 2;
            for (int i = 0; i < numWeights; i++)
            {
                byte b = desc[1 + (i / 2)];
                weights[i] = (byte)((i & 1) == 0 ? (b >> 4) : (b & 0x0F));
            }
            consumed = 1 + bytes;
        }

        // The final symbol's weight is implied so the distribution sums to a power of two.
        long weightSum = 0;
        for (int i = 0; i < numWeights; i++)
            if (weights[i] > 0) weightSum += 1L << (weights[i] - 1);
        int maxBits = HighBit((int)weightSum) + 1;
        long left = (1L << maxBits) - weightSum;
        if (left <= 0 || (left & (left - 1)) != 0)
            throw new InvalidDataException("Invalid Huffman weight distribution.");
        weights[numWeights] = (byte)(HighBit((int)left) + 1);
        int numSymbols = numWeights + 1;

        var table = new HuffmanTable { _maxBits = maxBits };
        table._symbol = new byte[1 << maxBits];
        table._nbBits = new byte[1 << maxBits];

        // Assign code ranges in ASCENDING weight order (RFC 8878 §4.2.1 / HUF_readDTableX1): weight 1
        // (the longest codes) fills the lowest table indices first, then heavier weights; within a
        // weight, by ascending symbol value. Each weight-w symbol occupies 1<<(w-1) entries.
        int offset = 0;
        for (int w = 1; w <= maxBits; w++)
        {
            for (int sym = 0; sym < numSymbols; sym++)
            {
                if (weights[sym] != w) continue;
                int nb = maxBits + 1 - w;               // real bit length for this weight
                int span = 1 << (maxBits - nb);
                for (int i = 0; i < span; i++)
                {
                    table._symbol[offset + i] = (byte)sym;
                    table._nbBits[offset + i] = (byte)nb;
                }
                offset += span;
            }
        }
        if (offset != (1 << maxBits))
            throw new InvalidDataException("Huffman table did not fill its code space.");
        return table;
    }

    public void DecodeStream(ReverseBitReader br, Span<byte> outBuf)
    {
        for (int i = 0; i < outBuf.Length; i++)
        {
            int code = br.ReadBits(_maxBits);           // peek+consume maxBits, then give back extras
            byte sym = _symbol[code];
            int nb = _nbBits[code];
            outBuf[i] = sym;
            br.GiveBack(_maxBits - nb);
        }
    }

    // FSE-decompress the Huffman weights (two interleaved states) until the bitstream is exhausted.
    private static int FseDecompressWeights(FseTable table, ReverseBitReader br, byte[] weights)
    {
        int state1 = br.ReadBits(table.AccuracyLog);
        int state2 = br.ReadBits(table.AccuracyLog);
        int n = 0;
        while (true)
        {
            // Each step emits the current state's symbol, THEN advances it; the stream is done when
            // that advance reads past the start (overflow), at which point the OTHER state holds the
            // final symbol. Checking overflow after the update (not before) is what zstd does.
            weights[n++] = table.Symbol[state1];
            state1 = table.NextState(state1, br);
            if (br.Overflowed) { weights[n++] = table.Symbol[state2]; break; }

            weights[n++] = table.Symbol[state2];
            state2 = table.NextState(state2, br);
            if (br.Overflowed) { weights[n++] = table.Symbol[state1]; break; }

            if (n >= 254) break;
        }
        return n;
    }

    private static int HighBit(int x)
    {
        int b = 0;
        while ((x >>= 1) != 0) b++;
        return b;
    }
}
