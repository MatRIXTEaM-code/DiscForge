// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DiscForge.Core.Chd;

/// <summary>
/// Writes a CHD v5 image — the inverse of <see cref="ChdHdExtractor"/>/<see cref="ChdExtractor"/>.
/// This creates a hard-disk CHD from a raw image: the data is split into hunks, each
/// stored as raw DEFLATE (the CHD 'zlib' codec) when that is smaller, verbatim (NONE)
/// otherwise, or as a SELF reference when a hunk is identical to an earlier one. The
/// compressed hunk map is encoded in the same v5 format the reader decodes, and the
/// header carries the SHA-1s a reader (chdman included) checks.
///
/// Clean-room, from the public CHD v5 map format and RFC 1951; round-trips against
/// DiscForge's own reader and is accepted by chdman.
/// </summary>
public static class ChdWriter
{
    private const int HeaderLen = 124;

    /// <summary>Create a hard-disk CHD from a raw image.</summary>
    public static byte[] CreateHd(byte[] image, int hunkBytes = 4096, int unitBytes = 512)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Build(image, "zlib", hunkBytes, unitBytes,
                     new List<(uint, byte[])>(), hunk => Deflate(hunk));
    }

    /// <summary>A CD track for <see cref="CreateCd"/>: its content sectors (2352 bytes
    /// each, little-endian for audio as in a BIN) plus type and pregap.</summary>
    public sealed record CdTrackInput(int Number, string ChdType, string SubType, byte[] Sectors,
                                      int Frames, int Pregap);

    private const int Sector = 2352, Subcode = 96, Frame = 2448, FramesPerHunk = 8;

    /// <summary>
    /// Create a CD CHD from decoded tracks. Each track's sectors are laid out as CHD
    /// frames (2352-byte sector + 96-byte sub-channel, zero here as a BIN carries none),
    /// audio byte-swapped to the CHD's big-endian, tracks padded to a 4-frame boundary,
    /// with a metadata descriptor per track. The result round-trips to the same BIN and
    /// is accepted by chdman.
    /// </summary>
    public static byte[] CreateCd(IReadOnlyList<CdTrackInput> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        int hunkBytes = FramesPerHunk * Frame;   // 19584

        // --- build the logical image: frames of [sector][subcode], padded per track ---
        var logical = new MemoryStream();
        var metadata = new List<(uint tag, byte[] data)>();
        foreach (var t in tracks)
        {
            int count = t.Frames + t.Pregap;
            bool audio = t.ChdType.Contains("AUDIO", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                var frame = new byte[Frame];
                Array.Copy(t.Sectors, (long)i * Sector, frame, 0, Sector);
                if (audio)
                    for (int b = 0; b + 1 < Sector; b += 2) (frame[b], frame[b + 1]) = (frame[b + 1], frame[b]);
                logical.Write(frame, 0, Frame);
            }
            // pad the track up to a multiple of four frames
            int pad = RoundUp(count, 4) - count;
            for (int i = 0; i < pad; i++) logical.Write(new byte[Frame], 0, Frame);

            string desc = $"TRACK:{t.Number} TYPE:{t.ChdType} SUBTYPE:{t.SubType} FRAMES:{t.Frames} " +
                          $"PREGAP:{t.Pregap} PGTYPE:{(t.Pregap > 0 ? t.ChdType : "MODE1")} PGSUB:NONE POSTGAP:0\0";
            metadata.Add((FourCc("CHT2"), Encoding.ASCII.GetBytes(desc)));
        }

        return Build(logical.ToArray(), "cdzl", hunkBytes, Frame, metadata, EncodeCdHunk);
    }

    /// <summary>
    /// Create a CD CHD from a BIN/CUE. Reads the cue and its bin file(s) (resolved
    /// relative to <paramref name="cueDir"/>), splitting each bin into its tracks by the
    /// cue's INDEX positions (a pregap between INDEX 00 and 01 is carried as pregap
    /// frames), and builds the CHD.
    /// </summary>
    public static byte[] CreateCdFromBinCue(string cueText, string cueDir)
    {
        ArgumentNullException.ThrowIfNull(cueText);
        ArgumentNullException.ThrowIfNull(cueDir);
        var sheet = DiscForge.Core.Cue.CueSheet.Parse(cueText);
        var tracks = new List<CdTrackInput>();

        // Group tracks by the bin file they live in, in cue order.
        var byFile = new List<(string file, List<DiscForge.Core.Cue.CueTrack> list)>();
        foreach (var t in sheet.Tracks)
        {
            if (byFile.Count == 0 || byFile[^1].file != t.File) byFile.Add((t.File, new()));
            byFile[^1].list.Add(t);
        }

        foreach (var (file, list) in byFile)
        {
            string path = Path.Combine(cueDir, file);
            if (!File.Exists(path))
                throw new FileNotFoundException($"{file}: referenced by the cue but not found.", path);
            byte[] bin = File.ReadAllBytes(path);

            for (int i = 0; i < list.Count; i++)
            {
                var ct = list[i];
                int sectorSize = SectorSize(ct.Type);
                long fileFrames = bin.Length / sectorSize;
                // This track starts at its INDEX 00 (pregap) or INDEX 01, in frames.
                long startFrame = TrackStartFrame(ct);
                long nextStart = i + 1 < list.Count ? TrackStartFrame(list[i + 1]) : fileFrames;
                int total = (int)(nextStart - startFrame);
                if (total <= 0) throw new ChdFormatException($"Track {ct.Number} has a non-positive length in the cue.");

                int pregap = 0;
                var idx0 = ct.Indices.FirstOrDefault(x => x.Number == 0);
                var idx1 = ct.Indices.FirstOrDefault(x => x.Number == 1);
                if (idx0 is not null && idx1 is not null) pregap = (int)(idx1.Time.ToSectors() - idx0.Time.ToSectors());
                pregap = Math.Clamp(pregap, 0, total);

                var sectors = new byte[(long)total * Sector];
                // Copy at the track's stored sector size, widening 2048/2336 to 2352 is
                // out of scope here — the common CHD case is 2352 raw sectors.
                if (sectorSize != Sector)
                    throw new ChdFormatException($"Track {ct.Number} uses {sectorSize}-byte sectors; CHD creation needs 2352-byte (raw) tracks.");
                Array.Copy(bin, startFrame * Sector, sectors, 0, (long)total * Sector);

                tracks.Add(new CdTrackInput(ct.Number, ChdTypeFor(ct.Type),
                    ct.Type == DiscForge.Core.Cue.CueTrackType.Audio ? "RW" : "NONE",
                    sectors, total - pregap, pregap));
            }
        }
        return CreateCd(tracks);
    }

    private static int SectorSize(DiscForge.Core.Cue.CueTrackType t) => t switch
    {
        DiscForge.Core.Cue.CueTrackType.Mode1_2048 => 2048,
        DiscForge.Core.Cue.CueTrackType.Mode2_2336 => 2336,
        _ => 2352,
    };

    private static string ChdTypeFor(DiscForge.Core.Cue.CueTrackType t) => t switch
    {
        DiscForge.Core.Cue.CueTrackType.Audio => "AUDIO",
        DiscForge.Core.Cue.CueTrackType.Mode2_2352 or DiscForge.Core.Cue.CueTrackType.Mode2_2336 => "MODE2_RAW",
        _ => "MODE1_RAW",
    };

    private static long TrackStartFrame(DiscForge.Core.Cue.CueTrack t)
    {
        var idx0 = t.Indices.FirstOrDefault(x => x.Number == 0);
        var idx1 = t.Indices.FirstOrDefault(x => x.Number == 1);
        var start = idx0 ?? idx1;
        return start?.Time.ToSectors() ?? 0;
    }

    // Shared assembly: hunk every block (SELF dedup, codec-or-NONE), encode the map,
    // write the metadata list, and the header with the SHA-1s a reader checks.
    private static byte[] Build(byte[] logical, string compressor, int hunkBytes, int unitBytes,
                                List<(uint tag, byte[] data)> metadata, Func<byte[], byte[]?> compress)
    {
        if (hunkBytes <= 0 || hunkBytes % unitBytes != 0)
            throw new ArgumentException("hunkBytes must be a positive multiple of unitBytes.");

        int numHunks = (int)(((long)logical.Length + hunkBytes - 1) / hunkBytes);
        var body = new MemoryStream();
        var types = new byte[numHunks];
        var lengths = new uint[numHunks];
        var offsets = new long[numHunks];
        var crcs = new ushort[numHunks];
        var seen = new Dictionary<string, int>();
        long fileOffset = HeaderLen;
        int maxLen = 1;

        var hunkBuf = new byte[hunkBytes];
        for (int h = 0; h < numHunks; h++)
        {
            Array.Clear(hunkBuf);
            int copy = (int)Math.Min(hunkBytes, (long)logical.Length - (long)h * hunkBytes);
            Array.Copy(logical, (long)h * hunkBytes, hunkBuf, 0, copy);
            crcs[h] = Crc16(hunkBuf);
            string key = System.Convert.ToHexString(SHA1.HashData(hunkBuf));
            if (seen.TryGetValue(key, out int refHunk))
            {
                types[h] = (byte)ChdHunkType.Self;
                offsets[h] = refHunk;
                continue;
            }
            seen[key] = h;

            byte[]? comp = compress(hunkBuf);
            if (comp is not null && comp.Length < hunkBytes)
            {
                types[h] = (byte)ChdHunkType.Codec0;
                lengths[h] = (uint)comp.Length;
                offsets[h] = fileOffset;
                body.Write(comp, 0, comp.Length);
                fileOffset += comp.Length;
                maxLen = Math.Max(maxLen, comp.Length);
            }
            else
            {
                types[h] = (byte)ChdHunkType.None;
                lengths[h] = (uint)hunkBytes;
                offsets[h] = fileOffset;
                body.Write(hunkBuf, 0, hunkBytes);
                fileOffset += hunkBytes;
            }
        }

        int lengthBits = BitWidth(maxLen);
        int selfBits = Math.Max(1, BitWidth(Math.Max(1, numHunks - 1)));
        byte[] mapData = MapEncoder.Encode(types, lengths, offsets, crcs, numHunks, hunkBytes, unitBytes,
                                           firstOffset: HeaderLen, lengthBits, selfBits, out ushort mapCrc);

        byte[] bodyBytes = body.ToArray();
        long metaOffset = metadata.Count > 0 ? HeaderLen + bodyBytes.Length : 0;
        byte[] metaBytes = EncodeMetadata(metadata, metaOffset);
        long mapOffset = HeaderLen + bodyBytes.Length + metaBytes.Length;

        var header = new byte[HeaderLen];
        WriteAscii(header, 0, "MComprHD");
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x08), HeaderLen);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x0C), 5);
        WriteAscii(header, 0x10, compressor);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x20), (ulong)logical.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x28), (ulong)mapOffset);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(0x30), (ulong)metaOffset);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x38), (uint)hunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x3C), (uint)unitBytes);

        byte[] rawSha1 = SHA1.HashData(logical);
        rawSha1.CopyTo(header.AsSpan(0x40));
        ComputeOverallSha1(rawSha1, metadata).CopyTo(header.AsSpan(0x54));

        var outStream = new MemoryStream();
        outStream.Write(header);
        outStream.Write(bodyBytes);
        outStream.Write(metaBytes);
        var mapHeader = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(mapHeader.AsSpan(0), (uint)mapData.Length);
        for (int i = 0; i < 6; i++) mapHeader[4 + i] = (byte)(((long)HeaderLen) >> (8 * (5 - i)));
        BinaryPrimitives.WriteUInt16BigEndian(mapHeader.AsSpan(10), mapCrc);
        mapHeader[12] = (byte)lengthBits;
        mapHeader[13] = (byte)selfBits;
        outStream.Write(mapHeader);
        outStream.Write(mapData);
        return outStream.ToArray();
    }

    private static int RoundUp(int v, int m) => (v + m - 1) / m * m;
    private static uint FourCc(string s) => (uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]);

    // Metadata linked list: [tag:4][flags(1)+len(3):4][next:8][data]. The CHECKSUM flag
    // (0x01) marks an entry for inclusion in the overall SHA-1.
    private static byte[] EncodeMetadata(List<(uint tag, byte[] data)> items, long baseOffset)
    {
        var ms = new MemoryStream();
        long at = baseOffset;
        for (int i = 0; i < items.Count; i++)
        {
            var (tag, data) = items[i];
            int entryLen = 16 + data.Length;
            long next = i + 1 < items.Count ? at + entryLen : 0;
            var head = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(0), tag);
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (0x01u << 24) | (uint)data.Length);
            BinaryPrimitives.WriteUInt64BigEndian(head.AsSpan(8), (ulong)next);
            ms.Write(head); ms.Write(data);
            at += entryLen;
        }
        return ms.ToArray();
    }

    // Overall SHA-1 (CHD v5): SHA-1 of the raw SHA-1 followed by, for each checksummed
    // metadata entry sorted ascending, its 4-byte tag then the SHA-1 of its data.
    private static byte[] ComputeOverallSha1(byte[] rawSha1, List<(uint tag, byte[] data)> metadata)
    {
        var entries = new List<byte[]>();
        foreach (var (tag, data) in metadata)
        {
            var blob = new byte[4 + 20];                 // tag + SHA-1(data)
            BinaryPrimitives.WriteUInt32BigEndian(blob, tag);
            SHA1.HashData(data).CopyTo(blob.AsSpan(4));
            entries.Add(blob);
        }
        entries.Sort(CompareBytes);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(rawSha1);
        foreach (var e in entries) sha.AppendData(e);
        return sha.GetHashAndReset();
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length && i < b.Length; i++) if (a[i] != b[i]) return a[i] - b[i];
        return a.Length - b.Length;
    }

    // A cdzl CD hunk: [ecc flags=0][base length:2][deflate(sectors)][deflate(subcode)].
    // ECC is left intact (flags 0), so the reader copies sectors verbatim.
    private static byte[]? EncodeCdHunk(byte[] hunk)
    {
        int frames = hunk.Length / Frame;
        var sectors = new byte[frames * Sector];
        var subs = new byte[frames * Subcode];
        for (int f = 0; f < frames; f++)
        {
            Array.Copy(hunk, f * Frame, sectors, f * Sector, Sector);
            Array.Copy(hunk, f * Frame + Sector, subs, f * Subcode, Subcode);
        }
        byte[] baseStream = Deflate(sectors);
        byte[] subStream = Deflate(subs);
        if (baseStream.Length > 0xFFFF) return null;   // base length field is 16-bit
        // The reader tells cdzl (DEFLATE) from cdlz (LZMA) by the base's first byte:
        // 0x00 means LZMA. A DEFLATE stored block can start with 0x00, so fall back to
        // an uncompressed hunk rather than emit an ambiguous one.
        if (baseStream.Length == 0 || baseStream[0] == 0x00) return null;
        var ms = new MemoryStream();
        ms.WriteByte(0);                                // ecc flags
        ms.WriteByte((byte)(baseStream.Length >> 8));
        ms.WriteByte((byte)baseStream.Length);
        ms.Write(baseStream); ms.Write(subStream);
        return ms.ToArray();
    }

    // Raw DEFLATE of a hunk (the CHD 'zlib' codec is header-less deflate).
    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            ds.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static int BitWidth(int v) { int b = 0; while (v > 0) { b++; v >>= 1; } return b; }

    private static void WriteAscii(byte[] b, int at, string s)
    {
        for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i];
    }

    private static ushort Crc16(byte[] data)
    {
        int c = 0xFFFF;
        foreach (byte b in data)
        {
            c ^= b << 8;
            for (int i = 0; i < 8; i++) c = (c & 0x8000) != 0 ? ((c << 1) ^ 0x1021) & 0xFFFF : (c << 1) & 0xFFFF;
        }
        return (ushort)c;
    }

    // Encodes the CHD v5 compressed hunk map (the inverse of ChdMap.Decode).
    private static class MapEncoder
    {
        private const int RleSmall = 7, RleLarge = 8;

        public static byte[] Encode(byte[] types, uint[] lengths, long[] offsets, ushort[] crcs,
                                    int numHunks, int hunkBytes, int unitBytes,
                                    long firstOffset, int lengthBits, int selfBits, out ushort mapCrc)
        {
            // 1. Build the symbol stream (types + RLE escapes + RLE count symbols).
            // The decoder consumes the RLE symbol's own hunk slot too, so one RLE covers
            // 1 + repcount hunks: RLE_SMALL covers 3..18 (repcount 2..17), RLE_LARGE
            // covers 19..274. Runs shorter than 3 are emitted as extra literals.
            var symbols = new List<int>();
            int h = 0;
            while (h < numHunks)
            {
                int t = types[h];
                symbols.Add(t);
                h++;
                int run = 0;
                while (h + run < numHunks && types[h + run] == t) run++;
                while (run > 0)
                {
                    if (run < 3) { symbols.Add(t); h++; run--; continue; }
                    int cover = Math.Min(run, 274);
                    if (cover <= 18) { symbols.Add(RleSmall); symbols.Add(cover - 3); }
                    else { int v = cover - 19; symbols.Add(RleLarge); symbols.Add(v >> 4); symbols.Add(v & 0xF); }
                    h += cover; run -= cover;
                }
            }

            // 2. Histogram over the 16 code alphabet and a length-limited Huffman tree.
            var histo = new long[16];
            foreach (int s in symbols) histo[s]++;
            int[] codeLengths = HuffmanLengths(histo, 8);
            var (codeBits, codeLen) = CanonicalCodes(codeLengths, 8);

            // 3. Write the bitstream: tree, then the symbol stream, then per-hunk data.
            var bw = new BitWriter();
            ExportTreeRle(bw, codeLengths, maxBits: 8);
            foreach (int s in symbols) bw.Write(codeBits[s], codeLen[s]);
            for (int i = 0; i < numHunks; i++)
            {
                switch ((ChdHunkType)types[i])
                {
                    case ChdHunkType.Codec0:
                    case ChdHunkType.Codec1:
                    case ChdHunkType.Codec2:
                    case ChdHunkType.Codec3:
                        bw.Write(lengths[i], lengthBits); bw.Write(crcs[i], 16); break;
                    case ChdHunkType.None:
                        bw.Write(crcs[i], 16); break;
                    case ChdHunkType.Self:
                        bw.Write((uint)offsets[i], selfBits); break;
                }
            }

            // 4. The decompressed rawmap that the header CRC covers.
            var rawmap = new byte[numHunks * 12];
            long curOffset = firstOffset;
            for (int i = 0; i < numHunks; i++)
            {
                var tt = (ChdHunkType)types[i];
                uint length = 0; long offset; ushort crc = 0;
                switch (tt)
                {
                    case ChdHunkType.None: length = (uint)hunkBytes; offset = curOffset; curOffset += hunkBytes; crc = crcs[i]; break;
                    case ChdHunkType.Self: offset = offsets[i]; break;
                    default: length = lengths[i]; offset = curOffset; curOffset += length; crc = crcs[i]; break;
                }
                rawmap[i * 12] = types[i];
                rawmap[i * 12 + 1] = (byte)(length >> 16); rawmap[i * 12 + 2] = (byte)(length >> 8); rawmap[i * 12 + 3] = (byte)length;
                for (int k = 0; k < 6; k++) rawmap[i * 12 + 4 + k] = (byte)(offset >> (8 * (5 - k)));
                rawmap[i * 12 + 10] = (byte)(crc >> 8); rawmap[i * 12 + 11] = (byte)crc;
            }
            mapCrc = Crc16(rawmap);
            return bw.ToBytes();
        }

        // export_tree_rle: write each of the code lengths in numBits, using value 1 as
        // an escape (a real length of 1 is written as "1,1").
        private static void ExportTreeRle(BitWriter bw, int[] lengths, int maxBits)
        {
            int numBits = maxBits >= 16 ? 5 : maxBits >= 8 ? 4 : 3;
            foreach (int l in lengths)
            {
                if (l == 1) { bw.Write(1, numBits); bw.Write(1, numBits); }
                else bw.Write((uint)l, numBits);
            }
        }

        // Length-limited Huffman lengths from a histogram (package-merge-free: standard
        // Huffman then a Kraft-limiting pass), matching VorbisEncoder's approach.
        private static int[] HuffmanLengths(long[] freq, int maxLen)
        {
            int n = freq.Length;
            var lengths = new int[n];
            int cap = 2 * n;
            var parent = new int[cap];
            var weight = new long[cap];
            for (int i = 0; i < cap; i++) parent[i] = -1;
            var heap = new List<(long w, int node)>(n);
            void Push((long, int) e) { heap.Add(e); int c = heap.Count - 1; while (c > 0) { int p = (c - 1) / 2; if (heap[p].w <= heap[c].w) break; (heap[p], heap[c]) = (heap[c], heap[p]); c = p; } }
            (long, int) Pop() { var top = heap[0]; var last = heap[^1]; heap.RemoveAt(heap.Count - 1); if (heap.Count > 0) { heap[0] = last; int c = 0; while (true) { int l = 2 * c + 1, r = 2 * c + 2, m = c; if (l < heap.Count && heap[l].w < heap[m].w) m = l; if (r < heap.Count && heap[r].w < heap[m].w) m = r; if (m == c) break; (heap[m], heap[c]) = (heap[c], heap[m]); c = m; } } return top; }
            int used = 0;
            for (int i = 0; i < n; i++) if (freq[i] > 0) { weight[i] = freq[i]; Push((freq[i], i)); used++; }
            if (used == 0) return lengths;
            if (used == 1) { for (int i = 0; i < n; i++) if (freq[i] > 0) lengths[i] = 1; return lengths; }
            int next = n;
            while (heap.Count > 1) { var a = Pop(); var b = Pop(); weight[next] = a.Item1 + b.Item1; parent[a.Item2] = next; parent[b.Item2] = next; Push((weight[next], next)); next++; }
            for (int i = 0; i < n; i++) { if (freq[i] == 0) continue; int d = 0, p = parent[i]; while (p != -1) { d++; p = parent[p]; } lengths[i] = Math.Max(1, d); }
            for (int i = 0; i < n; i++) if (lengths[i] > maxLen) lengths[i] = maxLen;
            long total = 0; foreach (int l in lengths) if (l > 0) total += 1L << (maxLen - l);
            long capK = 1L << maxLen;
            while (total > capK)
            {
                int j = -1, best = -1;
                for (int i = 0; i < n; i++) if (lengths[i] > 0 && lengths[i] < maxLen && lengths[i] > best) { best = lengths[i]; j = i; }
                if (j < 0) break;
                lengths[j]++; total -= 1L << (maxLen - lengths[j]);
            }
            return lengths;
        }

        // Canonical codes matching the decoder's assign_canonical_codes (lengths 32→1).
        private static (uint[] codes, int[] lens) CanonicalCodes(int[] lengths, int maxBits)
        {
            var histo = new int[33];
            foreach (int l in lengths) if (l > 0 && l <= 32) histo[l]++;
            var start = new int[33];
            long curStart = 0;
            for (int codeLen = 32; codeLen > 0; codeLen--) { start[codeLen] = (int)curStart; curStart = (curStart + histo[codeLen]) >> 1; }
            var counter = (int[])start.Clone();
            var codes = new uint[lengths.Length];
            for (int sym = 0; sym < lengths.Length; sym++)
                if (lengths[sym] > 0) codes[sym] = (uint)counter[lengths[sym]]++;
            return (codes, lengths);
        }

        // MSB-first bit writer for the map bitstream.
        private sealed class BitWriter
        {
            private readonly List<byte> _bytes = new();
            private int _cur, _nbits;
            public void Write(uint value, int bits)
            {
                for (int i = bits - 1; i >= 0; i--)
                {
                    _cur = (_cur << 1) | (int)((value >> i) & 1);
                    if (++_nbits == 8) { _bytes.Add((byte)_cur); _cur = 0; _nbits = 0; }
                }
            }
            public byte[] ToBytes()
            {
                if (_nbits > 0) { _cur <<= 8 - _nbits; _bytes.Add((byte)_cur); _cur = 0; _nbits = 0; }
                return _bytes.ToArray();
            }
        }
    }
}
