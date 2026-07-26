// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;

namespace DiscForge.Core.Audio;

/// <summary>
/// A clean-room FLAC encoder — the counterpart to <see cref="DiscForge.Core.Chd.ChdFlac"/>'s
/// decoder — so DiscForge can write compressed, losslessly-decodable FLAC (e.g. for
/// a ScummVM game folder) without depending on an external tool like ffmpeg.
///
/// It targets what CD audio needs: 16-bit PCM, mono or stereo, at any sample rate
/// (44.1 kHz for Red Book). Compression comes from per-channel FIXED linear
/// predictors (orders 0–4, the best chosen per block) with Rice-coded residuals,
/// a CONSTANT subframe for silence, and — for stereo — inter-channel decorrelation:
/// each block is coded whichever of left/right, left/side, right/side or mid/side
/// is smallest. The output is a standard FLAC stream: "fLaC", a STREAMINFO block
/// (including the MD5 of the source audio so decoders can verify it), then frames,
/// each with the mandatory CRC-8 header and CRC-16 footer.
///
/// Clean-room, from the public FLAC format specification. Verified by decoding its
/// output back to identical PCM (through DiscForge's own decoder) and by standard
/// tools (ffmpeg/flac) accepting it and its MD5.
/// </summary>
public static class FlacEncoder
{
    private const int BlockSize = 4096;

