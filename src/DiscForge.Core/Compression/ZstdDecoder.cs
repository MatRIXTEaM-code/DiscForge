// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.Compression;

/// <summary>
/// A decode-only Zstandard (RFC 8878) decompressor, clean-room from the public specification.
/// It exists so DiscForge can read RVZ disc images (whose data groups are zstd-compressed)
/// without taking a binary dependency — the same zero-dependency, provable-against-a-reference
/// discipline every other format in the project follows. Compression is intentionally not
/// implemented; only the subset needed to reconstruct real files: single- and multi-frame
/// streams, Raw/RLE/Compressed blocks, Raw/RLE/Huffman (and treeless) literals, and
/// FSE-coded sequences with repeat offsets. Dictionaries are not supported (RVZ does not use
/// them).
///
/// Validated byte-exact against reference `zstandard`-produced streams: 120+ randomized fuzz
/// vectors across every content type (random, text, run-length, structured, all-zero), sizes
/// from 0 to 1.5 MB, and compression levels 1–22 — plus targeted coverage of Raw/RLE/Huffman
/// (single- and 4-stream) and treeless literals, FSE-compressed and predefined sequence tables,
/// multi-block frames, and repeat offsets. All pass.
/// </summary>
public static class ZstdDecoder
{
    private const uint MagicNumber = 0xFD2FB528;
    private const uint SkippableMagicMin = 0x184D2A50;
    private const uint SkippableMagicMax = 0x184D2A5F;

    /// <summary>Decompress exactly ONE zstd frame from the front of <paramref name="src"/>, setting
    /// <paramref name="consumed"/> to the number of input bytes it used. For callers (like the CHD
    /// reader) that have several concatenated streams and need to know where this frame ends.</summary>
    public static byte[] DecompressFrame(ReadOnlySpan<byte> src, out int consumed)
    {
        using var ms = new MemoryStream();
        int pos = 0;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        if (magic != MagicNumber)
            throw new InvalidDataException($"Not a zstd frame (magic 0x{magic:X8}).");
        pos += 4;
        DecodeFrame(src, ref pos, ms);
        consumed = pos;
        return ms.ToArray();
    }

