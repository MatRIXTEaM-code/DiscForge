// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.
//
// Decompressors for the DOS-era LHA/LArc, ARJ and ZOO formats.
//
// -lzs-, -lz5- and the LHA extension header handling follow Lhasa by Simon Howard, used under the
// ISC licence:
//
//   Copyright (c) 2011, 2012, Simon Howard
//   Permission to use, copy, modify, and/or distribute this software for any purpose with or without
//   fee is hereby granted, provided that the above copyright notice and this permission notice appear
//   in all copies. THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
//   REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO
//   EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR
//   ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF
//   CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
//   PERFORMANCE OF THIS SOFTWARE.
//
// The static-Huffman LZ coder shared by LHA -lh4-..-lh7-/-lhx-, ARJ methods 1-3 and ZOO's -lh5-
// variant is Haruhiko Okumura's published "ar002" design; -lh1- is Okumura and Yoshizaki's LZHUF
// adaptive-Huffman design. Both are written here from their public descriptions. ARJ's method 4 and
// the ARJ container follow the "ARJ TECHNICAL INFORMATION" note (Robert Jung, 1993) and the behaviour
// of archives made by the ARJ archiver; ZOO follows Rahul Dhesi's published zoo format description.

using System.Buffers.Binary;

namespace DiscForge.Core.OldArchives;

/// <summary>Reads bits most-significant first from a byte stream; zeros past the end.</summary>
internal sealed class MsbBitReader
{
    private readonly Stream _in;
    private readonly byte[] _buf = new byte[1 << 16];
    private int _pos, _len;
    private ulong _bits;   // left-aligned
    private int _count;
    private int _overrun;  // bytes of zero padding supplied past the end

    public MsbBitReader(Stream input) { _in = input; }

    private int NextByte()
    {
        if (_pos == _len)
        {
            _len = _in.Read(_buf, 0, _buf.Length);
            _pos = 0;
            if (_len <= 0)
            {
                _len = 0;
                if (++_overrun > 64) throw new OldArchiveException("Compressed data ended early.");
                return 0;
            }
        }
        return _buf[_pos++];
    }

    private void Fill(int n)
    {
        while (_count < n)
        {
            _bits |= (ulong)NextByte() << (56 - _count);
            _count += 8;
        }
    }

    public int Peek(int n)
    {
        if (n == 0) return 0;
        Fill(n);
        return (int)(_bits >> (64 - n));
    }

    public void Skip(int n)
    {
        if (n == 0) return;
        Fill(n);
        _bits <<= n;
        _count -= n;
    }

    public int Read(int n)
    {
        if (n > 24) { int hi = Read(n - 16); return (hi << 16) | Read(16); }
        int v = Peek(n);
        Skip(n);
        return v;
    }

    public int Bit() => Read(1);

    public int ReadByteAligned() => Read(8);
}

/// <summary>Reads bits least-significant first (ZOO's LZW).</summary>
internal sealed class LsbBitReader
{
    private readonly Stream _in;
    private ulong _bits;
    private int _count;
    private int _overrun;

    public LsbBitReader(Stream input) { _in = input; }

    public int Read(int n)
    {
        while (_count < n)
        {
            int b = _in.ReadByte();
            if (b < 0)
            {
                if (++_overrun > 8) throw new OldArchiveException("Compressed data ended early.");
                b = 0;
            }
            _bits |= (ulong)b << _count;
            _count += 8;
        }
        int v = (int)(_bits & ((1UL << n) - 1));
        _bits >>= n;
        _count -= n;
        return v;
    }
}

/// <summary>Output history plus a buffered writer.</summary>
internal sealed class RingOutput
{
    private readonly byte[] _ring;
    private readonly int _mask;
    private int _pos;
    private readonly Stream _out;
    private readonly byte[] _buf = new byte[1 << 16];
    private int _n;
    public long Written;

    public RingOutput(int ringBits, Stream output, byte fill = 0, int startPos = 0)
    {
        _ring = new byte[1 << ringBits];
        if (fill != 0) Array.Fill(_ring, fill);
        _mask = _ring.Length - 1;
        _pos = startPos & _mask;
        _out = output;
    }

    public byte[] Ring => _ring;
    public int Position { get => _pos; set => _pos = value & _mask; }

    public void Put(byte b)
    {
        _ring[_pos] = b;
        _pos = (_pos + 1) & _mask;
        _buf[_n++] = b;
        Written++;
        if (_n == _buf.Length) Flush();
    }

