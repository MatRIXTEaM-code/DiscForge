// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.
//
// ACE decompression. The ACE container is described in Marcel Lemke's public "Technical information
// of the archiver ACE v1.2" (1998); the compression algorithms were never documented, so this
// follows the behaviour of acefile (https://github.com/droe/acefile), a from-scratch Python ACE
// decompressor validated against WinAce 2.69 and unace 2.5, used under the BSD 2-clause licence:
//
//   Copyright (c) 2017-2026, Daniel Roethlisberger and contributors. All rights reserved.
//   Redistribution and use in source and binary forms, with or without modification, are permitted
//   provided that the following conditions are met: 1. Redistributions of source code must retain the
//   above copyright notice, this list of conditions, and the following disclaimer. 2. Redistributions
//   in binary form must reproduce the above copyright notice, this list of conditions and the
//   following disclaimer in the documentation and/or other materials provided with the distribution.
//   THIS SOFTWARE IS PROVIDED BY THE AUTHOR "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING,
//   BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
//   ARE DISCLAIMED. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
//   SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
//   SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
//   CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
//   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
//   POSSIBILITY OF SUCH DAMAGE.
//
// The ACE format and its algorithms were designed by Marcel Lemke.

using System.Buffers.Binary;

namespace DiscForge.Core.Ace;

/// <summary>Thrown for any malformed, truncated or undecodable ACE data.</summary>
public sealed class AceFormatException(string message) : Exception(message);

/// <summary>
/// ACE bit reader: the stream is a sequence of little-endian 32-bit words, each read most significant
/// bit first. Up to 31 bits past the end may be peeked (they read as zero), but not consumed.
/// </summary>
internal sealed class AceBitStream
{
    private readonly Stream _in;
    private readonly byte[] _buf = new byte[1 << 16];
    private int _bufPos, _bufLen;
    private ulong _bits;        // next bits, MSB-aligned at bit 63
    private int _count;         // valid bits in _bits (real + padding)
    private int _padding;       // how many of _count are zero padding past the end
    private bool _eof;

    public AceBitStream(Stream input) { _in = input; }

    private bool LoadWord()
    {
        if (_bufPos + 4 > _bufLen)
        {
            int keep = _bufLen - _bufPos;
            if (keep > 0) Array.Copy(_buf, _bufPos, _buf, 0, keep);
            _bufLen = keep; _bufPos = 0;
            while (_bufLen < _buf.Length)
            {
                int n = _in.Read(_buf, _bufLen, _buf.Length - _bufLen);
                if (n <= 0) break;
                _bufLen += n;
            }
            if (_bufLen - _bufPos < 4)
            {
                if (_bufLen - _bufPos != 0) throw new AceFormatException("Compressed data is not a whole number of 32-bit words.");
                return false;
            }
        }
        uint w = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(_bufPos, 4));
        _bufPos += 4;
        _bits |= (ulong)w << (32 - _count);
        _count += 32;
        return true;
    }

    private void Fill(int need)
    {
        while (_count < need)
        {
            if (!_eof && LoadWord()) continue;
            _eof = true;
            // Zero padding past the end, at most 31 bits' worth may ever be peeked.
            _count += 32;
            _padding += 32;
        }
    }

    public uint Peek(int n)
    {
        if (n == 0) return 0;
        Fill(n);
        if (n > _count - _padding + 31) throw new AceFormatException("Compressed data ended early.");
        return (uint)(_bits >> (64 - n));
    }

    public void Skip(int n)
    {
        if (n == 0) return;
        Fill(n);
        if (n > _count - _padding) throw new AceFormatException("Compressed data ended early.");
        _bits <<= n;
        _count -= n;
    }

    public uint Read(int n)
    {
        if (n > 32) throw new ArgumentOutOfRangeException(nameof(n));
        if (n == 32) { uint hi = Read(16); return (hi << 16) | Read(16); }
        uint v = Peek(n);
        Skip(n);
        return v;
    }

    public int ReadGolombRice(int rBits, bool signed)
    {
        long value = rBits == 0 ? 0 : Read(rBits);
        while (Read(1) == 1)
        {
            value += 1L << rBits;
            if (value > int.MaxValue) throw new AceFormatException("Golomb-Rice value out of range.");
        }
        if (!signed) return (int)value;
        return (value & 1) != 0 ? -(int)(value >> 1) - 1 : (int)(value >> 1);
    }

    public uint ReadKnownWidthUInt(int bits)
    {
        if (bits < 2) return (uint)bits;
        bits -= 1;
        return Read(bits) + (1u << bits);
    }
}

/// <summary>A compression sub-mode switch found in the ACE 2.0 blocked stream.</summary>
internal sealed record AceMode(int Mode, int DeltaDist = 0, int DeltaLen = 0, int ExeMode = 0)
{
    public const int Lz77 = 0, Lz77Delta = 1, Lz77Exe = 2, Sound8 = 3, Sound16 = 4, Sound32A = 5, Sound32B = 6, Pic = 7;

