// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Patch;

/// <summary>What <see cref="VcdiffEncoder.Create"/> produced.</summary>
public sealed record VcdiffEncodeReport(long SourceBytes, long TargetBytes, long PatchBytes, int Windows,
    long CopiedBytes, long AddedBytes, long RunBytes);

/// <summary>
/// Clean-room VCDIFF (RFC 3284) encoder producing xdelta3-compatible patches: the default code table,
/// no secondary compression, and xdelta3's per-window Adler-32 (window indicator bit 2, four bytes
/// big-endian) so xdelta3 — and every front-end built on it — verifies the output as it applies it.
/// DiscForge's own <see cref="VcdiffPatch"/> applies the result too.
///
/// Streaming, for disc-size images: the target is encoded in 8 MiB windows, and each window matches
/// against a bounded slice of the source (up to 64 MiB) centred on where the previous window's copies
/// were coming from. That follows data that has shifted — an insertion or deletion earlier in the
/// image moves everything after it — without ever holding the whole source in memory. Within a window
/// matching is greedy: a fast path extends the current alignment (the common "most of the disc is
/// unchanged" case runs at memcmp speed), then a hash of 16-byte blocks sampled every 4 bytes of the
/// source slice, plus the window's own earlier bytes, finds new matches; runs of one byte become RUN.
///
/// Patches are larger than xdelta3's own at the same settings (no secondary compression, simpler
/// matching) but apply anywhere xdelta3 does.
/// </summary>
public static class VcdiffEncoder
{
    public const int DefaultWindowSize = 8 * 1024 * 1024;
    public const int DefaultSourceSpan = 64 * 1024 * 1024;

    private const int MinMatch = 16;      // shortest COPY worth its instruction + address
    private const int SampleStride = 4;   // source slice is indexed every 4 bytes
    private const int HashBits = 22;
    private const int MinRun = 12;