    /// <summary>Copy <paramref name="len"/> bytes starting <paramref name="distance"/> bytes back.</summary>
    public void CopyBack(int distance, int len)
    {
        int src = (_pos - distance) & _mask;
        for (int i = 0; i < len; i++)
        {
            Put(_ring[src]);
            src = (src + 1) & _mask;
        }
    }

    /// <summary>Copy from an absolute ring position (LArc style).</summary>
    public void CopyFrom(int start, int len)
    {
        for (int i = 0; i < len; i++) Put(_ring[(start + i) & _mask]);
    }

    public void Flush()
    {
        if (_n == 0) return;
        _out.Write(_buf, 0, _n);
        _n = 0;
    }
}

/// <summary>A canonical Huffman code (shortest codes first, then by symbol) decoded by table lookup.</summary>
internal sealed class CanonicalCode
{
    private int[] _table = Array.Empty<int>();   // (symbol << 5) | length
    private int _bits;
    private int _single = -1;

    public void SetSingle(int symbol) { _single = symbol; _bits = 0; }

    public void Build(ReadOnlySpan<byte> lengths)
    {
        _single = -1;
        int max = 0;
        foreach (byte l in lengths) if (l > max) max = l;
        if (max == 0) { _single = 0; return; }
        if (max > 24) throw new OldArchiveException("Huffman code too long.");
        _bits = max;
        _table = new int[1 << max];
        Array.Fill(_table, -1);
        int code = 0;
        for (int len = 1; len <= max; len++)
        {
            for (int sym = 0; sym < lengths.Length; sym++)
            {
                if (lengths[sym] != len) continue;
                int span = 1 << (max - len);
                int start = code << (max - len);
                if (start + span > _table.Length) throw new OldArchiveException("Over-subscribed Huffman code.");
                for (int k = 0; k < span; k++) _table[start + k] = (sym << 5) | len;
                code++;
            }
            code <<= 1;
        }
    }

    public int Read(MsbBitReader br)
    {
        if (_single >= 0) return _single;
        int e = _table[br.Peek(_bits)];
        if (e < 0) throw new OldArchiveException("Invalid Huffman code in compressed data.");
        br.Skip(e & 31);
        return e >> 5;
    }
}

/// <summary>
/// The static-Huffman LZ77 coder of LHA -lh4-/-lh5-/-lh6-/-lh7-/-lhx-, ARJ methods 1-3 and ZOO -lh5-:
/// blocks of (16-bit symbol count, pre-code, literal/length code, distance code), 510 literal/length
/// symbols, match length = symbol - 253.
/// </summary>
internal static class StaticHuffmanLz
{
    private const int NC = 510, NT = 19, TBIT = 5, CBIT = 9;

    /// <param name="ringBits">log2 of the history kept.</param>
    /// <param name="pbit">bits used to send the distance-code count.</param>
    /// <param name="fill">initial history contents (LHA uses spaces).</param>
    /// <param name="lhark">LHARK 0.4's variant: 289 symbols, extended length and distance codes.</param>
    public static void Decode(Stream packed, long size, Stream output, int ringBits, int pbit, byte fill, bool lhark = false)
    {
        int nc = lhark ? 289 : NC;
        var br = new MsbBitReader(packed);
        var ring = new RingOutput(ringBits, output, fill);
        var pt = new CanonicalCode();
        var c = new CanonicalCode();
        var p = new CanonicalCode();
        int np = (1 << pbit) - 1;
        var lens = new byte[NC];
        var small = new byte[64];
        int blockLeft = 0;
        while (ring.Written < size)
        {
            if (blockLeft == 0)
            {
                blockLeft = br.Read(16);
                ReadPtLen(br, pt, small, NT, TBIT, 3);
                ReadCLen(br, c, pt, lens, nc);
                ReadPtLen(br, p, small, np, pbit, -1);
                if (blockLeft == 0) continue;
            }
            blockLeft--;
            int sym = c.Read(br);
            if (sym < 256) ring.Put((byte)sym);
            else
            {
                int len, d;
                if (!lhark)
                {
                    len = sym - 253;
                    d = p.Read(br);
                    if (d > 1) d = (1 << (d - 1)) + br.Read(d - 1);
                }
                else
                {
                    if (sym < 264) len = sym - 253;
                    else if (sym < 288) { int nb = (sym - 260) / 4; len = ((4 + sym % 4) << nb) + br.Read(nb) + 3; }
                    else len = 514;
                    d = p.Read(br);
                    if (d >= 4) { int nb = (d - 2) / 2; d = ((2 + d % 2) << nb) + br.Read(nb); }
                }
                if (ring.Written + len > size) len = (int)(size - ring.Written);
                ring.CopyBack(d + 1, len);
            }
        }
        ring.Flush();
    }