    public static AceMode ReadFrom(AceBitStream bs)
    {
        int mode = (int)bs.Read(8);
        return mode switch
        {
            Lz77Delta => new AceMode(mode, DeltaDist: (int)bs.Read(8), DeltaLen: (int)bs.Read(17)),
            Lz77Exe => new AceMode(mode, ExeMode: (int)bs.Read(8)),
            _ => new AceMode(mode),
        };
    }
}

internal sealed class AceHuffmanTree
{
    private readonly int[] _codes;
    public readonly int[] Widths;
    private readonly int _maxWidth;

    private AceHuffmanTree(int[] codes, int[] widths, int maxWidth) { _codes = codes; Widths = widths; _maxWidth = maxWidth; }

    public int ReadSymbol(AceBitStream bs)
    {
        uint code = bs.Peek(_maxWidth);
        if (code >= (uint)_codes.Length) throw new AceFormatException("Invalid Huffman code.");
        int symbol = _codes[code];
        bs.Skip(Widths[symbol]);
        return symbol;
    }

    private const int WidthWidthBits = 3;
    private const int MaxWidthWidth = (1 << WidthWidthBits) - 1;

    /// <summary>The exact (unstable) quicksort the format's tree reconstruction depends on: descending
    /// by key, ties ordered the way this particular algorithm leaves them.</summary>
    private static void QuickSort(int[] keys, int[] values, int left, int right)
    {
        int newLeft = left, newRight = right;
        int m = keys[right];
        while (true)
        {
            while (keys[newLeft] > m) newLeft++;
            while (keys[newRight] < m) newRight--;
            if (newLeft <= newRight)
            {
                (keys[newLeft], keys[newRight]) = (keys[newRight], keys[newLeft]);
                (values[newLeft], values[newRight]) = (values[newRight], values[newLeft]);
                newLeft++; newRight--;
            }
            if (newLeft >= newRight) break;
        }
        if (left < newRight)
        {
            if (left < newRight - 1) QuickSort(keys, values, left, newRight);
            else if (keys[left] < keys[newRight])
            {
                (keys[left], keys[newRight]) = (keys[newRight], keys[left]);
                (values[left], values[newRight]) = (values[newRight], values[left]);
            }
        }
        if (right > newLeft)
        {
            if (newLeft < right - 1) QuickSort(keys, values, newLeft, right);
            else if (keys[newLeft] < keys[right])
            {
                (keys[newLeft], keys[right]) = (keys[right], keys[newLeft]);
                (values[newLeft], values[right]) = (values[right], values[newLeft]);
            }
        }
    }

    private static AceHuffmanTree Make(int[] widths, int maxWidth)
    {
        int n = widths.Length;
        var symbols = new int[n];
        for (int i = 0; i < n; i++) symbols[i] = i;
        var sortedWidths = (int[])widths.Clone();
        if (n > 0) QuickSort(sortedWidths, symbols, 0, n - 1);

        int used = 0;
        while (used < n && sortedWidths[used] != 0) used++;
        if (used < 2)
        {
            if (n == 0) throw new AceFormatException("Empty Huffman tree.");
            widths[symbols[0]] = 1;
            if (used == 0) used = 1;
        }

        int maxCodes = 1 << maxWidth;
        var codes = new List<int>(maxCodes);
        for (int k = used - 1; k >= 0; k--)
        {
            int wdt = sortedWidths[k], sym = symbols[k];
            if (wdt > maxWidth) throw new AceFormatException("Huffman code wider than allowed.");
            int repeat = 1 << (maxWidth - wdt);
            for (int r = 0; r < repeat; r++) codes.Add(sym);
            if (codes.Count > maxCodes) throw new AceFormatException("Over-subscribed Huffman tree.");
        }
        return new AceHuffmanTree(codes.ToArray(), widths, maxWidth);
    }

    public static AceHuffmanTree Read(AceBitStream bs, int maxWidth, int numCodes)
    {
        int numWidths = (int)bs.Read(9) + 1;
        if (numWidths > numCodes + 1) numWidths = numCodes + 1;
        int lowerWidth = (int)bs.Read(4);
        int upperWidth = (int)bs.Read(4);

        var widthWidths = new int[upperWidth + 1];
        for (int i = 0; i < widthWidths.Length; i++) widthWidths[i] = (int)bs.Read(WidthWidthBits);
        var widthTree = Make(widthWidths, MaxWidthWidth);

        var widths = new int[numWidths];
        int count = 0;
        while (count < numWidths)
        {
            int symbol = widthTree.ReadSymbol(bs);
            if (symbol < upperWidth) widths[count++] = symbol;
            else
            {
                int length = Math.Min((int)bs.Read(4) + 4, numWidths - count);
                count += length;   // zeros
            }
        }
        if (upperWidth > 0)
            for (int i = 1; i < widths.Length; i++) widths[i] = (widths[i] + widths[i - 1]) % upperWidth;
        for (int i = 0; i < widths.Length; i++)
            if (widths[i] > 0) widths[i] += lowerWidth;
        return Make(widths, maxWidth);
    }
}