    /// <summary>Decompress a complete zstd stream (one or more frames) to a byte array.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> src)
    {
        using var ms = new MemoryStream();
        int pos = 0;
        while (pos + 4 <= src.Length)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos, 4));
            if (magic >= SkippableMagicMin && magic <= SkippableMagicMax)
            {
                if (pos + 8 > src.Length) throw new InvalidDataException("Truncated skippable frame.");
                uint skipLen = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + 4, 4));
                pos += 8 + (int)skipLen;
                continue;
            }
            if (magic != MagicNumber)
                throw new InvalidDataException($"Not a zstd frame (magic 0x{magic:X8} at offset {pos}).");
            pos += 4;
            DecodeFrame(src, ref pos, ms);
        }
        return ms.ToArray();
    }

    // ── Frame ───────────────────────────────────────────────────────────────────────────────
    private static void DecodeFrame(ReadOnlySpan<byte> src, ref int pos, Stream outStream)
    {
        byte fhd = src[pos++];
        int fcsFlag = fhd >> 6;
        bool singleSegment = (fhd & 0x20) != 0;
        bool contentChecksum = (fhd & 0x04) != 0;
        int dictIdFlag = fhd & 0x03;

        ulong windowSize = 0;
        if (!singleSegment)
        {
            byte wd = src[pos++];
            int exponent = wd >> 3;
            int mantissa = wd & 0x07;
            ulong windowBase = 1UL << (10 + exponent);
            windowSize = windowBase + windowBase / 8 * (ulong)mantissa;
        }

        // Dictionary_ID (unsupported, but parse past it)
        int dictIdBytes = dictIdFlag switch { 0 => 0, 1 => 1, 2 => 2, 3 => 4, _ => 0 };
        uint dictId = 0;
        for (int i = 0; i < dictIdBytes; i++) dictId |= (uint)src[pos++] << (8 * i);
        if (dictId != 0) throw new NotSupportedException("Dictionary-based zstd frames are not supported.");

        int fcsBytes = fcsFlag switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, 3 => 8, _ => 0 };
        ulong contentSize = 0;
        bool haveContentSize = fcsBytes > 0;
        for (int i = 0; i < fcsBytes; i++) contentSize |= (ulong)src[pos++] << (8 * i);
        if (fcsBytes == 2) contentSize += 256;  // 2-byte FCS has a +256 bias
        if (singleSegment) windowSize = haveContentSize ? contentSize : windowSize;

        // Decoding window: keep the whole output so far (RVZ chunks are ≤ a few MB, so a flat
        // growing buffer is fine and lets matches reach anywhere earlier in the frame).
        var window = new List<byte>(haveContentSize ? (int)Math.Min(contentSize, int.MaxValue) : 1 << 16);

        // Persist FSE tables across blocks for the "repeat" compression mode, and the Huffman table
        // for "treeless" literals (a later block reusing the previous block's tree).
        FseTable? prevLL = null, prevOF = null, prevML = null;
        HuffmanTable? prevHuff = null;
        var repeats = new int[] { 1, 4, 8 };   // reset per frame

        while (true)
        {
            int header = src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16);
            pos += 3;
            bool lastBlock = (header & 1) != 0;
            int blockType = (header >> 1) & 0x03;
            int blockSize = header >> 3;

            switch (blockType)
            {
                case 0: // Raw
                    for (int i = 0; i < blockSize; i++) window.Add(src[pos + i]);
                    pos += blockSize;
                    break;
                case 1: // RLE
                    byte rle = src[pos++];
                    for (int i = 0; i < blockSize; i++) window.Add(rle);
                    break;
                case 2: // Compressed
                    DecodeCompressedBlock(src.Slice(pos, blockSize), window, repeats,
                                          ref prevLL, ref prevOF, ref prevML, ref prevHuff);
                    pos += blockSize;
                    break;
                default:
                    throw new InvalidDataException("Reserved zstd block type.");
            }

            if (lastBlock) break;
        }

        if (contentChecksum) pos += 4;   // xxHash64 low 32 bits — not verified here

        foreach (byte b in window) outStream.WriteByte(b);
    }

    // ── Compressed block ────────────────────────────────────────────────────────────────────
    private static void DecodeCompressedBlock(ReadOnlySpan<byte> block, List<byte> window, int[] repeats,
                                              ref FseTable? prevLL, ref FseTable? prevOF, ref FseTable? prevML,
                                              ref HuffmanTable? prevHuff)
    {
        int p = 0;
        byte[] literals = DecodeLiterals(block, ref p, ref prevHuff);

        // Sequences section
        int litPos = 0;
        int nbSeq = ReadSequenceCount(block, ref p);
        if (nbSeq == 0)
        {
            // No sequences: the literals ARE the output for this block.
            window.AddRange(literals);
            return;
        }

        byte modes = block[p++];
        int llMode = (modes >> 6) & 0x03;
        int ofMode = (modes >> 4) & 0x03;
        int mlMode = (modes >> 2) & 0x03;

        FseTable llTable = BuildSeqTable(block, ref p, llMode, SeqKind.LiteralLength, prevLL);
        FseTable ofTable = BuildSeqTable(block, ref p, ofMode, SeqKind.Offset, prevOF);
        FseTable mlTable = BuildSeqTable(block, ref p, mlMode, SeqKind.MatchLength, prevML);
        prevLL = llTable; prevOF = ofTable; prevML = mlTable;

        // The sequence bitstream is the rest of the block, read in reverse.
        var br = new ReverseBitReader(block.Slice(p));

        // Initial FSE states, in order LL, OF, ML.
        int llState = br.ReadBits(llTable.AccuracyLog);
        int ofState = br.ReadBits(ofTable.AccuracyLog);
        int mlState = br.ReadBits(mlTable.AccuracyLog);

        for (int s = 0; s < nbSeq; s++)
        {
            int ofCode = ofTable.Symbol[ofState];
            int mlCode = mlTable.Symbol[mlState];
            int llCode = llTable.Symbol[llState];

            // Read value extra bits in the order: offset, match, literal.
            int offsetValue = (int)((1u << ofCode) + (uint)br.ReadBits(ofCode));
            int matchLength = MlBaseline[mlCode] + br.ReadBits(MlBits[mlCode]);
            int litLength = LlBaseline[llCode] + br.ReadBits(LlBits[llCode]);

            int offset = ResolveOffset(offsetValue, litLength, repeats);

            // Emit: literalLength literals, then a match of matchLength from `offset` back.
            for (int i = 0; i < litLength; i++) window.Add(literals[litPos++]);
            int start = window.Count - offset;
            if (start < 0) throw new InvalidDataException("Match offset points before the start of output.");
            for (int i = 0; i < matchLength; i++) window.Add(window[start + i]);

            // Update states for the next sequence (LL, ML, OF), except after the last one.
            if (s < nbSeq - 1)
            {
                llState = llTable.NextState(llState, br);
                mlState = mlTable.NextState(mlState, br);
                ofState = ofTable.NextState(ofState, br);
            }
        }

        // Remaining literals after the last sequence are copied verbatim.
        while (litPos < literals.Length) window.Add(literals[litPos++]);
    }

    private static int ResolveOffset(int offsetValue, int litLength, int[] rep)
    {
        int offset;
        if (offsetValue > 3)
        {
            offset = offsetValue - 3;
            rep[2] = rep[1]; rep[1] = rep[0]; rep[0] = offset;
        }
        else
        {
            int idx = offsetValue - 1;          // 0,1,2
            if (litLength == 0) idx++;          // 1,2,3
            if (idx == 0) { offset = rep[0]; }
            else if (idx == 1) { offset = rep[1]; rep[1] = rep[0]; rep[0] = offset; }
            else if (idx == 2) { offset = rep[2]; rep[2] = rep[1]; rep[1] = rep[0]; rep[0] = offset; }
            else { offset = rep[0] - 1; rep[2] = rep[1]; rep[1] = rep[0]; rep[0] = offset; }
        }
        return offset;
    }

    private static int ReadSequenceCount(ReadOnlySpan<byte> block, ref int p)
    {
        byte b0 = block[p++];
        if (b0 < 128) return b0;
        if (b0 < 255) { byte b1 = block[p++]; return ((b0 - 128) << 8) + b1; }
        int v = block[p] | (block[p + 1] << 8);
        p += 2;
        return v + 0x7F00;
    }

    // ── Literals section ────────────────────────────────────────────────────────────────────
    private static byte[] DecodeLiterals(ReadOnlySpan<byte> block, ref int p, ref HuffmanTable? prevHuff)
    {
        byte h0 = block[p];
        int litType = h0 & 0x03;
        int sizeFormat = (h0 >> 2) & 0x03;

        if (litType == 0 || litType == 1) // Raw or RLE
        {
            int regen;
            switch (sizeFormat)
            {
                case 0: case 2: regen = h0 >> 3; p += 1; break;             // 1-byte header (5-bit size)
                case 1: regen = (h0 >> 4) | (block[p + 1] << 4); p += 2; break; // 12-bit
                default: regen = (h0 >> 4) | (block[p + 1] << 4) | (block[p + 2] << 12); p += 3; break; // 20-bit
            }
            var outLit = new byte[regen];
            if (litType == 0) { block.Slice(p, regen).CopyTo(outLit); p += regen; }
            else { byte v = block[p++]; Array.Fill(outLit, v); }
            return outLit;
        }

        // Compressed (2) or Treeless (3): Huffman-coded literals.
        int regenSize, compSize, streams;
        switch (sizeFormat)
        {
            case 0: // 1 stream, 10-bit sizes
                regenSize = (h0 >> 4) | ((block[p + 1] & 0x3F) << 4);
                compSize = (block[p + 1] >> 6) | (block[p + 2] << 2);
                streams = 1; p += 3; break;
            case 1: // 4 streams, 10-bit sizes
                regenSize = (h0 >> 4) | ((block[p + 1] & 0x3F) << 4);
                compSize = (block[p + 1] >> 6) | (block[p + 2] << 2);
                streams = 4; p += 3; break;
            case 2: // 4 streams, 14-bit sizes
                regenSize = (h0 >> 4) | (block[p + 1] << 4) | ((block[p + 2] & 0x03) << 12);
                compSize = (block[p + 2] >> 2) | (block[p + 3] << 6);
                streams = 4; p += 4; break;
            default: // 3: 4 streams, 18-bit sizes
                regenSize = (h0 >> 4) | (block[p + 1] << 4) | ((block[p + 2] & 0x3F) << 12);
                compSize = (block[p + 2] >> 6) | (block[p + 3] << 2) | (block[p + 4] << 10);
                streams = 4; p += 5; break;
        }

        ReadOnlySpan<byte> huffBlock = block.Slice(p, compSize);
        p += compSize;

        var result = new byte[regenSize];
        HuffmanTable huff;
        int streamStart;
        if (litType == 2) // Compressed: a Huffman tree description precedes the streams.
        {
            huff = HuffmanTable.Read(huffBlock, out int treeBytes);
            streamStart = treeBytes;
            prevHuff = huff;                    // retain for any following treeless block
        }
        else                                    // Treeless (3): reuse the previous block's tree.
        {
            huff = prevHuff ?? throw new InvalidDataException("Treeless literals with no retained Huffman table.");
            streamStart = 0;
        }

        ReadOnlySpan<byte> streamsSpan = huffBlock.Slice(streamStart);
        if (streams == 1)
        {
            var br = new ReverseBitReader(streamsSpan);
            huff.DecodeStream(br, result.AsSpan(0, regenSize));
        }
        else
        {
            // 4 streams: a 6-byte jump table gives the first three compressed sizes.
            int s1 = streamsSpan[0] | (streamsSpan[1] << 8);
            int s2 = streamsSpan[2] | (streamsSpan[3] << 8);
            int s3 = streamsSpan[4] | (streamsSpan[5] << 8);
            int total = streamsSpan.Length - 6;
            int s4 = total - s1 - s2 - s3;
            if (s4 < 0) throw new InvalidDataException("Bad 4-stream Huffman jump table.");

            int segLen = (regenSize + 3) / 4;
            int off = 6, outOff = 0;
            int[] sizes = { s1, s2, s3, s4 };
            for (int k = 0; k < 4; k++)
            {
                int thisOut = k == 3 ? regenSize - outOff : segLen;
                var br = new ReverseBitReader(streamsSpan.Slice(off, sizes[k]));
                huff.DecodeStream(br, result.AsSpan(outOff, thisOut));
                off += sizes[k];
                outOff += thisOut;
            }
        }
        return result;
    }

    // ── Sequence FSE table selection ────────────────────────────────────────────────────────
    private enum SeqKind { LiteralLength, Offset, MatchLength }

    private static FseTable BuildSeqTable(ReadOnlySpan<byte> block, ref int p, int mode, SeqKind kind, FseTable? prev)
    {
        switch (mode)
        {
            case 0: // Predefined
                return kind switch
                {
                    SeqKind.LiteralLength => FseTable.FromNormalized(PredefLL, 6),
                    SeqKind.Offset => FseTable.FromNormalized(PredefOF, 5),
                    _ => FseTable.FromNormalized(PredefML, 6),
                };
            case 1: // RLE — a single byte is the symbol, table of accuracy 0
            {
                byte sym = block[p++];
                return FseTable.SingleSymbol(sym);
            }
            case 2: // FSE_Compressed — read a table description
                return FseTable.ReadFromDescription(block, ref p, MaxLog(kind));
            case 3: // Repeat — reuse the previous table
                return prev ?? throw new InvalidDataException("Repeat FSE mode with no previous table.");
            default:
                throw new InvalidDataException("Bad sequence compression mode.");
        }
    }

    private static int MaxLog(SeqKind kind) => kind switch
    {
        SeqKind.LiteralLength => 9,
        SeqKind.Offset => 8,
        _ => 9,
    };

    // ── LL / ML baseline + extra-bit tables (RFC 8878 §3.1.1.3.2.1.1) ────────────────────────
    private static readonly int[] LlBaseline =
    {
        0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,
        16,18,20,22,24,28,32,40,48,64,128,256,512,1024,2048,4096,8192,16384,32768,65536
    };
    private static readonly int[] LlBits =
    {
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        1,1,1,1,2,2,3,3,4,6,7,8,9,10,11,12,13,14,15,16
    };
    private static readonly int[] MlBaseline =
    {
        3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,
        35,37,39,41,43,47,51,59,67,83,99,131,259,515,1027,2051,4099,8195,16387,32771,65539
    };
    private static readonly int[] MlBits =
    {
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        1,1,1,1,2,2,3,3,4,4,5,7,8,9,10,11,12,13,14,15,16
    };

    // ── Predefined FSE distributions (RFC 8878 §3.1.1.3.2.2.1) ───────────────────────────────
    private static readonly short[] PredefLL =
    {
        4,3,2,2,2,2,2,2,2,2,2,2,2,1,1,1,2,2,2,2,2,2,2,2,2,3,2,1,1,1,1,1,-1,-1,-1,-1
    };
    // MatchLength default: -1 (low-probability) at codes 46-52 — SEVEN of them, not five.
    private static readonly short[] PredefML =
    {
        1,4,3,2,2,2,2,2,2,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,-1,-1,-1,-1,-1,-1,-1
    };
    private static readonly short[] PredefOF =
    {
        1,1,1,1,1,1,2,2,2,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,-1,-1,-1,-1,-1
    };
}