    private static void ReadPtLen(MsbBitReader br, CanonicalCode code, byte[] lens, int nn, int nbit, int special)
    {
        int n = br.Read(nbit);
        if (n == 0)
        {
            code.SetSingle(br.Read(nbit));
            return;
        }
        if (n > nn) n = nn;
        Array.Clear(lens);
        int i = 0;
        while (i < n)
        {
            int len = br.Read(3);
            if (len == 7)
                while (br.Bit() == 1)
                {
                    if (++len > 24) throw new OldArchiveException("Invalid code length.");
                }
            lens[i++] = (byte)len;
            if (i == special)
            {
                int z = br.Read(2);
                while (z-- > 0 && i < lens.Length) lens[i++] = 0;
            }
        }
        code.Build(lens.AsSpan(0, Math.Max(n, i)));
    }

    private static void ReadCLen(MsbBitReader br, CanonicalCode code, CanonicalCode pt, byte[] lens, int nc)
    {
        int n = br.Read(CBIT);
        if (n == 0)
        {
            code.SetSingle(br.Read(CBIT));
            return;
        }
        if (n > nc) n = nc;
        Array.Clear(lens);
        int i = 0;
        while (i < n)
        {
            int c = pt.Read(br);
            if (c <= 2)
            {
                int run = c == 0 ? 1 : c == 1 ? br.Read(4) + 3 : br.Read(CBIT) + 20;
                while (run-- > 0 && i < n) lens[i++] = 0;
            }
            else lens[i++] = (byte)(c - 2);
        }
        code.Build(lens.AsSpan(0, n));
    }
}

/// <summary>LHarc 1.x -lh1-: 4 KB LZSS with an adaptive Huffman code for literals and lengths (LZHUF).</summary>
internal static class Lh1Decoder
{
    private const int N = 4096, F = 60, Threshold = 2;
    private const int NChar = 256 - Threshold + F;   // 314
    private const int T = NChar * 2 - 1;             // 627
    private const int R = T - 1;
    private const int MaxFreq = 0x8000;

    private static readonly byte[] DCode = new byte[256];
    private static readonly byte[] DLen = new byte[256];

    static Lh1Decoder()
    {
        // Upper six bits of the distance: 1 code of 3 bits, 3 of 4, 8 of 5, 12 of 6, 24 of 7, 16 of 8.
        int[] counts = { 1, 3, 8, 12, 24, 16 };
        int code = 0, value = 0;
        for (int k = 0; k < counts.Length; k++)
        {
            int len = k + 3;
            int span = 1 << (8 - len);
            for (int j = 0; j < counts[k]; j++)
            {
                for (int s = 0; s < span; s++) { DCode[code + s] = (byte)value; DLen[code + s] = (byte)len; }
                code += span;
                value++;
            }
        }
    }

    public static void Decode(Stream packed, long size, Stream output)
    {
        var br = new MsbBitReader(packed);
        var ring = new RingOutput(12, output, (byte)' ', N - F);
        var freq = new int[T + 1];
        var prnt = new int[T + NChar];
        var son = new int[T];

        for (int i = 0; i < NChar; i++) { freq[i] = 1; son[i] = i + T; prnt[i + T] = i; }
        for (int i = 0, j = NChar; j <= R; i += 2, j++)
        {
            freq[j] = freq[i] + freq[i + 1];
            son[j] = i;
            prnt[i] = prnt[i + 1] = j;
        }
        freq[T] = 0xFFFF;
        prnt[R] = 0;

        while (ring.Written < size)
        {
            int c = son[R];
            while (c < T) c = son[c + br.Bit()];
            c -= T;
            Update(c, freq, prnt, son);
            if (c < 256) ring.Put((byte)c);
            else
            {
                int i = br.Read(8);
                int hi = DCode[i] << 6;
                int extra = DLen[i] - 2;
                while (extra-- > 0) i = (i << 1) | br.Bit();
                int pos = hi | (i & 0x3F);
                int len = c - 255 + Threshold;
                if (ring.Written + len > size) len = (int)(size - ring.Written);
                ring.CopyBack(pos + 1, len);
            }
        }
        ring.Flush();
    }