/// <summary>The shared LZ77 history. It spans every member of an archive (a solid archive relies on
/// that) and also records bytes produced by the SOUND and PIC modes and by stored members.</summary>
internal sealed class AceDictionary
{
    private const int Size = 1 << 23;               // 8 MiB: above ACE's 4 MiB maximum
    private readonly byte[] _ring = new byte[Size];
    private long _total;

    public void Put(byte b) { _ring[(int)(_total & (Size - 1))] = b; _total++; }

    public void Put(ReadOnlySpan<byte> data) { foreach (byte b in data) Put(b); }

    /// <summary>The byte <paramref name="dist"/> positions back (1 = the last one).</summary>
    public byte Back(long dist) => _ring[(int)((_total - dist) & (Size - 1))];

    public bool CanReach(long dist) => dist >= 1 && dist <= _total && dist <= Size;
}

internal sealed class AceLz77
{
    public const int MaxCodeWidth = 11;
    private const int MaxDistAtLen2 = 255, MaxDistAtLen3 = 8191, MaxDicBits = 22;
    public const int TypeCode = 260 + MaxDicBits + 1;
    public const int NumMainCodes = 260 + MaxDicBits + 2;
    private const int NumLenCodes = 256 - 1;

    private readonly AceDictionary _dic;
    private AceHuffmanTree? _mainTree, _lenTree;
    private int _symsToRead;
    private readonly long[] _hist = new long[4];

    public AceLz77(AceDictionary dic) { _dic = dic; }

    public void Reinit()
    {
        _symsToRead = 0;
        _mainTree = _lenTree = null;
        Array.Clear(_hist);
    }

    private int ReadMain(AceBitStream bs)
    {
        if (_symsToRead == 0)
        {
            _mainTree = AceHuffmanTree.Read(bs, MaxCodeWidth, NumMainCodes);
            _lenTree = AceHuffmanTree.Read(bs, MaxCodeWidth, NumLenCodes);
            _symsToRead = (int)bs.Read(15);
        }
        _symsToRead--;
        return _mainTree!.ReadSymbol(bs);
    }

    private void HistAppend(long dist)
    {
        _hist[0] = _hist[1]; _hist[1] = _hist[2]; _hist[2] = _hist[3]; _hist[3] = dist;
    }

    private long HistRetrieve(int offset)
    {
        int idx = 3 - offset;
        long d = _hist[idx];
        for (int i = idx; i < 3; i++) _hist[i] = _hist[i + 1];
        _hist[3] = d;
        return d;
    }

    /// <summary>
    /// Decode until <paramref name="want"/> bytes have been produced or a mode switch is read. Every
    /// byte goes to the dictionary and to <paramref name="output"/>. Returns the mode switch, if any.
    /// </summary>
    public AceMode? Read(AceBitStream bs, long want, IAceSink output, out long produced)
    {
        if (want <= 0) throw new AceFormatException("LZ77 asked for nothing.");
        long have = 0;
        AceMode? next = null;
        while (have < want)
        {
            int symbol = ReadMain(bs);
            if (symbol <= 255)
            {
                _dic.Put((byte)symbol);
                output.Put((byte)symbol);
                have++;
            }
            else if (symbol < TypeCode)
            {
                long dist; int len;
                if (symbol <= 259)
                {
                    len = _lenTree!.ReadSymbol(bs);
                    int offset = symbol & 3;
                    dist = HistRetrieve(offset);
                    len += offset > 1 ? 3 : 2;
                }
                else
                {
                    dist = bs.ReadKnownWidthUInt(symbol - 260);
                    len = _lenTree!.ReadSymbol(bs);
                    HistAppend(dist);
                    len += dist <= MaxDistAtLen2 ? 2 : dist <= MaxDistAtLen3 ? 3 : 4;
                }
                dist += 1;
                if (have + len > want) throw new AceFormatException("LZ77 copy runs past the end.");
                if (!_dic.CanReach(dist)) throw new AceFormatException("LZ77 copy reaches before the start of the data.");
                for (int i = 0; i < len; i++)
                {
                    byte b = _dic.Back(dist);
                    _dic.Put(b);
                    output.Put(b);
                }
                have += len;
            }
            else if (symbol == TypeCode)
            {
                next = AceMode.ReadFrom(bs);
                break;
            }
            else throw new AceFormatException("Invalid LZ77 symbol.");
        }
        output.Flush();
        produced = have;
        return next;
    }
}

internal interface IAceSink
{
    void Put(byte b);
    void Flush();
}

