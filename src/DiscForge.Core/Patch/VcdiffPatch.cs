// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Compression;

namespace DiscForge.Core.Patch;

public sealed class VcdiffFormatException(string message) : Exception(message);

/// <summary>What a VCDIFF (xdelta) patch's file header says, before any window is decoded.</summary>
public sealed record VcdiffPatchInfo
{
    /// <summary>0 = none, 1 = DJW, 2 = LZMA, 16 = FGK (xdelta3's secondary-compressor ids).</summary>
    public required int SecondaryCompressor { get; init; }
    public required bool CustomCodeTable { get; init; }
    /// <summary>xdelta3's application header, when present: "target//source/" style file names.</summary>
    public string? ApplicationHeader { get; init; }
    public required int WindowCount { get; init; }
    public required long TargetSize { get; init; }
    public required bool HasChecksums { get; init; }
    /// <summary>True when at least one window actually has a secondary-compressed section. xdelta3
    /// names a compressor in the header even when it ended up compressing nothing.</summary>
    public required bool UsesSecondaryCompression { get; init; }

    public string SecondaryName => SecondaryCompressor switch
    {
        0 => "none", 1 => "DJW", 2 => "LZMA", 16 => "FGK", var n => $"unknown ({n})",
    };

    /// <summary>True when DiscForge can apply this patch itself; false means DJW/FGK secondary
    /// compression or a custom code table, which need xdelta3 itself (or a front-end for it).</summary>
    public bool Supported => !CustomCodeTable && (!UsesSecondaryCompression || SecondaryCompressor == 2);

    public string Summary() =>
        $"xdelta/VCDIFF · {WindowCount:N0} window(s) → {TargetSize:N0} bytes" +
        (UsesSecondaryCompression ? $" · {SecondaryName} compressed" : "") +
        (HasChecksums ? " · Adler-32 per window" : "") +
        (CustomCodeTable ? " · custom code table" : "");
}

/// <summary>
/// Clean-room VCDIFF decoder (RFC 3284), plus the two xdelta3 extensions every real xdelta patch
/// uses: a per-window Adler-32 of the decoded target (window indicator bit 2), and LZMA secondary
/// compression of the data/instruction/address sections (each section then holds its decoded size as
/// a VCDIFF integer followed by the next piece of one long-running .xz stream per section kind — the
/// first window's piece opens the stream, later windows' pieces continue it, sync-flushed at each
/// window's end; established by decoding patches xdelta3 itself produced, not from its source). xdelta3's DJW and FGK secondary compressors are not implemented;
/// <see cref="VcdiffPatchInfo.Supported"/> says so up front so a caller can point the user at xdelta3.
///
/// Applies window by window, streaming: the source is read by seeking, and the target is written
/// window by window to a read/write stream (a VCD_TARGET window copies from target already written),
/// so images larger than 2 GB — DVD-size PS2 discs are a common xdelta target — never need to fit in
/// memory.
/// </summary>
public static class VcdiffPatch
{
    private const byte VcdSecondary = 0x01, VcdCodeTable = 0x02, VcdAppHeader = 0x04;
    private const byte VcdSource = 0x01, VcdTarget = 0x02, VcdAdler32 = 0x04;