    private static void Reconstruct(int[] freq, int[] prnt, int[] son)
    {
        int j = 0;
        for (int i = 0; i < T; i++)
            if (son[i] >= T) { freq[j] = (freq[i] + 1) / 2; son[j] = son[i]; j++; }
        for (int i = 0, k2 = NChar; k2 < T; i += 2, k2++)
        {
            int f = freq[k2] = freq[i] + freq[i + 1];
            int k = k2 - 1;
            while (f < freq[k]) k--;
            k++;
            int l = k2 - k;
            Array.Copy(freq, k, freq, k + 1, l);
            freq[k] = f;
            Array.Copy(son, k, son, k + 1, l);
            son[k] = i;
        }
        for (int i = 0; i < T; i++)
        {
            int k = son[i];
            if (k >= T) prnt[k] = i;
            else prnt[k] = prnt[k + 1] = i;
        }
    }

    private static void Update(int c, int[] freq, int[] prnt, int[] son)
    {
        if (freq[R] == MaxFreq) Reconstruct(freq, prnt, son);
        c = prnt[c + T];
        do
        {
            int k = ++freq[c];
            int l = c + 1;
            if (k > freq[l])
            {
                while (k > freq[++l]) { }
                l--;
                freq[c] = freq[l];
                freq[l] = k;
                int i = son[c];
                prnt[i] = l;
                if (i < T) prnt[i + 1] = l;
                int j = son[l];
                son[l] = i;
                prnt[j] = c;
                if (j < T) prnt[j + 1] = c;
                son[c] = j;
                c = l;
            }
        } while ((c = prnt[c]) != 0);
    }
}

/// <summary>LArc -lzs- (2 KB window, flag bit per item) and -lz5- (4 KB window, flag byte per 8 items).</summary>
internal static class LarcDecoders
{
    public static void DecodeLzs(Stream packed, long size, Stream output)
    {
        var br = new MsbBitReader(packed);
        var ring = new RingOutput(11, output, (byte)' ', 2048 - 17);
        while (ring.Written < size)
        {
            if (br.Bit() == 1) ring.Put((byte)br.Read(8));
            else
            {
                int pos = br.Read(11);
                int len = br.Read(4) + 2;
                if (ring.Written + len > size) len = (int)(size - ring.Written);
                ring.CopyFrom(pos, len);
            }
        }
        ring.Flush();
    }

    public static void DecodeLz5(Stream packed, long size, Stream output)
    {
        var ring = new RingOutput(12, output, 0, 4096 - 18);
        // Initial history, as LArc sets it up.
        var h = ring.Ring;
        int p = 0;
        for (int i = 0; i < 256; i++) for (int j = 0; j < 13; j++) h[p++] = (byte)i;
        for (int i = 0; i < 256; i++) h[p++] = (byte)i;
        for (int i = 0; i < 256; i++) h[p++] = (byte)(255 - i);
        for (int i = 0; i < 128; i++) h[p++] = 0;
        for (int i = 0; i < 110; i++) h[p++] = (byte)' ';
        for (int i = 0; i < 18; i++) h[p++] = 0;

        int Next()
        {
            int b = packed.ReadByte();
            if (b < 0) throw new OldArchiveException("Compressed data ended early.");
            return b;
        }

        while (ring.Written < size)
        {
            int flags = Next();
            for (int bit = 0; bit < 8 && ring.Written < size; bit++)
            {
                if ((flags & (1 << bit)) != 0) ring.Put((byte)Next());
                else
                {
                    int lo = Next(), hi = Next();
                    int pos = ((hi & 0xF0) << 4) | lo;
                    int len = (hi & 0x0F) + 3;
                    if (ring.Written + len > size) len = (int)(size - ring.Written);
                    ring.CopyFrom(pos, len);
                }
            }
        }
        ring.Flush();
    }
}

/// <summary>ARJ method 4 ("fastest"): LZ77 with unary-prefixed lengths and distances.</summary>
internal static class ArjFastDecoder
{
    public static void Decode(Stream packed, long size, Stream output)
    {
        var br = new MsbBitReader(packed);
        var ring = new RingOutput(15, output);
        while (ring.Written < size)
        {
            int len = ReadPrefixed(br, 0, 7);
            if (len == 0) ring.Put((byte)br.Read(8));
            else
            {
                len += 2;
                int dist = ReadPrefixed(br, 9, 13);
                if (ring.Written + len > size) len = (int)(size - ring.Written);
                ring.CopyBack(dist + 1, len);
            }
        }
        ring.Flush();
    }

    private static int ReadPrefixed(MsbBitReader br, int start, int stop)
    {
        int plus = 0, pwr = 1 << start, width;
        for (width = start; width < stop; width++)
        {
            if (br.Bit() == 0) break;
            plus += pwr;
            pwr <<= 1;
        }
        return (width != 0 ? br.Read(width) : 0) + plus;
    }
}