/// <summary>ACE 2.0 SOUND mode: per-channel adaptive predictor over Huffman-coded residuals.</summary>
internal sealed class AceSound
{
    private const int RunLenCodes = 32;
    private const int TypeCode = 256 + RunLenCodes;
    private const int NumCodes = 256 + RunLenCodes + 1;
    private const int MaxCodeWidth = 10;
    private static readonly int[] NumChannels = { 1, 2, 3, 3 };
    private static readonly int[,] UseChannels = { { 0, 0, 0, 0 }, { 0, 1, 0, 1 }, { 0, 1, 0, 2 }, { 1, 0, 2, 0 } };
    private static readonly int[] Quantizer = BuildQuantizer();

    private static int[] BuildQuantizer()
    {
        var q = new int[256];
        for (int i = 1; i <= 128; i++)
        {
            int bl = 0; for (int v = i; v > 0; v >>= 1) bl++;
            q[i] = bl; q[256 - i] = bl;
        }
        return q;
    }

    private sealed class SymbolReader
    {
        private readonly AceHuffmanTree?[] _trees;
        private int _symsToRead;
        public SymbolReader(int models) { _trees = new AceHuffmanTree?[models]; }

        public int Read(AceBitStream bs, int model)
        {
            if (_symsToRead == 0)
            {
                for (int i = 0; i < _trees.Length; i++) _trees[i] = AceHuffmanTree.Read(bs, MaxCodeWidth, NumCodes);
                _symsToRead = (int)bs.Read(15);
            }
            _symsToRead--;
            return _trees[model]!.ReadSymbol(bs);
        }
    }

    private static int SChar(int i) => (sbyte)(byte)i;

    private sealed class Channel
    {
        private readonly SymbolReader _sr;
        private readonly int _modelBase;
        private readonly int[] _predDifCnt = new int[2], _lastPredDifCnt = new int[2];
        private readonly int[] _rarDifCnt = new int[4], _rarCoeff = new int[4], _rarDif = new int[9];
        private int _byteCount, _lastSample, _lastDelta, _adaptModelCnt, _adaptModelUse, _getState, _getCode;

        public Channel(SymbolReader sr, int idx) { _sr = sr; _modelBase = 3 * idx; }

        /// <summary>Next residual value, or null when a mode switch was read (then in <paramref name="mode"/>).</summary>
        public int? Get(AceBitStream bs, out AceMode? mode)
        {
            mode = null;
            if (_getState != 2)
            {
                int model = _getState << 1;
                if (model == 0) model += _adaptModelUse;
                model += _modelBase;
                _getCode = _sr.Read(bs, model);
                if (_getCode == TypeCode) { mode = AceMode.ReadFrom(bs); return null; }
            }
            int value = 0;
            if (_getState == 0)
            {
                if (_getCode >= RunLenCodes)
                {
                    value = _getCode - RunLenCodes;
                    _adaptModelCnt = (_adaptModelCnt * 7 >> 3) + value;
                    _adaptModelUse = _adaptModelCnt > 40 ? 1 : 0;
                }
                else _getState = 2;
            }
            else if (_getState == 1)
            {
                value = _getCode;
                _getState = 0;
            }
            if (_getState == 2)
            {
                if (_getCode == 0) _getState = 1;
                else _getCode--;
                value = 0;
            }
            return (value & 1) != 0 ? 255 - (value >> 1) : value >> 1;
        }

        private int PredictedSample() =>
            (8 * _lastSample + _rarCoeff[0] * _rarDifCnt[0] + _rarCoeff[1] * _rarDifCnt[1]
             + _rarCoeff[2] * _rarDifCnt[2] + _rarCoeff[3] * _rarDifCnt[3]) >> 3 & 0xFF;

        public int Predict() => _predDifCnt[0] > _predDifCnt[1] ? _lastSample : PredictedSample();