    /// <summary>Encode a patch turning <paramref name="source"/> into <paramref name="target"/> and
    /// write it to <paramref name="patch"/>. Both inputs must be seekable.</summary>
    public static VcdiffEncodeReport Create(Stream source, Stream target, Stream patch,
        string? sourceName = null, string? targetName = null,
        int windowSize = DefaultWindowSize, int sourceSpan = DefaultSourceSpan, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(patch);
        if (!source.CanSeek || !target.CanSeek) throw new ArgumentException("Source and target must be seekable.");
        if (target.Length == 0) throw new ArgumentException("The target is empty — there is nothing to patch to.");
        if (windowSize < 1024 || sourceSpan < windowSize) throw new ArgumentOutOfRangeException(nameof(windowSize));

        long srcLen = source.Length, tgtLen = target.Length;
        long patchStart = patch.CanSeek ? patch.Position : 0;
        long written = 0;
        void Put(ReadOnlySpan<byte> b) { patch.Write(b); written += b.Length; }

        // ---- file header: magic, then xdelta3's application header (target//source/ names) ----
        var header = new List<byte> { 0xD6, 0xC3, 0xC4, 0x00 };
        string app = $"{targetName ?? ""}//{sourceName ?? ""}/";
        bool withApp = targetName is not null || sourceName is not null;
        header.Add((byte)(withApp ? 0x04 : 0x00));
        if (withApp)
        {
            var bytes = Encoding.UTF8.GetBytes(app);
            WriteInt(header, bytes.Length);
            header.AddRange(bytes);
        }
        Put(header.ToArray());

        var tgtBuf = new byte[windowSize];
        var srcBuf = new byte[sourceSpan];
        var srcIndex = new SourceIndex();
        var tgtTable = new int[1 << HashBits];
        long drift = 0;                // (source offset − target offset) of the last copy made
        AnchorIndex? anchors = null;   // built on first need
        long copied = 0, added = 0, runs = 0;
        int windows = 0;

        for (long tPos = 0; tPos < tgtLen; tPos += windowSize)
        {
            int wLen = (int)Math.Min(windowSize, tgtLen - tPos);
            target.Position = tPos;
            target.ReadExactly(tgtBuf, 0, wLen);
            ReadOnlySpan<byte> tgt = tgtBuf.AsSpan(0, wLen);

            // Source slice for this window: centred on where this window's bytes probably came from.
            (WindowWriter w, long segPos) EncodeWith(ref long d)
            {
                long sp = 0; int sl = 0;
                if (srcLen > 0)
                {
                    long centre = tPos + d + wLen / 2;
                    sp = Math.Clamp(centre - sourceSpan / 2, 0, Math.Max(0, srcLen - sourceSpan));
                    sl = (int)Math.Min(sourceSpan, srcLen - sp);
                    source.Position = sp;
                    source.ReadExactly(srcBuf, 0, sl);
                }
                var ww = new WindowWriter(sl);
                srcIndex.Cover(srcBuf.AsSpan(0, sl), sp);
                EncodeWindow(srcBuf.AsSpan(0, sl), tgtBuf.AsSpan(0, wLen), srcIndex, tgtTable, ww, sp, tPos, ref d);
                return (ww, sp);
            }

            long tryDrift = drift;
            var (w, segPos) = EncodeWith(ref tryDrift);
            // Poor coverage with a source bigger than one slice: the data may have moved further than
            // the slice reaches (a large insertion or deletion). Look the window's bytes up in a
            // sparse index of the whole source, and re-encode around the most common displacement.
            if (srcLen > sourceSpan && w.Copied < wLen / 2)
            {
                anchors ??= AnchorIndex.Build(source);
                if (anchors.Locate(tgtBuf.AsSpan(0, wLen), tPos) is { } found && found != drift)
                {
                    long d2 = found;
                    var (w2, seg2) = EncodeWith(ref d2);
                    if (w2.Copied > w.Copied) { w = w2; segPos = seg2; tryDrift = d2; }
                }
            }
            drift = tryDrift;
            copied += w.Copied; added += w.Added; runs += w.Runs;
            w.Serialize();

            // ---- window ----
            var win = new List<byte>(w.Data.Count + w.Inst.Count + w.Addr.Count + 32);
            bool usesSource = w.UsedSource;
            win.Add((byte)((usesSource ? 0x01 : 0x00) | 0x04));
            if (usesSource) { WriteInt(win, w.SourceUsedLength); WriteInt(win, segPos + w.SourceLo); }
            var delta = new List<byte>();
            WriteInt(delta, wLen);
            delta.Add(0); // no secondary compression
            WriteInt(delta, w.Data.Count);
            WriteInt(delta, w.Inst.Count);
            WriteInt(delta, w.Addr.Count);
            uint adler = VcdiffPatch.Adler32(tgt);
            delta.Add((byte)(adler >> 24)); delta.Add((byte)(adler >> 16)); delta.Add((byte)(adler >> 8)); delta.Add((byte)adler);
            WriteInt(win, delta.Count + w.Data.Count + w.Inst.Count + w.Addr.Count);
            win.AddRange(delta);
            Put(win.ToArray());
            Put(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(w.Data));
            Put(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(w.Inst));
            Put(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(w.Addr));
            windows++;
            progress?.Report((double)(tPos + wLen) / tgtLen);
        }
        patch.Flush();
        return new VcdiffEncodeReport(srcLen, tgtLen, written, windows, copied, added, runs);
    }

    /// <summary>In-memory convenience for small inputs and tests.</summary>
    public static byte[] Create(byte[] source, byte[] target, int windowSize = DefaultWindowSize, int sourceSpan = DefaultSourceSpan)
    {
        using var s = new MemoryStream(source, false);
        using var t = new MemoryStream(target, false);
        using var p = new MemoryStream();
        Create(s, t, p, windowSize: windowSize, sourceSpan: Math.Max(sourceSpan, windowSize));
        return p.ToArray();
    }

    // ---- matching -------------------------------------------------------------------------------