    public static bool HasMagic(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0xD6 && data[1] == 0xC3 && data[2] == 0xC4 && data[3] == 0x00;

    /// <summary>Read the file header and walk every window's header (without decoding) to count
    /// windows and total the target size.</summary>
    public static VcdiffPatchInfo Inspect(byte[] patch)
    {
        var r = new Reader(patch);
        var (secondary, customTable, app) = ReadFileHeader(ref r);
        int windows = 0; long target = 0; bool sums = false, compressed = false;
        while (!r.AtEnd)
        {
            var w = ReadWindowHeader(ref r);
            windows++;
            target += w.TargetLength;
            sums |= w.HasAdler;
            compressed |= w.DeltaIndicator != 0;
            r.Skip(w.DataLength + w.InstLength + w.AddrLength);
        }
        return new VcdiffPatchInfo
        {
            SecondaryCompressor = secondary, CustomCodeTable = customTable, ApplicationHeader = app,
            WindowCount = windows, TargetSize = target, HasChecksums = sums, UsesSecondaryCompression = compressed,
        };
    }

    public static VcdiffPatchInfo InspectFile(string path) => Inspect(File.ReadAllBytes(path));

    /// <summary>
    /// Apply <paramref name="patch"/> to <paramref name="source"/> (seekable, read), writing the
    /// result to <paramref name="target"/> (seekable, read/write — it is truncated first).
    /// Returns the number of bytes written. With <paramref name="verifyChecksums"/> each window's
    /// Adler-32 is checked as it is written, so a wrong source image is caught at the first window
    /// that disagrees rather than producing a silently broken result.
    /// </summary>
    public static long Apply(byte[] patch, Stream source, Stream target, bool verifyChecksums = true,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!source.CanSeek || !source.CanRead) throw new ArgumentException("Source must be readable and seekable.", nameof(source));
        if (!target.CanSeek || !target.CanWrite || !target.CanRead) throw new ArgumentException("Target must be readable, writable and seekable.", nameof(target));

        var r = new Reader(patch);
        var (secondary, customTable, _) = ReadFileHeader(ref r);
        if (customTable) throw new VcdiffFormatException("This patch uses a custom VCDIFF code table, which DiscForge does not support.");

        target.SetLength(0);
        long written = 0;
        // xdelta3 keeps one compressed stream per section kind for the whole patch (see XzStreamDecoder).
        var dataXz = new XzStreamDecoder();
        var instXz = new XzStreamDecoder();
        var addrXz = new XzStreamDecoder();
        int windowNo = 0;
        while (!r.AtEnd)
        {
            windowNo++;
            var w = ReadWindowHeader(ref r);
            var data = r.Take(w.DataLength);
            var inst = r.Take(w.InstLength);
            var addr = r.Take(w.AddrLength);
            if (w.DeltaIndicator != 0)
            {
                if (secondary != 2)
                    throw new VcdiffFormatException(secondary == 0
                        ? $"Window {windowNo} is secondary-compressed but the header names no compressor."
                        : $"This patch uses xdelta3's {(secondary == 1 ? "DJW" : secondary == 16 ? "FGK" : $"#{secondary}")} " +
                          "secondary compression, which DiscForge does not decode. Apply it with xdelta3 (or a front-end " +
                          "such as Delta Patcher / xdelta UI).");
                if ((w.DeltaIndicator & 1) != 0) data = Decompress(dataXz, data, windowNo, "data");
                if ((w.DeltaIndicator & 2) != 0) inst = Decompress(instXz, inst, windowNo, "instruction");
                if ((w.DeltaIndicator & 4) != 0) addr = Decompress(addrXz, addr, windowNo, "address");
            }

            // The copy source for this window: a segment of the source file, or of target already written.
            byte[] segment = Array.Empty<byte>();
            if (w.Indicator is VcdSource or VcdTarget)
            {
                var from = w.Indicator == VcdSource ? source : target;
                if (w.SegmentPosition < 0 || w.SegmentPosition + w.SegmentLength > from.Length)
                    throw new VcdiffFormatException(
                        $"Window {windowNo} copies bytes {w.SegmentPosition:N0}–{w.SegmentPosition + w.SegmentLength:N0} " +
                        $"of the {(w.Indicator == VcdSource ? "source image" : "output")}, which is only {from.Length:N0} bytes — " +
                        "wrong source file?");
                segment = new byte[w.SegmentLength];
                long keep = from.Position;
                from.Position = w.SegmentPosition;
                from.ReadExactly(segment);
                from.Position = keep;
            }

            var window = DecodeWindow(segment, data, inst, addr, w.TargetLength, windowNo);
            if (verifyChecksums && w.HasAdler && Adler32(window) != w.Adler)
                throw new VcdiffFormatException(
                    $"Window {windowNo} decoded to the wrong bytes (Adler-32 mismatch) — the image is not the one this patch was made from.");

            target.Position = written;
            target.Write(window);
            written += window.Length;
            progress?.Report((double)r.Position / patch.Length);
        }
        target.Flush();
        return written;
    }

    /// <summary>In-memory convenience for small files and tests.</summary>
    public static byte[] Apply(byte[] patch, byte[] source, bool verifyChecksums = true)
    {
        using var src = new MemoryStream(source, writable: false);
        using var dst = new MemoryStream();
        Apply(patch, src, dst, verifyChecksums);
        return dst.ToArray();
    }

    // ---- header parsing ------------------------------------------------------------------------

