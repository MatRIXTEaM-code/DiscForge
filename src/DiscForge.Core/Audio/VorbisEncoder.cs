// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Audio;

/// <summary>
/// A clean-room Ogg Vorbis I encoder, so DiscForge can write .ogg audio (e.g. for a
/// ScummVM game folder) without depending on an external tool like ffmpeg — the
/// lossy counterpart to <see cref="FlacEncoder"/>.
///
/// It targets what CD audio needs: 16-bit PCM, mono or stereo, at any sample rate
/// (44.1 kHz for Red Book). Each 2048-sample block is windowed with the Vorbis
/// window, transformed by a forward MDCT, and the 1024 spectral coefficients are
/// scalar-quantised and entropy-coded. The stream is a standard Vorbis I bitstream:
/// the identification, comment and setup headers (with our own generated codebooks,
/// a flat floor and a scalar residue), then one audio packet per block, all wrapped
/// in Ogg pages. Because the codebooks travel in the setup header, any Vorbis
/// decoder (libvorbis, ffmpeg, ScummVM) reads it back.
///
/// The residue value book is Huffman-shaped from a Laplacian model of MDCT
/// coefficients, so near-zero coefficients — the vast majority — cost few bits. This
/// is a faithful, transparent-on-real-material encoder, not a psychoacoustic one: it
/// does no perceptual bit allocation, trading some efficiency for simplicity and a
/// self-contained, dependency-free implementation.
///
/// Clean-room, from the public Vorbis I specification. Verified by decoding its
/// output back to PCM (via libvorbis/ffmpeg) at unity gain and correct pitch.
/// </summary>
public static class VorbisEncoder
{
    private const int BlockSize = 2048;
    private const int Half = BlockSize / 2;      // 1024 spectral bins
    private const int Mult = 2;                   // floor1 multiplier => Y range 128
    private const int RangeBits = 10;             // floor spans 1<<10 = 1024 bins
    private const int FloorPost = 127;            // flat floor Y post (~0 dB)
    // Empirically-calibrated gain constant: ffmpeg's iMDCT of our forward MDCT scales
    // by this factor, so a residue delta of D/GainNorm yields unity round-trip gain.
    private const double GainNorm = 485.0;

    /// <summary>Encoding quality: trades file size for fidelity. Standard is a good
    /// default for CD audio; High is near-transparent at a larger size.</summary>
    public enum Quality { Standard, High }