    private static void EncodeWindow(ReadOnlySpan<byte> src, ReadOnlySpan<byte> tgt, SourceIndex srcIndex, int[] tgtTable,
        WindowWriter w, long segPos, long tPos, ref long drift)
    {
        Array.Clear(tgtTable);

        int n = tgt.Length;
        int pos = 0, addStart = 0;
        // Expected source address for the current alignment (from the last copy).
        long expect = tPos + drift - segPos;
        int tgtIndexed = 0;   // target positions below this are in tgtTable
        int misses = 0;       // consecutive failed lookups: step faster through data that doesn't match

        while (pos < n)
        {
            // 1) Fast path: keep following the current alignment.
            long e = expect + pos;
            if (e >= 0 && e + MinMatch <= src.Length && pos + MinMatch <= n
                && src.Slice((int)e, MinMatch).SequenceEqual(tgt.Slice(pos, MinMatch)))
            {
                int len = Extend(src, (int)e, tgt, pos);
                EmitCopy(w, tgt, ref addStart, ref pos, (int)e, len);
                misses = 0;
                continue;
            }

            if (pos + MinMatch > n) { pos++; continue; }

            // 2) Runs of one byte.
            int run = RunLength(tgt, pos);
            if (run >= MinRun)
            {
                w.Add(tgt.Slice(addStart, pos - addStart));
                w.Run(tgt[pos], run);
                pos += run; addStart = pos;
                misses = 0;
                continue;
            }

            // 3) Hash lookup: source slice, then earlier in this window.
            int h = Hash(tgt.Slice(pos, MinMatch));
            int bestAddr = -1, bestLen = 0, bestBack = 0;
            long abs = srcIndex.Lookup(h) - segPos;
            int sc = abs >= 0 && abs + MinMatch <= src.Length ? (int)abs : -1;
            if (sc >= 0 && src.Slice(sc, MinMatch).SequenceEqual(tgt.Slice(pos, MinMatch)))
            {
                int back = 0;
                while (back < pos - addStart && sc - back > 0 && src[sc - back - 1] == tgt[pos - back - 1]) back++;
                int len = Extend(src, sc, tgt, pos) + back;
                bestAddr = sc - back; bestLen = len; bestBack = back;
            }
            // index target positions up to pos (sparsely) so later bytes can copy from them
            for (; tgtIndexed + MinMatch <= pos; tgtIndexed += SampleStride)
                tgtTable[Hash(tgt.Slice(tgtIndexed, MinMatch))] = tgtIndexed + 1;
            int tc = tgtTable[h] - 1;
            if (tc >= 0 && tc < pos && tgt.Slice(tc, MinMatch).SequenceEqual(tgt.Slice(pos, MinMatch)))
            {
                int len = ExtendSelf(tgt, tc, pos);
                if (len > bestLen) { bestAddr = src.Length + tc; bestLen = len; bestBack = 0; }
            }

            if (bestLen >= MinMatch)
            {
                pos -= bestBack;
                EmitCopy(w, tgt, ref addStart, ref pos, bestAddr, bestLen);
                if (bestAddr < src.Length)
                {
                    expect = bestAddr - (pos - bestLen) ;
                    drift = segPos + expect - tPos;
                }
                misses = 0;
                continue;
            }
            // Acceleration: after a long stretch with no match, sample more sparsely. A match found
            // later is extended backwards over the skipped bytes, so little is lost.
            pos += 1 + Math.Min(misses++ >> 6, 31);
        }
        w.Add(tgt.Slice(addStart, n - addStart));
    }

    private static void EmitCopy(WindowWriter w, ReadOnlySpan<byte> tgt, ref int addStart, ref int pos, int addr, int len)
    {
        w.Add(tgt.Slice(addStart, pos - addStart));
        w.Copy(addr, len, pos);
        pos += len;
        addStart = pos;
    }

    private static int Extend(ReadOnlySpan<byte> src, int s, ReadOnlySpan<byte> tgt, int t)
    {
        int max = Math.Min(src.Length - s, tgt.Length - t);
        int common = src.Slice(s, max).CommonPrefixLength(tgt.Slice(t, max));
        return common;
    }

    /// <summary>Match within the target window (may overlap, like LZ77).</summary>
    private static int ExtendSelf(ReadOnlySpan<byte> tgt, int from, int t)
    {
        int len = 0;
        while (t + len < tgt.Length && tgt[from + len] == tgt[t + len]) len++;
        return len;
    }