/// <summary>ZOO method 1: LZW, 9 to 13-bit codes (LSB first), 256 = clear, 257 = end.</summary>
internal static class ZooLzwDecoder
{
    private const int MaxBits = 13, Clear = 256, End = 257, First = 258;

    public static void Decode(Stream packed, long size, Stream output)
    {
        var br = new LsbBitReader(packed);
        var prefix = new int[1 << MaxBits];
        var suffix = new byte[1 << MaxBits];
        var stack = new byte[1 << MaxBits];
        var buf = new BufferedStream(output, 1 << 16);
        long written = 0;
        int nbits = 9, maxCode = 512, free = First;
        int oldCode = -1;
        byte finChar = 0;
        while (written < size)
        {
            int code = br.Read(nbits);
            if (code == End) break;
            if (code == Clear)
            {
                nbits = 9; maxCode = 512; free = First;
                code = br.Read(nbits);
                if (code == End) break;
                if (code > 255) throw new OldArchiveException("Bad LZW data.");
                oldCode = code;
                finChar = (byte)code;
                buf.WriteByte(finChar);
                written++;
                continue;
            }
            if (oldCode < 0)
            {
                if (code > 255) throw new OldArchiveException("Bad LZW data.");
                oldCode = code; finChar = (byte)code;
                buf.WriteByte(finChar); written++;
                continue;
            }
            int inCode = code;
            int sp = 0;
            if (code >= free)
            {
                if (code > free) throw new OldArchiveException("Bad LZW code.");
                stack[sp++] = finChar;
                code = oldCode;
            }
            while (code > 255)
            {
                if (sp >= stack.Length) throw new OldArchiveException("Bad LZW chain.");
                stack[sp++] = suffix[code];
                code = prefix[code];
            }
            finChar = (byte)code;
            stack[sp++] = finChar;
            while (sp > 0 && written < size) { buf.WriteByte(stack[--sp]); written++; }
            if (free < (1 << MaxBits))
            {
                prefix[free] = oldCode;
                suffix[free] = finChar;
                free++;
                if (free >= maxCode && nbits < MaxBits) { nbits++; maxCode <<= 1; }
            }
            oldCode = inCode;
        }
        buf.Flush();
    }
}

/// <summary>CRC-16/ARC (poly 0xA001 reflected, init 0) — LHA and ZOO.</summary>
internal static class Crc16Arc
{
    private static readonly ushort[] Table = Build();

    private static ushort[] Build()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int r = i;
            for (int j = 0; j < 8; j++) r = (r & 1) != 0 ? (r >> 1) ^ 0xA001 : r >> 1;
            t[i] = (ushort)r;
        }
        return t;
    }

    public static ushort Update(ushort crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = (ushort)(Table[(crc ^ b) & 0xFF] ^ (crc >> 8));
        return crc;
    }
}

/// <summary>Standard CRC-32 (ARJ).</summary>
internal static class Crc32Std
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
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

    /// <summary>Running value (pre-inversion); start with 0xFFFFFFFF and invert at the end.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFF, data);
}

/// <summary>Pass-through write stream that counts bytes and computes a checksum.</summary>
internal sealed class ChecksumStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _crc32;
    private uint _c32 = 0xFFFFFFFF;
    private ushort _c16;
    public long Count { get; private set; }

    public ChecksumStream(Stream inner, bool crc32) { _inner = inner; _crc32 = crc32; }

    public uint Crc => _crc32 ? ~_c32 : _c16;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_crc32) _c32 = Crc32Std.Update(_c32, buffer);
        else _c16 = Crc16Arc.Update(_c16, buffer);
        Count += buffer.Length;
        _inner.Write(buffer);
    }

    public override void WriteByte(byte value) => Write(new[] { value });
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Count;
    public override long Position { get => Count; set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Read-only window onto part of a file (or several parts across volumes).</summary>
internal sealed class SegmentReadStream : Stream
{
    private readonly IReadOnlyList<(Stream File, long Offset, long Length)> _segs;
    private int _seg;
    private long _in;

    public SegmentReadStream(IReadOnlyList<(Stream File, long Offset, long Length)> segs) { _segs = segs; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (_seg < _segs.Count)
        {
            var (f, off, len) = _segs[_seg];
            long left = len - _in;
            if (left <= 0) { _seg++; _in = 0; continue; }
            f.Position = off + _in;
            int n = f.Read(buffer, offset, (int)Math.Min(count, left));
            if (n <= 0) throw new EndOfStreamException("Archive ended early.");
            _in += n;
            return n;
        }
        return 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