    /// <summary>
    /// Encode interleaved 16-bit PCM to an Ogg Vorbis byte stream. <paramref name="channels"/>
    /// is 1 or 2; <paramref name="interleaved"/> holds channel-interleaved samples
    /// (L,R,L,R… for stereo).
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<short> interleaved, int sampleRate, int channels,
                                Quality quality = Quality.Standard)
    {
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), "Vorbis encoding here supports 1 or 2 channels.");
        if (sampleRate is <= 0 or > 0xFFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (interleaved.Length % channels != 0)
            throw new ArgumentException("Sample count is not a whole number of frames for the channel count.");

        int n = interleaved.Length / channels;
        var enc = new Impl(sampleRate, channels, quality);
        return enc.Run(interleaved, n);
    }

    private sealed class Impl
    {
        private readonly int _rate, _channels;
        private readonly int _bits, _valHalf;
        private readonly double _d;
        private readonly double[] _window = new double[BlockSize];
        // Forward MDCT via a size-BlockSize FFT: h[n] = x[n]·e^{-jπn/2M}; H = FFT(h);
        // X[k] = Re{ e^{-j(π/M)·n0·(k+½)}·H[k] }. Pre/post twiddles precomputed here.
        private readonly double[] _preCos = new double[BlockSize], _preSin = new double[BlockSize];
        private readonly double[] _postCos = new double[Half], _postSin = new double[Half];
        private readonly int[] _bitrev = new int[BlockSize];
        private readonly double[] _twCos, _twSin;
        private Codebook _classBook = null!, _valBook = null!;

        public Impl(int rate, int channels, Quality quality)
        {
            _rate = rate; _channels = channels;
            (_bits, _d) = quality == Quality.High ? (12, 0.35) : (10, 1.0);
            _valHalf = (1 << _bits) / 2;

            for (int i = 0; i < BlockSize; i++)
            {
                double s = Math.Sin((i + 0.5) / BlockSize * Math.PI);
                _window[i] = Math.Sin(Math.PI / 2 * s * s);
            }

            double n0 = (BlockSize / 2 + 1) / 2.0;   // standard MDCT time offset
            for (int nn = 0; nn < BlockSize; nn++)
            {
                double a = -Math.PI * nn / BlockSize;    // -πn/(2M), 2M = BlockSize
                _preCos[nn] = Math.Cos(a); _preSin[nn] = Math.Sin(a);
            }
            for (int k = 0; k < Half; k++)
            {
                double a = -(Math.PI / Half) * n0 * (k + 0.5);
                _postCos[k] = Math.Cos(a); _postSin[k] = Math.Sin(a);
            }
            // bit-reversal permutation and twiddle table for an in-place radix-2 FFT.
            int bits = 0; while ((1 << bits) < BlockSize) bits++;
            for (int i = 0; i < BlockSize; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                _bitrev[i] = r;
            }
            _twCos = new double[BlockSize / 2]; _twSin = new double[BlockSize / 2];
            for (int i = 0; i < BlockSize / 2; i++)
            {
                double a = -2 * Math.PI * i / BlockSize;
                _twCos[i] = Math.Cos(a); _twSin[i] = Math.Sin(a);
            }

            BuildBooks();
        }

        public byte[] Run(ReadOnlySpan<short> interleaved, int total)
        {
            var ch = new double[_channels][];
            for (int c = 0; c < _channels; c++) ch[c] = new double[total];
            for (int i = 0; i < total; i++)
                for (int c = 0; c < _channels; c++)
                    ch[c][i] = interleaved[i * _channels + c] / 32768.0;

            var ogg = new OggWriter(0x44464F52);   // serial 'DFOR'
            ogg.WritePage(new[] { BuildIdentification() }, 0, bos: true, eos: false);
            ogg.WritePage(new[] { BuildComment(), BuildSetup() }, 0, bos: false, eos: false);

            int hop = Half;
            int nBlocks = (int)Math.Ceiling((double)total / hop) + 1;
            var packets = new List<(byte[] data, long granule)>(nBlocks);
            var frame = new double[BlockSize];
            for (int b = 0; b < nBlocks; b++)
            {
                int start = b * hop - Half;
                var bw = new BitWriter();
                bw.Write(0, 1);                     // audio packet; mode number = 0 bits
                var spec = new double[_channels][];
                for (int c = 0; c < _channels; c++)
                {
                    for (int i = 0; i < BlockSize; i++)
                    {
                        int idx = start + i;
                        double s = (idx >= 0 && idx < total) ? ch[c][idx] : 0.0;
                        frame[i] = s * _window[i];
                    }
                    spec[c] = Mdct(frame);
                }
                for (int c = 0; c < _channels; c++) EncodeFloor(bw);
                for (int c = 0; c < _channels; c++) _classBook.Encode(bw, 0);
                for (int c = 0; c < _channels; c++) EncodeResidue(bw, spec[c]);
                long granule = Math.Min((long)b * hop, total);
                packets.Add((bw.ToBytes(), granule));
            }

            for (int i = 0; i < packets.Count; i++)
            {
                bool eos = i == packets.Count - 1;
                ogg.WritePage(new[] { packets[i].data }, eos ? total : packets[i].granule, bos: false, eos: eos);
            }
            return ogg.ToBytes();
        }

        private readonly double[] _re = new double[BlockSize], _im = new double[BlockSize];

        private double[] Mdct(double[] x)
        {
            var re = _re; var im = _im;
            for (int n = 0; n < BlockSize; n++) { re[n] = x[n] * _preCos[n]; im[n] = x[n] * _preSin[n]; }
            Fft(re, im);
            var X = new double[Half];
            for (int k = 0; k < Half; k++) X[k] = re[k] * _postCos[k] - im[k] * _postSin[k];
            return X;
        }

        // In-place radix-2 decimation-in-time FFT (forward, e^{-j} convention).
        private void Fft(double[] re, double[] im)
        {
            for (int i = 0; i < BlockSize; i++)
            {
                int j = _bitrev[i];
                if (j > i) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= BlockSize; len <<= 1)
            {
                int half = len >> 1, step = BlockSize / len;
                for (int i = 0; i < BlockSize; i += len)
                {
                    for (int k = 0; k < half; k++)
                    {
                        int t = k * step;
                        double wr = _twCos[t], wi = _twSin[t];
                        int a = i + k, b = a + half;
                        double vr = re[b] * wr - im[b] * wi;
                        double vi = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - vr; im[b] = im[a] - vi;
                        re[a] += vr; im[a] += vi;
                    }
                }
            }
        }

        private void EncodeFloor(BitWriter bw)
        {
            bw.Write(1, 1);                         // nonzero
            int postBits = Ilog((256 / Mult) - 1);  // range 128 => 7 bits
            bw.Write(FloorPost, postBits);
            bw.Write(FloorPost, postBits);
        }

        private void EncodeResidue(BitWriter bw, double[] spec)
        {
            for (int k = 0; k < Half; k++)
            {
                int q = (int)Math.Round(spec[k] / _d);
                if (q < -_valHalf) q = -_valHalf;
                if (q > _valHalf - 1) q = _valHalf - 1;
                _valBook.Encode(bw, q + _valHalf);
            }
        }

        private void BuildBooks()
        {
            _classBook = new Codebook { Dimensions = 1, Entries = 2, LookupType = 0, Lengths = new[] { 1, 1 } };
            _classBook.MakeWords();

            int L = 1 << _bits;
            float delta = (float)(_d / GainNorm);
            const double tau = 5.0;
            var freqs = new long[L];
            for (int i = 0; i < L; i++)
            {
                int dist = Math.Abs(i - _valHalf);
                freqs[i] = Math.Max(1, (long)Math.Round(1_000_000.0 * Math.Exp(-dist / tau)));
            }
            var lengths = HuffmanLengths(freqs, 32);
            var mult = new int[L];
            for (int i = 0; i < L; i++) mult[i] = i;
            _valBook = new Codebook
            {
                Dimensions = 1, Entries = L, LookupType = 1, MinValue = -_valHalf * delta,
                DeltaValue = delta, ValueBits = _bits, SequenceP = false,
                Lengths = lengths, Multiplicands = mult,
            };
            _valBook.MakeWords();
        }

        // ---- header packets ----
        private byte[] BuildIdentification()
        {
            var bw = new BitWriter();
            bw.Write(1, 8); WriteVorbis(bw);
            bw.Write(0, 32); bw.Write((uint)_channels, 8); bw.Write((uint)_rate, 32);
            bw.Write(0, 32); bw.Write(0, 32); bw.Write(0, 32);
            bw.Write(11, 4); bw.Write(11, 4); bw.Write(1, 1);
            return bw.ToBytes();
        }
        private static byte[] BuildComment()
        {
            var bw = new BitWriter();
            bw.Write(3, 8); WriteVorbis(bw);
            var vb = Encoding.UTF8.GetBytes("DiscForge");
            bw.Write((uint)vb.Length, 32); foreach (byte b in vb) bw.Write(b, 8);
            bw.Write(0, 32); bw.Write(1, 1);
            return bw.ToBytes();
        }
        private byte[] BuildSetup()
        {
            var bw = new BitWriter();
            bw.Write(5, 8); WriteVorbis(bw);
            var books = new[] { _classBook, _valBook };
            bw.Write((uint)(books.Length - 1), 8);
            foreach (var cb in books) PackCodebook(bw, cb);
            bw.Write(0, 6); bw.Write(0, 16);                    // 1 time transform (placeholder)
            bw.Write(0, 6); PackFloor1Flat(bw);                 // 1 floor
            bw.Write(0, 6); PackResidue(bw);                    // 1 residue
            bw.Write(0, 6); PackMapping(bw);                    // 1 mapping
            bw.Write(0, 6);                                     // 1 mode
            bw.Write(0, 1); bw.Write(0, 16); bw.Write(0, 16); bw.Write(0, 8);
            bw.Write(1, 1);
            return bw.ToBytes();
        }
        private static void WriteVorbis(BitWriter bw) { foreach (char c in "vorbis") bw.Write((byte)c, 8); }

        private void PackCodebook(BitWriter bw, Codebook cb)
        {
            bw.Write(0x564342, 24);                             // sync 'BCV'
            bw.Write((uint)cb.Dimensions, 16);
            bw.Write((uint)cb.Entries, 24);
            bw.Write(0, 1); bw.Write(0, 1);                     // ordered=0, sparse=0
            foreach (int len in cb.Lengths) bw.Write((uint)(len - 1), 5);
            bw.Write((uint)cb.LookupType, 4);
            if (cb.LookupType == 1)
            {
                bw.Write(FloatToVorbis(cb.MinValue), 32);
                bw.Write(FloatToVorbis(cb.DeltaValue), 32);
                bw.Write((uint)(cb.ValueBits - 1), 4);
                bw.Write(cb.SequenceP ? 1u : 0u, 1);
                int lv = Lookup1Values(cb.Entries, cb.Dimensions);
                for (int i = 0; i < lv; i++) bw.Write((uint)cb.Multiplicands[i], cb.ValueBits);
            }
        }
        private static void PackFloor1Flat(BitWriter bw)
        {
            bw.Write(1, 16); bw.Write(0, 5); bw.Write(Mult - 1, 2); bw.Write(RangeBits, 4);
        }
        private static void PackResidue(BitWriter bw)
        {
            bw.Write(1, 16);                                    // residue type 1
            bw.Write(0, 24); bw.Write(Half, 24); bw.Write(Half - 1, 24);
            bw.Write(0, 6);                                     // 1 classification
            bw.Write(0, 8);                                     // classbook = 0
            bw.Write(1, 3); bw.Write(0, 1);                     // cascade: book in pass 0
            bw.Write(1, 8);                                     // pass-0 book = 1
        }
        private void PackMapping(BitWriter bw)
        {
            bw.Write(0, 16); bw.Write(0, 1); bw.Write(0, 1); bw.Write(0, 2);
            bw.Write(0, 8); bw.Write(0, 8); bw.Write(0, 8);     // time, floor, residue
        }

        private static int Ilog(int v) { int r = 0; while (v > 0) { r++; v >>= 1; } return r; }
        private static int Lookup1Values(int entries, int dim)
        { int v = 0; while (Pow(v + 1, dim) <= entries) v++; return v; }
        private static long Pow(int b, int e) { long r = 1; for (int i = 0; i < e; i++) r *= b; return r; }

        private static uint FloatToVorbis(float f)
        {
            uint sign = 0; double val = f;
            if (val < 0) { sign = 0x80000000u; val = -val; }
            if (val == 0) return sign;
            int exp = (int)Math.Floor(Math.Log(val) / Math.Log(2.0));
            long mant = (long)Math.Round(val * Math.Pow(2.0, 20 - exp));
            if (mant >= (1L << 21)) { mant >>= 1; exp++; }
            uint packed = (uint)(mant & 0x1FFFFF);
            packed |= (uint)((exp + 768) & 0x3FF) << 21;
            packed |= sign;
            return packed;
        }

        private static int[] HuffmanLengths(long[] freq, int maxLen)
        {
            int n = freq.Length;
            var lengths = new int[n];
            int cap = 2 * n;
            var parent = new int[cap];
            var weight = new long[cap];
            for (int i = 0; i < cap; i++) parent[i] = -1;
            var heap = new List<(long w, int node)>(n);
            void Push((long, int) e)
            {
                heap.Add(e); int c = heap.Count - 1;
                while (c > 0) { int p = (c - 1) / 2; if (heap[p].w <= heap[c].w) break; (heap[p], heap[c]) = (heap[c], heap[p]); c = p; }
            }
            (long w, int node) Pop()
            {
                var top = heap[0]; var last = heap[^1]; heap.RemoveAt(heap.Count - 1);
                if (heap.Count > 0)
                {
                    heap[0] = last; int c = 0;
                    while (true)
                    {
                        int l = 2 * c + 1, r = 2 * c + 2, m = c;
                        if (l < heap.Count && heap[l].w < heap[m].w) m = l;
                        if (r < heap.Count && heap[r].w < heap[m].w) m = r;
                        if (m == c) break; (heap[m], heap[c]) = (heap[c], heap[m]); c = m;
                    }
                }
                return top;
            }
            for (int i = 0; i < n; i++) { weight[i] = freq[i]; Push((freq[i], i)); }
            int next = n;
            while (heap.Count > 1)
            {
                var a = Pop(); var b = Pop();
                weight[next] = a.w + b.w; parent[a.node] = next; parent[b.node] = next;
                Push((weight[next], next)); next++;
            }
            for (int i = 0; i < n; i++)
            {
                int d = 0, p = parent[i];
                while (p != -1) { d++; p = parent[p]; }
                lengths[i] = Math.Max(1, d);
            }
            for (int i = 0; i < n; i++) if (lengths[i] > maxLen) lengths[i] = maxLen;
            long total = 0; foreach (int l in lengths) total += 1L << (maxLen - l);
            long capK = 1L << maxLen;
            while (total > capK)
            {
                int j = -1, best = -1;
                for (int i = 0; i < n; i++) if (lengths[i] < maxLen && lengths[i] > best) { best = lengths[i]; j = i; }
                if (j < 0) break;
                lengths[j]++; total -= 1L << (maxLen - lengths[j]);
            }
            return lengths;
        }
    }

    // LSB-first bit packer (Vorbis packs the low bit first within each byte).
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _cur, _bit;
        public void Write(uint value, int bits)
        {
            for (int i = 0; i < bits; i++)
            {
                _cur |= (int)((value >> i) & 1u) << _bit;
                if (++_bit == 8) { _bytes.Add((byte)_cur); _cur = 0; _bit = 0; }
            }
        }
        public void Write(int value, int bits) => Write((uint)value, bits);
        public byte[] ToBytes()
        {
            var o = new List<byte>(_bytes);
            if (_bit > 0) o.Add((byte)_cur);
            return o.ToArray();
        }
    }

    // A codebook we define ourselves; codes are canonical Huffman, bit-reversed for
    // the LSB-first packer (faithful to libvorbis _make_words).
    private sealed class Codebook
    {
        public int Dimensions, Entries, LookupType, ValueBits;
        public float MinValue, DeltaValue;
        public bool SequenceP;
        public int[] Lengths = Array.Empty<int>();
        public int[] Multiplicands = Array.Empty<int>();
        public uint[] Codes = Array.Empty<uint>();

        public void MakeWords()
        {
            var r = new uint[Entries];
            var marker = new uint[33];
            for (int i = 0; i < Entries; i++)
            {
                int length = Lengths[i];
                if (length <= 0) continue;
                uint entry = marker[length];
                r[i] = entry;
                for (int j = length; j > 0; j--)
                {
                    if ((marker[j] & 1) != 0)
                    {
                        if (j == 1) marker[1]++; else marker[j] = marker[j - 1] << 1;
                        break;
                    }
                    marker[j]++;
                }
                for (int j = length + 1; j < 33; j++)
                {
                    if ((marker[j] >> 1) == entry) { entry = marker[j]; marker[j] = marker[j - 1] << 1; }
                    else break;
                }
            }
            Codes = new uint[Entries];
            for (int i = 0; i < Entries; i++)
            {
                uint temp = 0;
                for (int j = 0; j < Lengths[i]; j++) { temp <<= 1; temp |= (r[i] >> j) & 1; }
                Codes[i] = temp;
            }
        }
        public void Encode(BitWriter bw, int entry) => bw.Write(Codes[entry], Lengths[entry]);
    }

    // Minimal Ogg bitstream container.
    private sealed class OggWriter
    {
        private readonly uint _serial;
        private uint _seq;
        private readonly List<byte> _out = new();
        public OggWriter(uint serial) { _serial = serial; }

        public void WritePage(byte[][] packets, long granule, bool bos, bool eos)
        {
            var segs = new List<int>();
            foreach (var p in packets)
            { int len = p.Length; while (len >= 255) { segs.Add(255); len -= 255; } segs.Add(len); }
            if (segs.Count == 0) segs.Add(0);

            var page = new List<byte>();
            page.AddRange("OggS"u8.ToArray());
            page.Add(0);
            byte h = 0; if (bos) h |= 0x02; if (eos) h |= 0x04; page.Add(h);
            page.AddRange(BitConverter.GetBytes((ulong)granule));
            page.AddRange(BitConverter.GetBytes(_serial));
            page.AddRange(BitConverter.GetBytes(_seq++));
            int crcPos = page.Count; page.AddRange(new byte[4]);
            page.Add((byte)segs.Count); foreach (int s in segs) page.Add((byte)s);
            foreach (var p in packets) page.AddRange(p);

            uint crc = OggCrc(page);
            page[crcPos] = (byte)crc; page[crcPos + 1] = (byte)(crc >> 8);
            page[crcPos + 2] = (byte)(crc >> 16); page[crcPos + 3] = (byte)(crc >> 24);
            _out.AddRange(page);
        }
        public byte[] ToBytes() => _out.ToArray();

        private static readonly uint[] Tbl = BuildTable();
        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint r = i << 24;
                for (int j = 0; j < 8; j++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04c11db7 : r << 1;
                t[i] = r;
            }
            return t;
        }
        private static uint OggCrc(List<byte> d)
        { uint c = 0; foreach (byte b in d) c = (c << 8) ^ Tbl[((c >> 24) & 0xFF) ^ b]; return c; }
    }
}