    private static int RunLength(ReadOnlySpan<byte> t, int pos)
    {
        byte b = t[pos];
        int i = pos + 1;
        while (i < t.Length && t[i] == b) i++;
        return i - pos;
    }

    private static int Hash(ReadOnlySpan<byte> b) => (int)(Hash64(b) >> (64 - HashBits));

    // ---- instruction encoding (default code table) ------------------------------------------------

    /// <summary>
    /// Hash index over the current source slice, keyed by ABSOLUTE source position so it survives the
    /// slice sliding forward window by window: only the newly covered part of the source is hashed.
    /// Entries left over from earlier slices are harmless — lookups are bounds-checked and verified.
    /// </summary>
    private sealed class SourceIndex
    {
        private readonly long[] _table = new long[1 << HashBits];   // absolute position + 1
        private long _from, _to;                                      // absolute range indexed

        public void Cover(ReadOnlySpan<byte> slice, long slicePos)
        {
            long a = slicePos, b = slicePos + slice.Length;
            if (b <= _from || a >= _to) { Array.Clear(_table); IndexRange(slice, slicePos, a, b); }
            else
            {
                if (a < _from) IndexRange(slice, slicePos, a, _from);
                if (b > _to) IndexRange(slice, slicePos, _to, b);
            }
            _from = a; _to = b;
        }

        private void IndexRange(ReadOnlySpan<byte> slice, long slicePos, long from, long to)
        {
            long p = (from + SampleStride - 1) / SampleStride * SampleStride;
            for (; p + MinMatch <= to; p += SampleStride)
                _table[Hash(slice.Slice((int)(p - slicePos), MinMatch))] = p + 1;
        }

        /// <summary>Absolute source position for the hash, or -1.</summary>
        public long Lookup(int h) => _table[h] - 1;
    }

    /// <summary>
    /// A sparse fingerprint index of the whole source — a 16-byte hash every <c>stride</c> bytes — used
    /// only to re-find data that has moved beyond one source slice. Built with one streaming pass.
    /// </summary>
    private sealed class AnchorIndex
    {
        private readonly Dictionary<ulong, long> _at = new();
        private readonly ulong[] _filter = new ulong[1 << 18]; // 16M-bit prefilter
        private AnchorIndex() { }

        public static AnchorIndex Build(Stream source)
        {
            var ix = new AnchorIndex();
            long len = source.Length;
            long stride = Math.Max(4096, (len >> 21) + 1);            // at most ~2M anchors
            stride = (stride + 15) & ~15L;
            var buf = new byte[MinMatch];
            for (long p = 0; p + MinMatch <= len; p += stride)
            {
                source.Position = p;
                source.ReadExactly(buf);
                ulong h = Hash64(buf);
                ix._at.TryAdd(h, p);
                ix._filter[(h >> 40) & 0x3FFFF] |= 1UL << (int)(h & 63);
            }
            return ix;
        }

        /// <summary>Most common (source − target) displacement among the window's anchor hits, or null.</summary>
        public long? Locate(ReadOnlySpan<byte> window, long tPos)
        {
            var votes = new Dictionary<long, int>();
            int hits = 0;
            // The first 1 MiB is plenty: anchors are at most a few KiB apart in the source.
            int scan = Math.Min(window.Length, 1 << 20);
            for (int p = 0; p + MinMatch <= scan && hits < 64; p++)
            {
                ulong h = Hash64(window.Slice(p, MinMatch));
                if ((_filter[(h >> 40) & 0x3FFFF] & (1UL << (int)(h & 63))) == 0) continue;
                if (!_at.TryGetValue(h, out long sp)) continue;
                long d = sp - (tPos + p);
                votes[d] = votes.GetValueOrDefault(d) + 1;
                hits++;
                p += MinMatch - 1;
            }
            if (votes.Count == 0) return null;
            return votes.MaxBy(kv => kv.Value).Key;
        }
    }

    private static ulong Hash64(ReadOnlySpan<byte> b)
    {
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(b);
        ulong c = BinaryPrimitives.ReadUInt64LittleEndian(b[8..]);
        return (a * 0x9E3779B97F4A7C15UL) ^ (c * 0xC2B2AE3D27D4EB4FUL);
    }