        public void Adjust(int sample)
        {
            _byteCount++;
            int predSample = PredictedSample();
            int predDif = SChar(predSample - sample) << 3;
            _rarDif[0] += Math.Abs(predDif - _rarDifCnt[0]);
            _rarDif[1] += Math.Abs(predDif + _rarDifCnt[0]);
            _rarDif[2] += Math.Abs(predDif - _rarDifCnt[1]);
            _rarDif[3] += Math.Abs(predDif + _rarDifCnt[1]);
            _rarDif[4] += Math.Abs(predDif - _rarDifCnt[2]);
            _rarDif[5] += Math.Abs(predDif + _rarDifCnt[2]);
            _rarDif[6] += Math.Abs(predDif - _rarDifCnt[3]);
            _rarDif[7] += Math.Abs(predDif + _rarDifCnt[3]);
            _rarDif[8] += Math.Abs(predDif);

            _lastDelta = SChar(sample - _lastSample);
            _predDifCnt[0] += Quantizer[(predDif >> 3) & 0xFF];
            _predDifCnt[1] += Quantizer[(_lastSample - sample) & 0xFF];
            _lastSample = sample;

            if ((_byteCount & 0x1F) == 0)
            {
                int minDif = 0xFFFF, minDifPos = 8;
                for (int i = 8; i >= 0; i--)
                {
                    if (_rarDif[i] <= minDif) { minDif = _rarDif[i]; minDifPos = i; }
                    _rarDif[i] = 0;
                }
                if (minDifPos != 8)
                {
                    int i = minDifPos >> 1;
                    if ((minDifPos & 1) == 0) { if (_rarCoeff[i] >= -16) _rarCoeff[i]--; }
                    else { if (_rarCoeff[i] <= 16) _rarCoeff[i]++; }
                }
                if ((_byteCount & 0xFF) == 0)
                    for (int i = 0; i < 2; i++)
                    {
                        _predDifCnt[i] -= _lastPredDifCnt[i];
                        _lastPredDifCnt[i] = _predDifCnt[i];
                    }
            }
            _rarDifCnt[3] = _rarDifCnt[2];
            _rarDifCnt[2] = _rarDifCnt[1];
            _rarDifCnt[1] = _lastDelta - _rarDifCnt[0];
            _rarDifCnt[0] = _lastDelta;
        }
    }

    private int _mode;
    private Channel[] _channels = Array.Empty<Channel>();

    public void Reinit(int mode)
    {
        _mode = mode - AceMode.Sound8;
        int n = NumChannels[_mode];
        var sr = new SymbolReader(n * 3);
        _channels = new Channel[n];
        for (int i = 0; i < n; i++) _channels[i] = new Channel(sr, i);
    }

    public AceMode? Read(AceBitStream bs, long want, List<byte> chunk)
    {
        long count = want & ~3L;
        for (long i = 0; i < count; i++)
        {
            var ch = _channels[UseChannels[_mode, (int)(i % 4)]];
            int? value = ch.Get(bs, out var mode);
            if (value is null) return mode;
            int sample = (value.Value + ch.Predict()) & 0xFF;
            chunk.Add((byte)sample);
            ch.Adjust(SChar(sample));
        }
        return null;
    }
}

/// <summary>ACE 2.0 PIC mode: two-dimensional pixel prediction with Golomb-Rice coded errors.</summary>
internal sealed class AcePic
{
    private sealed class ErrContext
    {
        public int UsedCounter, PredictorNumber, AverageCounter = 4;
        public readonly int[] ErrorCounters = new int[4];
    }

    private sealed class ErrModel
    {
        public readonly ErrContext[] Contexts = new ErrContext[365];
        public ErrModel() { for (int i = 0; i < Contexts.Length; i++) Contexts[i] = new ErrContext(); }
    }

    private static readonly int[] DifBitWidth = BuildDifBitWidth();
    private static readonly int[] Q = BuildQuantizer(1), Q9 = BuildQuantizer(9), Q81 = BuildQuantizer(81);

    private static int BitLength(int v) { int n = 0; for (; v > 0; v >>= 1) n++; return n; }

    private static int[] BuildDifBitWidth()
    {
        var t = new int[256];
        for (int i = 0; i < 128; i++) t[i] = BitLength(2 * i);
        for (int i = -128; i < 0; i++) t[256 + i] = BitLength(-2 * i - 1);
        return t;
    }

    private static int[] BuildQuantizer(int mul)
    {
        var q = new List<int>();
        for (int i = -255; i < -20; i++) q.Add(-4);
        for (int i = -20; i < -6; i++) q.Add(-3);
        for (int i = -6; i < -2; i++) q.Add(-2);
        for (int i = -2; i < 0; i++) q.Add(-1);
        q.Add(0);
        for (int i = 1; i < 3; i++) q.Add(1);
        for (int i = 3; i < 7; i++) q.Add(2);
        for (int i = 7; i < 21; i++) q.Add(3);
        for (int i = 21; i < 256; i++) q.Add(4);
        return q.Select(v => v * mul).ToArray();
    }

    private sealed class PixelDecoder
    {
        private readonly int _kind;   // 0 = plain, 1 = difference to reference plane, 2 = scaled difference
        public int A, B, C, D, X;

        public PixelDecoder(int kind)
        {
            _kind = kind;
            if (kind != 0) A = B = C = X = 128;
        }

        public void ShiftPixels() { C = A; A = D; B = X; }

        public void UpdateD(int thisD, int refD) => D = _kind switch
        {
            0 => thisD,
            1 => (128 + thisD - refD) & 0xFF,
            _ => (128 + thisD - (refD * 11 >> 4)) & 0xFF,
        };

        public int Produce(int refX) => _kind switch
        {
            0 => X,
            1 => (X + refX - 128) & 0xFF,
            _ => (X + (refX * 11 >> 4) - 128) & 0xFF,
        };

        public int Context() => Math.Abs(Q81[255 + D - A] + Q9[255 + A - C] + Q[255 + C - B]);