    private static (int secondary, bool customTable, string? app) ReadFileHeader(ref Reader r)
    {
        if (!HasMagic(r.Remaining)) throw new VcdiffFormatException("Not a VCDIFF/xdelta patch (bad magic).");
        r.Skip(4);
        byte ind = r.Byte();
        if ((ind & ~(VcdSecondary | VcdCodeTable | VcdAppHeader)) != 0)
            throw new VcdiffFormatException($"Unknown VCDIFF header indicator bits 0x{ind:X2}.");
        int secondary = (ind & VcdSecondary) != 0 ? r.Byte() : 0;
        bool customTable = (ind & VcdCodeTable) != 0;
        if (customTable)
        {
            // Length-prefixed; skipped so Inspect can still report the rest.
            r.Skip(checked((int)r.Integer()));
        }
        string? app = null;
        if ((ind & VcdAppHeader) != 0)
        {
            int len = checked((int)r.Integer());
            app = System.Text.Encoding.UTF8.GetString(r.Take(len)).TrimEnd('\0');
        }
        return (secondary, customTable, app);
    }

    private readonly record struct WindowHeader(
        byte Indicator, long SegmentLength, long SegmentPosition, int TargetLength, byte DeltaIndicator,
        int DataLength, int InstLength, int AddrLength, bool HasAdler, uint Adler);

    private static WindowHeader ReadWindowHeader(ref Reader r)
    {
        byte ind = r.Byte();
        if ((ind & ~(VcdSource | VcdTarget | VcdAdler32)) != 0 || (ind & 3) == 3)
            throw new VcdiffFormatException($"Invalid VCDIFF window indicator 0x{ind:X2}.");
        long segLen = 0, segPos = 0;
        if ((ind & 3) != 0) { segLen = r.Integer(); segPos = r.Integer(); }
        long deltaLen = r.Integer();
        int deltaStart = r.Position;
        long targetLen = r.Integer();
        byte deltaInd = r.Byte();
        if ((deltaInd & ~7) != 0) throw new VcdiffFormatException($"Invalid VCDIFF delta indicator 0x{deltaInd:X2}.");
        long dataLen = r.Integer(), instLen = r.Integer(), addrLen = r.Integer();
        bool hasAdler = (ind & VcdAdler32) != 0;
        uint adler = 0;
        if (hasAdler) adler = r.BigEndian32();
        if (targetLen > int.MaxValue || segLen > int.MaxValue)
            throw new VcdiffFormatException("VCDIFF window too large.");
        if (r.Position - deltaStart + dataLen + instLen + addrLen != deltaLen)
            throw new VcdiffFormatException("VCDIFF window length fields disagree — truncated or corrupt patch.");
        return new WindowHeader((byte)(ind & 3), segLen, segPos, (int)targetLen, deltaInd,
            checked((int)dataLen), checked((int)instLen), checked((int)addrLen), hasAdler, adler);
    }

    private static byte[] Decompress(XzStreamDecoder xz, byte[] section, int windowNo, string what)
    {
        var r = new Reader(section);
        long size = r.Integer();
        if (size > int.MaxValue) throw new VcdiffFormatException($"Window {windowNo}: {what} section too large.");
        var body = r.Remaining;
        try
        {
            var output = new byte[size];
            xz.Decode(body, output);
            return output;
        }
        catch (InvalidDataException ex)
        {
            throw new VcdiffFormatException($"Window {windowNo}: could not decompress the {what} section ({ex.Message}).");
        }
    }

    // ---- window decoding (RFC 3284 §5–6) -------------------------------------------------------

    private const int Noop = 0, Add = 1, Run = 2, Copy = 3;
    private const int NearSize = 4, SameSize = 3;

    private readonly record struct Inst(byte Type1, byte Size1, byte Mode1, byte Type2, byte Size2, byte Mode2);

    private static readonly Inst[] CodeTable = BuildDefaultCodeTable();

    /// <summary>RFC 3284 §5.6 default code table.</summary>
    private static Inst[] BuildDefaultCodeTable()
    {
        var t = new List<Inst>(256) { new(Run, 0, 0, Noop, 0, 0) };
        for (byte size = 0; size <= 17; size++) t.Add(new(Add, size, 0, Noop, 0, 0));
        for (byte mode = 0; mode <= 8; mode++)
        {
            t.Add(new(Copy, 0, mode, Noop, 0, 0));
            for (byte size = 4; size <= 18; size++) t.Add(new(Copy, size, mode, Noop, 0, 0));
        }
        for (byte mode = 0; mode <= 5; mode++)
            for (byte add = 1; add <= 4; add++)
                for (byte copy = 4; copy <= 6; copy++)
                    t.Add(new(Add, add, 0, Copy, copy, mode));
        for (byte mode = 6; mode <= 8; mode++)
            for (byte add = 1; add <= 4; add++)
                t.Add(new(Add, add, 0, Copy, 4, mode));
        for (byte mode = 0; mode <= 8; mode++) t.Add(new(Copy, 4, mode, Add, 1, 0));
        if (t.Count != 256) throw new InvalidOperationException("VCDIFF code table construction is wrong.");
        return t.ToArray();
    }