    /// <summary>
    /// Encode interleaved 16-bit PCM to a FLAC byte stream. <paramref name="channels"/>
    /// is 1 or 2; <paramref name="interleaved"/> holds channel-interleaved samples
    /// (L,R,L,R… for stereo).
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<short> interleaved, int sampleRate, int channels)
    {
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), "FLAC encoding here supports 1 or 2 channels.");
        if (sampleRate is <= 0 or > 0xFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (interleaved.Length % channels != 0)
            throw new ArgumentException("Sample count is not a whole number of frames for the channel count.");

        int n = interleaved.Length / channels;
        byte[] audioMd5 = ComputePcmMd5(interleaved);

        // Deinterleave into per-channel sample arrays.
        var ch = new int[channels][];
        for (int c = 0; c < channels; c++) ch[c] = new int[n];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < channels; c++)
                ch[c][i] = interleaved[i * channels + c];

        // Encode every frame first, tracking block/frame sizes for STREAMINFO.
        var frames = new List<byte[]>();
        int minBlock = int.MaxValue, maxBlock = 0, minFrame = int.MaxValue, maxFrame = 0;
        int frameNumber = 0;
        for (int start = 0; start < n; start += BlockSize)
        {
            int bs = Math.Min(BlockSize, n - start);
            byte[] frame = EncodeFrame(ch, start, bs, sampleRate, channels, frameNumber++);
            frames.Add(frame);
            minBlock = Math.Min(minBlock, bs); maxBlock = Math.Max(maxBlock, bs);
            minFrame = Math.Min(minFrame, frame.Length); maxFrame = Math.Max(maxFrame, frame.Length);
        }
        if (frames.Count == 0) { minBlock = maxBlock = 0; minFrame = maxFrame = 0; }

        using var ms = new MemoryStream();
        ms.Write("fLaC"u8);
        WriteStreamInfo(ms, minBlock, maxBlock, minFrame, maxFrame, sampleRate, channels, n, audioMd5);
        foreach (var f in frames) ms.Write(f);
        return ms.ToArray();
    }

    // MD5 of the source audio, exactly as a FLAC decoder recomputes it: each sample
    // as a little-endian signed integer of ceil(bps/8) bytes, in stream order. For
    // 16-bit PCM that is just the interleaved little-endian sample bytes.
    private static byte[] ComputePcmMd5(ReadOnlySpan<short> interleaved)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[1 << 16];
        int bi = 0;
        foreach (short s in interleaved)
        {
            buffer[bi++] = (byte)(s & 0xFF);
            buffer[bi++] = (byte)((s >> 8) & 0xFF);
            if (bi == buffer.Length) { md5.AppendData(buffer, 0, bi); bi = 0; }
        }
        if (bi > 0) md5.AppendData(buffer, 0, bi);
        return md5.GetHashAndReset();
    }

    // ---- STREAMINFO ---------------------------------------------------------

    private static void WriteStreamInfo(Stream s, int minBlock, int maxBlock, int minFrame, int maxFrame,
                                        int sampleRate, int channels, long totalSamples, byte[] audioMd5)
    {
        // Metadata block header: last-block flag (1) + type 0 (STREAMINFO) + 24-bit length 34.
        s.WriteByte(0x80);
        s.WriteByte(0); s.WriteByte(0); s.WriteByte(34);

        var bw = new BitWriter();
        bw.Write((uint)minBlock, 16);
        bw.Write((uint)maxBlock, 16);
        bw.Write((uint)minFrame, 24);
        bw.Write((uint)maxFrame, 24);
        bw.Write((uint)sampleRate, 20);
        bw.Write((uint)(channels - 1), 3);
        bw.Write(16 - 1, 5);                       // bits per sample - 1
        bw.Write((uint)(totalSamples >> 18) & 0x3FFFF, 18);   // 36-bit total samples, high 18
        bw.Write((uint)(totalSamples & 0x3FFFF), 18);         // low 18
        var body = bw.ToArray();                   // 18 bytes so far
        s.Write(body, 0, body.Length);
        s.Write(audioMd5, 0, 16);                  // 128-bit MD5 of the unencoded audio
    }

    // ---- frames -------------------------------------------------------------

    private static byte[] EncodeFrame(int[][] ch, int start, int bs, int sampleRate, int channels, int frameNumber)
    {
        // Choose the channel assignment (stereo only) and the per-subframe block
        // arrays, coding whichever decorrelation is cheapest.
        int chAssign;
        int[][] sub;
        int[] subBps;
        if (channels == 2)
        {
            var l = Slice(ch[0], start, bs);
            var r = Slice(ch[1], start, bs);
            var side = new int[bs];
            var mid = new int[bs];
            for (int i = 0; i < bs; i++) { side[i] = l[i] - r[i]; mid[i] = (l[i] + r[i]) >> 1; }

            long cL = SubframeCost(l, 16), cR = SubframeCost(r, 16), cS = SubframeCost(side, 17), cM = SubframeCost(mid, 16);
            long independent = cL + cR, leftSide = cL + cS, rightSide = cS + cR, midSide = cM + cS;
            long best = Math.Min(Math.Min(independent, leftSide), Math.Min(rightSide, midSide));

            if (best == midSide) { chAssign = 10; sub = new[] { mid, side }; subBps = new[] { 16, 17 }; }
            else if (best == leftSide) { chAssign = 8; sub = new[] { l, side }; subBps = new[] { 16, 17 }; }
            else if (best == rightSide) { chAssign = 9; sub = new[] { side, r }; subBps = new[] { 17, 16 }; }
            else { chAssign = 1; sub = new[] { l, r }; subBps = new[] { 16, 16 }; }
        }
        else
        {
            chAssign = 0; sub = new[] { Slice(ch[0], start, bs) }; subBps = new[] { 16 };
        }

        var bw = new BitWriter();
        bw.Write(0x3FFE, 14);                       // frame sync
        bw.Write(0, 1);                             // reserved
        bw.Write(0, 1);                             // blocking strategy: fixed block size
        bw.Write(7, 4);                             // block size: 16-bit (blocksize-1) at end of header
        bw.Write((uint)SampleRateCode(sampleRate), 4);
        bw.Write((uint)chAssign, 4);                // channel assignment
        bw.Write(4, 3);                             // sample size: 16 bits
        bw.Write(0, 1);                             // reserved
        WriteUtf8(bw, (ulong)frameNumber);          // fixed blocking → frame number
        bw.Write((uint)(bs - 1), 16);               // block size - 1
        // Sample-rate codes used here (0–11) carry no trailing rate bytes.

        // The header is byte-aligned here; CRC-8 covers it.
        var headerBytes = bw.ToArray();
        bw.WriteByte(Crc8(headerBytes, 0, headerBytes.Length));

        for (int c = 0; c < sub.Length; c++)
            EncodeSubframe(bw, sub[c], subBps[c]);

        bw.Align();                                 // frame footer is byte-aligned
        var frameBytes = bw.ToArray();
        ushort crc16 = Crc16(frameBytes, 0, frameBytes.Length);

        var full = new byte[frameBytes.Length + 2];
        Array.Copy(frameBytes, full, frameBytes.Length);
        full[^2] = (byte)(crc16 >> 8);
        full[^1] = (byte)(crc16 & 0xFF);
        return full;
    }

    private static int[] Slice(int[] src, int start, int bs)
    {
        var block = new int[bs];
        Array.Copy(src, start, block, 0, bs);
        return block;
    }

    private static int SampleRateCode(int rate) => rate switch
    {
        44100 => 9, 48000 => 10, 32000 => 8, 22050 => 6, 24000 => 7,
        88200 => 1, 176400 => 2, 192000 => 3, 8000 => 4, 16000 => 5, 96000 => 11,
        _ => 0,   // 0 = read from STREAMINFO (no trailing rate bytes)
    };

    // ---- subframes ----------------------------------------------------------

    private static void EncodeSubframe(BitWriter bw, int[] x, int bps)
    {
        int bs = x.Length;
        // CONSTANT if the whole block is one value (silence and held tones).
        if (IsConstant(x))
        {
            bw.Write(0, 1);                          // padding
            bw.Write(0, 6);                          // subframe type: CONSTANT
            bw.Write(0, 1);                          // no wasted bits
            bw.WriteSigned(x[0], bps);
            return;
        }

        var (order, residual) = BestFixedOrder(x);
        bw.Write(0, 1);                              // padding
        bw.Write((uint)(8 + order), 6);              // subframe type: FIXED, order in low 3 bits
        bw.Write(0, 1);                              // no wasted bits
        for (int i = 0; i < order; i++) bw.WriteSigned(x[i], bps);   // warm-up samples
        WriteResidual(bw, residual, bs, order);
    }

    // Estimated encoded bit-cost of a subframe, used only to pick the stereo mode;
    // it mirrors EncodeSubframe's decisions so the choice matches what is written.
    private static long SubframeCost(int[] x, int bps)
    {
        if (IsConstant(x)) return 8 + bps;           // header + one value
        var (order, residual) = BestFixedOrder(x);
        int k = BestRiceParam(residual);
        long bits = 8 + (long)order * bps;           // header + warm-up
        bits += 2 + 4 + 4;                           // residual method + partition order + Rice param
        bits += (long)residual.Length * (k + 1);     // stop bits + remainders
        foreach (int r in residual) bits += ZigZag(r) >> k;   // unary quotients
        return bits;
    }

    private static bool IsConstant(int[] x)
    {
        for (int i = 1; i < x.Length; i++) if (x[i] != x[0]) return false;
        return true;
    }

    // Pick the FIXED predictor order (0..4) with the smallest residual magnitude.
    private static (int Order, int[] Residual) BestFixedOrder(int[] x)
    {
        int maxOrder = Math.Min(4, x.Length - 1);
        int bestOrder = 0;
        long bestCost = long.MaxValue;
        int[] bestResidual = FixedResidual(x, 0);
        for (int order = 0; order <= maxOrder; order++)
        {
            var res = FixedResidual(x, order);
            long cost = 0;
            foreach (int r in res) cost += Math.Abs((long)r);
            if (cost < bestCost) { bestCost = cost; bestOrder = order; bestResidual = res; }
        }
        return (bestOrder, bestResidual);
    }

    // Residual of a FIXED predictor of the given order over block x[order .. bs-1].
    private static int[] FixedResidual(int[] x, int order)
    {
        int count = x.Length - order;
        var r = new int[count];
        for (int i = 0; i < count; i++)
        {
            int idx = order + i;
            r[i] = order switch
            {
                0 => x[idx],
                1 => x[idx] - x[idx - 1],
                2 => x[idx] - 2 * x[idx - 1] + x[idx - 2],
                3 => x[idx] - 3 * x[idx - 1] + 3 * x[idx - 2] - x[idx - 3],
                _ => x[idx] - 4 * x[idx - 1] + 6 * x[idx - 2] - 4 * x[idx - 3] + x[idx - 4],
            };
        }
        return r;
    }

    // Single-partition Rice-coded residual (partition order 0, 4-bit parameters).
    private static void WriteResidual(BitWriter bw, int[] residual, int bs, int order)
    {
        bw.Write(0, 2);                              // coding method 0: 4-bit Rice parameters
        bw.Write(0, 4);                              // partition order 0 → one partition

        int k = BestRiceParam(residual);
        bw.Write((uint)k, 4);
        foreach (int v in residual)
        {
            uint u = ZigZag(v);
            uint q = u >> k;
            for (uint z = 0; z < q; z++) bw.Write(0, 1);   // unary quotient
            bw.Write(1, 1);                                 // stop bit
            if (k > 0) bw.Write(u & ((1u << k) - 1), k);    // remainder
        }
    }

    private static int BestRiceParam(int[] residual)
    {
        // Minimise total bits = count*(k+1) + sum(u >> k), over k in 0..14
        // (15 is the escape code, which this encoder never emits).
        int count = residual.Length;
        int bestK = 0;
        long bestBits = long.MaxValue;
        for (int k = 0; k <= 14; k++)
        {
            long q = 0;
            foreach (int v in residual) q += ZigZag(v) >> k;
            long bits = (long)count * (k + 1) + q;
            if (bits < bestBits) { bestBits = bits; bestK = k; }
        }
        return bestK;
    }

    private static uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));

    // ---- frame-number UTF-8-style coding ------------------------------------

    private static void WriteUtf8(BitWriter bw, ulong value)
    {
        if (value < 0x80) { bw.WriteByte((byte)value); return; }

        int bytes;
        if (value < 0x800) bytes = 2;
        else if (value < 0x10000) bytes = 3;
        else if (value < 0x200000) bytes = 4;
        else if (value < 0x4000000) bytes = 5;
        else bytes = 6;

        int leadBits = 7 - bytes;                    // data bits in the leading byte
        int prefix = 0xFF << (8 - bytes) & 0xFF;     // e.g. 3 bytes → 0xE0
        int shift = (bytes - 1) * 6;
        bw.WriteByte((byte)(prefix | (int)(value >> shift) & ((1 << leadBits) - 1)));
        for (int i = bytes - 2; i >= 0; i--)
            bw.WriteByte((byte)(0x80 | (int)(value >> (i * 6)) & 0x3F));
    }

    // ---- CRCs (FLAC: CRC-8 poly 0x07, CRC-16 poly 0x8005, MSB-first, init 0) --

    private static byte Crc8(byte[] data, int offset, int len)
    {
        int crc = 0;
        for (int i = 0; i < len; i++)
        {
            crc ^= data[offset + i];
            for (int b = 0; b < 8; b++)
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
        }
        return (byte)crc;
    }

    private static ushort Crc16(byte[] data, int offset, int len)
    {
        int crc = 0;
        for (int i = 0; i < len; i++)
        {
            crc ^= data[offset + i] << 8;
            for (int b = 0; b < 8; b++)
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return (ushort)crc;
    }

    // ---- MSB-first bit writer ----------------------------------------------

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _cur, _bits;

        public void Write(uint value, int n)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                _cur = (_cur << 1) | (int)((value >> i) & 1);
                if (++_bits == 8) { _bytes.Add((byte)_cur); _cur = 0; _bits = 0; }
            }
        }

        public void WriteSigned(int value, int n) => Write((uint)value & (n >= 32 ? ~0u : (1u << n) - 1), n);

        public void WriteByte(byte b)
        {
            if (_bits == 0) { _bytes.Add(b); return; }   // already aligned: fast path
            Write(b, 8);
        }

        public void Align() { if (_bits > 0) Write(0, 8 - _bits); }

        public byte[] ToArray() => _bytes.ToArray();     // only whole (flushed) bytes
    }
}