        private int Predict(int p) => p switch
        {
            0 => A,
            1 => B,
            2 => (A + B) >> 1,
            _ => (A + B - C) & 0xFF,
        };

        public void UpdateX(AceBitStream bs, ErrContext ctx)
        {
            ctx.UsedCounter++;
            int r = ctx.AverageCounter / ctx.UsedCounter;
            int epsilon = bs.ReadGolombRice(BitLength(r), signed: true);
            int predicted = Predict(ctx.PredictorNumber);
            X = (predicted + epsilon) & 0xFF;

            ctx.AverageCounter += Math.Abs(epsilon);
            if (ctx.UsedCounter == 128) { ctx.UsedCounter >>= 1; ctx.AverageCounter >>= 1; }

            int best = 0;
            for (int i = 0; i < 4; i++)
            {
                ctx.ErrorCounters[i] += DifBitWidth[(X - Predict(i)) & 0xFF];
                if (i == 0 || ctx.ErrorCounters[i] < ctx.ErrorCounters[best]) best = i;
            }
            ctx.PredictorNumber = best;
            if (ctx.ErrorCounters.Any(ec => ec > 0x7F))
                for (int i = 0; i < 4; i++) ctx.ErrorCounters[i] >>= 1;
        }
    }

    private int _width, _planes;
    private ErrModel _plane0 = new(), _planeN = new();
    private int[] _prevRow = Array.Empty<int>();
    private readonly List<byte> _leftover = new();

    public void Reinit(AceBitStream bs)
    {
        _width = bs.ReadGolombRice(12, signed: false);
        _planes = bs.ReadGolombRice(2, signed: false);
        if (_width < 0 || _planes < 0 || _width > 1 << 24 || _planes > 1 << 16) throw new AceFormatException("Invalid PIC dimensions.");
        _plane0 = new ErrModel();
        _planeN = new ErrModel();
        _prevRow = new int[_width + _planes];
        _leftover.Clear();
    }

    private static int At(int[] a, int i) => a[i < 0 ? i + a.Length : i];

    private int[] Row(AceBitStream bs)
    {
        var row = new int[_width + _planes];
        for (int plane = 0; plane < _planes; plane++)
        {
            ErrModel model;
            PixelDecoder dec;
            if (plane == 0) { model = _plane0; dec = new PixelDecoder(0); }
            else
            {
                model = _planeN;
                int kind = (int)bs.Read(2);
                if (kind > 2) throw new AceFormatException("Unknown PIC pixel decoder.");
                dec = new PixelDecoder(kind);
            }
            dec.UpdateD(At(_prevRow, plane), At(_prevRow, plane - 1));
            for (int col = plane; col < _width; col += _planes)
            {
                dec.ShiftPixels();
                dec.UpdateD(At(_prevRow, col + _planes), At(_prevRow, col + _planes - 1));
                var ctx = model.Contexts[dec.Context()];
                dec.UpdateX(bs, ctx);
                row[col] = dec.Produce(At(row, col - 1));
            }
        }
        _prevRow = row;
        return row;
    }

    public AceMode? Read(AceBitStream bs, long want, List<byte> chunk)
    {
        AceMode? next = null;
        if (_leftover.Count > 0) { chunk.AddRange(_leftover); _leftover.Clear(); }
        while (chunk.Count < want)
        {
            if (bs.Read(1) == 0) { next = AceMode.ReadFrom(bs); break; }
            var data = Row(bs);
            long n = Math.Min(want - chunk.Count, _width);
            for (int i = 0; i < n; i++) chunk.Add((byte)data[i]);
            for (int i = (int)n; i < _width; i++) _leftover.Add((byte)data[i]);
        }
        return next;
    }
}

/// <summary>
/// The per-archive ACE decompressor. One instance is used for all members of an archive in order,
/// because the LZ77 history carries over from member to member (required for solid archives).
/// </summary>
public sealed class AceDecompressor
{
    public const int CompStored = 0, CompLz77 = 1, CompBlocked = 2;

    private readonly AceDictionary _dic = new();
    private readonly AceLz77 _lz77;
    private readonly AceSound _sound = new();
    private readonly AcePic _pic = new();

    public AceDecompressor() { _lz77 = new AceLz77(_dic); }

    /// <summary>Decompress one member from <paramref name="packed"/> (already decrypted) to
    /// <paramref name="output"/>. Returns the ACE CRC-32 of what was written.</summary>
    public uint Decompress(int compType, Stream packed, long size, Stream output)
    {
        var crc = new AceCrc32();
        var sink = new StreamSink(output, crc);
        switch (compType)
        {
            case CompStored: DecompressStored(packed, size, sink); break;
            case CompLz77: DecompressLz77(packed, size, sink); break;
            case CompBlocked: DecompressBlocked(packed, size, sink); break;
            default: throw new AceFormatException($"Unknown compression method {compType}.");
        }
        sink.Flush();
        if (sink.Written != size) throw new AceFormatException($"Decompressed {sink.Written:N0} of {size:N0} bytes.");
        return crc.Value;
    }