    private static byte[] DecodeWindow(byte[] segment, byte[] data, byte[] inst, byte[] addr, int targetLength, int windowNo)
    {
        var output = new byte[targetLength];
        int outPos = 0;
        var dr = new Reader(data);
        var ir = new Reader(inst);
        var ar = new Reader(addr);
        var near = new long[NearSize];
        var same = new long[SameSize * 256];
        int nextSlot = 0;
        long segLen = segment.Length;

        void Execute(byte type, long size, byte mode, ref Reader dr, ref Reader ir, ref Reader ar)
        {
            if (type == Noop) return;
            if (size == 0) size = ir.Integer();
            if (outPos + size > targetLength)
                throw new VcdiffFormatException($"Window {windowNo}: an instruction writes past the window's end.");
            int n = (int)size;
            switch (type)
            {
                case Add:
                    dr.Take(n).CopyTo(output, outPos);
                    outPos += n;
                    break;
                case Run:
                    output.AsSpan(outPos, n).Fill(dr.Byte());
                    outPos += n;
                    break;
                case Copy:
                {
                    long here = segLen + outPos;
                    long a = mode switch
                    {
                        0 => ar.Integer(),
                        1 => here - ar.Integer(),
                        >= 2 and < 2 + NearSize => near[mode - 2] + ar.Integer(),
                        _ => same[(mode - (2 + NearSize)) * 256 + ar.Byte()],
                    };
                    if (a < 0 || a >= here)
                        throw new VcdiffFormatException($"Window {windowNo}: a copy address points outside the window.");
                    near[nextSlot] = a; nextSlot = (nextSlot + 1) % NearSize;
                    same[a % (SameSize * 256)] = a;

                    for (int i = 0; i < n; i++, a++)
                        output[outPos++] = a < segLen ? segment[a] : output[a - segLen];
                    break;
                }
                default:
                    throw new VcdiffFormatException($"Window {windowNo}: invalid instruction type {type}.");
            }
        }

        while (!ir.AtEnd)
        {
            var e = CodeTable[ir.Byte()];
            Execute(e.Type1, e.Size1, e.Mode1, ref dr, ref ir, ref ar);
            Execute(e.Type2, e.Size2, e.Mode2, ref dr, ref ir, ref ar);
        }
        if (outPos != targetLength)
            throw new VcdiffFormatException($"Window {windowNo}: decoded {outPos:N0} of {targetLength:N0} bytes.");
        return output;
    }

    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        int i = 0;
        while (i < data.Length)
        {
            int chunk = Math.Min(5552, data.Length - i);
            for (int k = 0; k < chunk; k++) { a += data[i + k]; b += a; }
            a %= Mod; b %= Mod;
            i += chunk;
        }
        return (b << 16) | a;
    }

    // ---- byte reader ---------------------------------------------------------------------------

    private struct Reader
    {
        private readonly byte[] _buf;
        public int Position;

        public Reader(byte[] buf) { _buf = buf; Position = 0; }

        public readonly bool AtEnd => Position >= _buf.Length;
        public readonly ReadOnlySpan<byte> Remaining => _buf.AsSpan(Position);

        public byte Byte()
        {
            if (Position >= _buf.Length) throw new VcdiffFormatException("VCDIFF data ended early.");
            return _buf[Position++];
        }

        public void Skip(int n)
        {
            if (n < 0 || Position + n > _buf.Length) throw new VcdiffFormatException("VCDIFF data ended early.");
            Position += n;
        }

        public byte[] Take(int n)
        {
            if (n < 0 || Position + n > _buf.Length) throw new VcdiffFormatException("VCDIFF data ended early.");
            var r = _buf.AsSpan(Position, n).ToArray();
            Position += n;
            return r;
        }

        /// <summary>RFC 3284 integer: base-128, most significant digit first.</summary>
        public long Integer()
        {
            long v = 0;
            for (int i = 0; i < 10; i++)
            {
                byte b = Byte();
                v = (v << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0) return v;
            }
            throw new VcdiffFormatException("VCDIFF integer too long.");
        }

        public uint BigEndian32()
        {
            uint v = 0;
            for (int i = 0; i < 4; i++) v = (v << 8) | Byte();
            return v;
        }
    }
}