    /// <summary>Collects a window's instructions, then serializes them once the source range the
    /// copies actually used is known — so the window declares only that slice of the source.</summary>
    private sealed class WindowWriter
    {
        private enum Kind : byte { Add, Run, Copy }
        private readonly record struct Op(Kind Kind, int Len, int Addr, int TargetPos, int DataOffset, byte RunByte);

        private readonly List<Op> _ops = new();
        private readonly List<byte> _addData = new();
        private readonly int _segLen;
        private int _lo = int.MaxValue, _hi;
        public long Copied, Added, Runs;
        public readonly List<byte> Data = new(), Inst = new(), Addr = new();

        public WindowWriter(int segLen) { _segLen = segLen; }

        public bool UsedSource => _hi > 0;
        /// <summary>First source-slice byte any copy uses.</summary>
        public int SourceLo => UsedSource ? _lo : 0;
        public int SourceUsedLength => UsedSource ? _hi - _lo : 0;

        public void Add(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return;
            _ops.Add(new Op(Kind.Add, bytes.Length, 0, 0, _addData.Count, 0));
            _addData.AddRange(bytes);
            Added += bytes.Length;
        }

        public void Run(byte b, int len)
        {
            _ops.Add(new Op(Kind.Run, len, 0, 0, 0, b));
            Runs += len;
        }

        /// <param name="addr">Address in the combined source-slice + window space.</param>
        /// <param name="targetPos">Where in the window this copy's output starts.</param>
        public void Copy(int addr, int len, int targetPos)
        {
            _ops.Add(new Op(Kind.Copy, len, addr, targetPos, 0, 0));
            if (addr < _segLen)
            {
                _lo = Math.Min(_lo, addr);
                _hi = Math.Max(_hi, Math.Min(_segLen, addr + len));
            }
            Copied += len;
        }

        /// <summary>Write the sections, with source addresses rebased onto the used slice.</summary>
        public void Serialize()
        {
            int lo = SourceLo, newSeg = SourceUsedLength;
            foreach (var op in _ops)
            {
                switch (op.Kind)
                {
                    case Kind.Add:
                        if (op.Len <= 17) Inst.Add((byte)(1 + op.Len));          // ADD size 1..17
                        else { Inst.Add(1); WriteInt(Inst, op.Len); }             // ADD size 0 + explicit
                        Data.AddRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_addData).Slice(op.DataOffset, op.Len));
                        break;
                    case Kind.Run:
                        Inst.Add(0); WriteInt(Inst, op.Len);                      // RUN size 0 + explicit
                        Data.Add(op.RunByte);
                        break;
                    case Kind.Copy:
                    {
                        long addr = op.Addr < _segLen ? op.Addr - lo : (long)op.Addr - _segLen + newSeg;
                        long here = (long)newSeg + op.TargetPos;
                        long rel = here - addr;
                        int mode = IntSize(rel) < IntSize(addr) ? 1 : 0;           // SELF or HERE, whichever is shorter
                        int baseIndex = 19 + mode * 16;
                        if (op.Len >= 4 && op.Len <= 18) Inst.Add((byte)(baseIndex + (op.Len - 3)));
                        else { Inst.Add((byte)baseIndex); WriteInt(Inst, op.Len); }
                        WriteInt(Addr, mode == 1 ? rel : addr);
                        break;
                    }
                }
            }
        }
    }

    private static int IntSize(long v)
    {
        int n = 1;
        while ((v >>= 7) != 0) n++;
        return n;
    }

    /// <summary>RFC 3284 integer: base-128, most significant digit first.</summary>
    private static void WriteInt(List<byte> o, long v)
    {
        if (v < 0) throw new ArgumentOutOfRangeException(nameof(v));
        Span<byte> tmp = stackalloc byte[10];
        int i = tmp.Length;
        tmp[--i] = (byte)(v & 0x7F);
        v >>= 7;
        while (v != 0) { tmp[--i] = (byte)(0x80 | (v & 0x7F)); v >>= 7; }
        for (; i < tmp.Length; i++) o.Add(tmp[i]);
    }
}