    private void DecompressStored(Stream packed, long size, StreamSink sink)
    {
        var buf = new byte[1 << 16];
        long done = 0;
        while (done < size)
        {
            int n = packed.Read(buf, 0, (int)Math.Min(buf.Length, size - done));
            if (n <= 0) throw new AceFormatException("Stored member is truncated.");
            _dic.Put(buf.AsSpan(0, n));
            sink.Write(buf.AsSpan(0, n));
            done += n;
        }
    }

    private void DecompressLz77(Stream packed, long size, StreamSink sink)
    {
        if (size == 0) return;
        _lz77.Reinit();
        var bs = new AceBitStream(packed);
        var mode = _lz77.Read(bs, size, sink, out _);
        if (mode is not null) throw new AceFormatException("Mode switch inside an ACE 1.0 LZ77 stream.");
    }

    private void DecompressBlocked(Stream packed, long size, StreamSink sink)
    {
        if (size == 0) return;
        var bs = new AceBitStream(packed);
        _lz77.Reinit();
        var exe = new ExeFilter(sink);
        int lastDelta = 0;
        AceMode? next = null;
        var mode = new AceMode(AceMode.Lz77);
        var chunk = new List<byte>();

        while (sink.Written < size)
        {
            if (next is not null)
            {
                if (mode.Mode != next.Mode)
                {
                    if (next.Mode is >= AceMode.Sound8 and <= AceMode.Sound32B) _sound.Reinit(next.Mode);
                    else if (next.Mode == AceMode.Pic) _pic.Reinit(bs);
                }
                mode = next;
                next = null;
            }
            long before = sink.Written;
            switch (mode.Mode)
            {
                case AceMode.Lz77Delta:
                {
                    if (mode.DeltaDist == 0 || mode.DeltaLen == 0) throw new AceFormatException("Invalid DELTA parameters.");
                    var collect = new ListSink();
                    while (collect.Data.Count < mode.DeltaLen)
                    {
                        var nm = _lz77.Read(bs, mode.DeltaLen - collect.Data.Count, collect, out _);
                        if (nm is not null)
                        {
                            if (next is not null) throw new AceFormatException("DELTA block switched mode twice.");
                            next = nm;
                            if (collect.Data.Count == 0) break;
                        }
                    }
                    var delta = collect.Data;
                    if (delta.Count == 0 && next is not null) continue;
                    for (int i = 0; i < delta.Count; i++)
                    {
                        delta[i] = (byte)(delta[i] + lastDelta);
                        lastDelta = delta[i];
                    }
                    int planeSize = mode.DeltaLen / mode.DeltaDist;
                    for (int pos = 0; pos < planeSize; pos++)
                        for (int plane = 0; plane < mode.DeltaLen; plane += planeSize)
                        {
                            int idx = plane + pos;
                            if (idx >= delta.Count) throw new AceFormatException("DELTA block is shorter than declared.");
                            sink.Put(delta[idx]);
                        }
                    sink.Flush();
                    break;
                }
                case AceMode.Lz77:
                case AceMode.Lz77Exe:
                {
                    long want = size - sink.Written - exe.Leftover.Count;
                    if (want <= 0)
                    {
                        // Only a held-back instruction tail is left: emit it as it stands.
                        exe.FlushLeftoverRaw();
                        break;
                    }
                    if (mode.Mode == AceMode.Lz77)
                    {
                        exe.FlushLeftoverRaw();
                        next = _lz77.Read(bs, want, sink, out _);
                    }
                    else
                    {
                        exe.BeginSegment(sink.Written, mode.ExeMode);
                        next = _lz77.Read(bs, want, exe, out _);
                        exe.EndSegment();
                    }
                    break;
                }
                case AceMode.Sound8:
                case AceMode.Sound16:
                case AceMode.Sound32A:
                case AceMode.Sound32B:
                {
                    chunk.Clear();
                    next = _sound.Read(bs, size - sink.Written, chunk);
                    var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chunk);
                    _dic.Put(span);
                    sink.Write(span);
                    break;
                }
                case AceMode.Pic:
                {
                    chunk.Clear();
                    next = _pic.Read(bs, size - sink.Written, chunk);
                    var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chunk);
                    _dic.Put(span);
                    sink.Write(span);
                    break;
                }
                default:
                    throw new AceFormatException($"Unknown ACE compression mode {mode.Mode}.");
            }
            if (sink.Written == before && next is null && exe.Leftover.Count == 0)
                throw new AceFormatException("ACE stream stopped making progress.");
        }
    }

    // ---- output plumbing ---------------------------------------------------------------------

    private sealed class StreamSink : IAceSink
    {
        private readonly Stream _out;
        private readonly AceCrc32 _crc;
        private readonly byte[] _buf = new byte[1 << 16];
        private int _n;
        private long _flushed;

        public StreamSink(Stream output, AceCrc32 crc) { _out = output; _crc = crc; }

        public long Written => _flushed + _n;

        public void Put(byte b)
        {
            _buf[_n++] = b;
            if (_n == _buf.Length) Flush();
        }

        public void Write(ReadOnlySpan<byte> data) { foreach (byte b in data) Put(b); }

        public void Flush()
        {
            if (_n == 0) return;
            _crc.Update(_buf.AsSpan(0, _n));
            _out.Write(_buf, 0, _n);
            _flushed += _n;
            _n = 0;
        }
    }

    private sealed class ListSink : IAceSink
    {
        public readonly List<byte> Data = new();
        public void Put(byte b) => Data.Add(b);
        public void Flush() { }
    }

    /// <summary>
    /// The EXE preprocessor's inverse: E8 (CALL) and E9 (JMP) operands were stored as absolute
    /// addresses and are turned back into relative ones. Streaming, with the exact end-of-segment rule
    /// of the reference decoder: the final four bytes of a segment are never rewritten, and an opcode
    /// found there (after the first of them) is held back and prefixed to the next LZ77 segment.
    /// </summary>
    private sealed class ExeFilter : IAceSink
    {
        private readonly StreamSink _out;
        private readonly List<byte> _pend = new();
        public List<byte> Leftover { get; } = new();
        private long _segStart;
        private int _exeMode;

        public ExeFilter(StreamSink output) { _out = output; }

        public void FlushLeftoverRaw()
        {
            foreach (byte b in Leftover) _out.Put(b);
            Leftover.Clear();
            _out.Flush();
        }

        public void BeginSegment(long written, int exeMode)
        {
            _segStart = written;       // absolute position of the segment's first byte (incl. leftover)
            _exeMode = exeMode;
            _pend.Clear();
            _pend.AddRange(Leftover);
            Leftover.Clear();
            Process();
        }

        public void Put(byte b)
        {
            _pend.Add(b);
            if (_pend.Count >= 4096) Process();
        }

        public void Flush() => Process();


        /// <summary>Rewrite and emit everything that has at least four bytes after it.</summary>
        private void Process()
        {
            int i = 0;
            while (i + 4 < _pend.Count)
            {
                byte op = _pend[i];
                if (op == 0xE8 && _exeMode != 0) { Rewrite32(i); i += 5; }
                else if (op == 0xE8 || op == 0xE9) { Rewrite16(i); i += 3; }
                else i++;
            }
            if (i > 0)
            {
                for (int k = 0; k < i; k++) _out.Put(_pend[k]);
                _pend.RemoveRange(0, i);
                _segStart += i;
            }
        }

        private void Rewrite16(int i)
        {
            long pos = _segStart + i;
            int rel = (_pend[i + 1] | (_pend[i + 2] << 8)) - (int)(pos & 0xFFFF);
            rel &= 0xFFFF;
            _pend[i + 1] = (byte)rel;
            _pend[i + 2] = (byte)(rel >> 8);
        }

        private void Rewrite32(int i)
        {
            long pos = _segStart + i;
            uint v = (uint)(_pend[i + 1] | (_pend[i + 2] << 8) | (_pend[i + 3] << 16) | (_pend[i + 4] << 24));
            uint rel = unchecked(v - (uint)pos);
            _pend[i + 1] = (byte)rel;
            _pend[i + 2] = (byte)(rel >> 8);
            _pend[i + 3] = (byte)(rel >> 16);
            _pend[i + 4] = (byte)(rel >> 24);
        }

        public void EndSegment()
        {
            Process();
            // What remains is the segment's tail: at most four bytes, none rewritten. The first of them is
            // passed through; an opcode at any later position starts the held-back leftover.
            int cut = _pend.Count;
            for (int k = 1; k < _pend.Count; k++)
                if (_pend[k] == 0xE8 || _pend[k] == 0xE9) { cut = k; break; }
            for (int k = 0; k < cut; k++) _out.Put(_pend[k]);
            for (int k = cut; k < _pend.Count; k++) Leftover.Add(_pend[k]);
            _pend.Clear();
            _out.Flush();
        }
    }
}

/// <summary>ACE's CRC-32: the standard polynomial and initial value, but the result is not inverted.
/// Header checks use its low 16 bits.</summary>
public sealed class AceCrc32
{
    private static readonly uint[] Table = BuildTable();
    private uint _state = 0xFFFFFFFF;

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i;
            for (int j = 0; j < 8; j++) r = (r & 1) != 0 ? (r >> 1) ^ 0xEDB88320u : r >> 1;
            t[i] = r;
        }
        return t;
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        uint c = _state;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        _state = c;
    }

    public uint Value => _state;

    public static uint Compute(ReadOnlySpan<byte> data) { var c = new AceCrc32(); c.Update(data); return c.Value; }
}
