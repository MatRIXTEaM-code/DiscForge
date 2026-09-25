// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.Chd;

/// <summary>The compression a CHD v5 hunk uses, as recorded in the decoded map.</summary>
public enum ChdHunkType : byte
{
    Codec0 = 0, Codec1 = 1, Codec2 = 2, Codec3 = 3,
    None = 4, Self = 5, Parent = 6,
    /// <summary>An unwritten (sparse) hunk in an uncompressed CHD with no parent:
    /// it reads as all zeros. Only produced by <see cref="ChdMap.DecodeUncompressed"/>.</summary>
    Zero = 7,
}

/// <summary>One decoded hunk-map entry.</summary>
/// <param name="Type">The (resolved) hunk compression type.</param>
/// <param name="Offset">For a compressed/None hunk, the byte offset of its data in the
/// file. For a Self hunk, the hunk NUMBER it copies. For a Parent hunk, the parent
/// unit offset.</param>
/// <param name="Length">Compressed byte length (0 for None/Self/Parent — None is a
/// full hunk, the others carry no data).</param>
/// <param name="Crc">CRC-16 of the hunk's raw (decompressed) data.</param>
public readonly record struct ChdMapEntry(ChdHunkType Type, long Offset, uint Length, ushort Crc);

/// <summary>
/// Decoder for the CHD v5 <b>compressed hunk map</b> — the table that says, for every
/// hunk, which codec it uses and where its data lives. Earlier DiscForge walked the
/// hunk stream by probing each hunk's codec bytes, which cannot resolve SELF hunks
/// (a hunk that says "I am identical to hunk N", carrying no data of its own) or
/// PARENT hunks. This decodes the map proper, so those resolve too.
///
/// The map is a bitstream: a Huffman tree over the 16 compression-type codes
/// (run-length-encoded lengths), then each hunk's type (with two run escapes for long
/// identical runs), then a second pass reading each hunk's compressed length + CRC, or
/// a SELF/PARENT reference. The decoded 12-byte-per-hunk table carries a CRC-16 of
/// itself in the map header, so <see cref="Decode"/> verifies its own output and
/// throws if it does not match — a decode is either provably correct or refused.
///
/// Clean-room implementation of the public CHD v5 map format, checked against its own
/// embedded CRC-16 across chdman-produced CD, hard-disk and parent/child images.
/// </summary>
public static class ChdMap
{
    // Compression type codes (the 16-symbol alphabet). 0–3 codecs, then:
    private const int None = 4, Self = 5, Parent = 6, RleSmall = 7, RleLarge = 8,
                      Self0 = 9, Self1 = 10, ParentSelf = 11, Parent0 = 12, Parent1 = 13;

    /// <summary>
    /// Decode the map at <paramref name="mapOffset"/>. Self-verifies against the map's
    /// stored CRC-16 and throws <see cref="ChdFormatException"/> on any mismatch.
    /// </summary>
    public static ChdMapEntry[] Decode(byte[] chd, long mapOffset, int hunkCount, int hunkBytes, int unitBytes)
    {
        ArgumentNullException.ThrowIfNull(chd);
        if (mapOffset <= 0 || mapOffset + 16 > chd.Length)
            throw new ChdFormatException("CHD map offset is out of range.");
        if (hunkBytes <= 0 || unitBytes <= 0)
            throw new ChdFormatException("CHD hunk or unit size is invalid — the header is corrupt.");
        // Bound the rawmap allocation and hunk loop: a plausible CHD map is far under
        // this. A corrupt header that claims billions of hunks is refused, not honoured.
        if (hunkCount < 0 || (long)hunkCount * 12 > 64L * 1024 * 1024)
            throw new ChdFormatException("CHD hunk count is implausibly large — the header is corrupt.");

        int m = (int)mapOffset;
        uint mapBytes = BinaryPrimitives.ReadUInt32BigEndian(chd.AsSpan(m, 4));
        long firstOffset = 0;
        for (int i = 0; i < 6; i++) firstOffset = (firstOffset << 8) | chd[m + 4 + i];
        ushort mapCrc = BinaryPrimitives.ReadUInt16BigEndian(chd.AsSpan(m + 10, 2));
        int lengthBits = chd[m + 12];
        int selfBits = chd[m + 13];
        int parentBits = chd[m + 14];

        long dataStart = mapOffset + 16;
        if (dataStart + mapBytes > chd.Length)
            throw new ChdFormatException("CHD compressed map runs past the end of the file.");
        var bits = new BitReader(chd, dataStart, mapBytes);

        // --- the type tree: import_tree_rle over 16 codes, maxbits 8 ---
        var tree = new Huffman(ImportTreeRle(bits, numCodes: 16, maxBits: 8), maxBits: 8);

        // --- pass 1: per-hunk compression type, with the two run escapes ---
        var types = new byte[hunkCount];
        int lastComp = 0, repCount = 0;
        for (int h = 0; h < hunkCount; h++)
        {
            if (repCount > 0) { types[h] = (byte)lastComp; repCount--; continue; }
            int val = tree.DecodeOne(bits);
            if (val == RleSmall) { types[h] = (byte)lastComp; repCount = 2 + tree.DecodeOne(bits); }
            else if (val == RleLarge)
            {
                types[h] = (byte)lastComp;
                repCount = 2 + 16 + (tree.DecodeOne(bits) << 4);
                repCount += tree.DecodeOne(bits);
            }
            else types[h] = (byte)(lastComp = val);
        }

        // --- pass 2: resolve each hunk's data reference, building the 12-byte rawmap ---
        var rawmap = new byte[hunkCount * 12];
        var entries = new ChdMapEntry[hunkCount];
        long curOffset = firstOffset;
        long lastSelf = 0, lastParent = 0;
        for (int h = 0; h < hunkCount; h++)
        {
            int t = types[h];
            long offset = curOffset;
            uint length = 0;
            ushort crc = 0;
            switch (t)
            {
                case 0: case 1: case 2: case 3:
                    length = (uint)bits.Read(lengthBits); curOffset += length; crc = (ushort)bits.Read(16);
                    break;
                case None:
                    length = (uint)hunkBytes; curOffset += hunkBytes; crc = (ushort)bits.Read(16);
                    break;
                case Self:
                    lastSelf = offset = bits.Read(selfBits);
                    break;
                case Parent:
                    offset = bits.Read(parentBits); lastParent = offset;
                    break;
                case Self1: lastSelf++; goto case Self0;
                case Self0: t = Self; offset = lastSelf; break;
                case ParentSelf:
                    t = Parent; lastParent = offset = (long)h * hunkBytes / unitBytes; break;
                case Parent1: lastParent += (long)hunkBytes / unitBytes; goto case Parent0;
                case Parent0: t = Parent; offset = lastParent; break;
                default:
                    throw new ChdFormatException($"CHD map has an unknown hunk type {t}.");
            }
            rawmap[h * 12] = (byte)t;
            rawmap[h * 12 + 1] = (byte)(length >> 16); rawmap[h * 12 + 2] = (byte)(length >> 8); rawmap[h * 12 + 3] = (byte)length;
            for (int i = 0; i < 6; i++) rawmap[h * 12 + 4 + i] = (byte)(offset >> (8 * (5 - i)));
            rawmap[h * 12 + 10] = (byte)(crc >> 8); rawmap[h * 12 + 11] = (byte)crc;
            entries[h] = new ChdMapEntry((ChdHunkType)t, offset, length, crc);
        }

        if (Crc16(rawmap) != mapCrc)
            throw new ChdFormatException(
                "The CHD hunk map failed its own CRC-16 check after decoding — the map is corrupt or in a layout " +
                "this build does not handle. Extraction was declined rather than risk wrong data.");

        return entries;
    }

    /// <summary>
    /// Decode the <b>uncompressed</b> CHD v5 map: when all four compressors are "none",
    /// the map at <paramref name="mapOffset"/> is not the Huffman bitstream above but a
    /// flat array of <paramref name="hunkCount"/> big-endian 4-byte entries. Each entry
    /// is the hunk's file offset in units of <paramref name="hunkBytes"/> (so the data
    /// lives at entry × hunkBytes); an entry of 0 is an unwritten hunk — read from the
    /// parent if one is supplied, otherwise all zeros. There is no map CRC and (per
    /// chdman) no SHA-1 to verify against, so the caller must not gate on the raw SHA-1
    /// when it is all-zero.
    ///
    /// Derived by observing chdman <c>createraw --compression none</c> output and
    /// confirming every hunk lands at entry × hunkBytes.
    /// </summary>
    public static ChdMapEntry[] DecodeUncompressed(
        byte[] chd, long mapOffset, int hunkCount, int hunkBytes, int unitBytes, bool hasParent)
    {
        ArgumentNullException.ThrowIfNull(chd);
        if (hunkBytes <= 0 || unitBytes <= 0)
            throw new ChdFormatException("CHD hunk or unit size is invalid — the header is corrupt.");
        if (hunkCount < 0 || (long)hunkCount * 4 > 256L * 1024 * 1024)
            throw new ChdFormatException("CHD hunk count is implausibly large — the header is corrupt.");
        if (mapOffset <= 0 || mapOffset + (long)hunkCount * 4 > chd.Length)
            throw new ChdFormatException("CHD uncompressed map runs past the end of the file.");

        var entries = new ChdMapEntry[hunkCount];
        int m = (int)mapOffset;
        for (int h = 0; h < hunkCount; h++)
        {
            uint blockoffs = BinaryPrimitives.ReadUInt32BigEndian(chd.AsSpan(m + h * 4, 4));
            if (blockoffs != 0)
                entries[h] = new ChdMapEntry(ChdHunkType.None, (long)blockoffs * hunkBytes, (uint)hunkBytes, 0);
            else if (hasParent)
                entries[h] = new ChdMapEntry(ChdHunkType.Parent, (long)h * hunkBytes / unitBytes, 0, 0);
            else
                entries[h] = new ChdMapEntry(ChdHunkType.Zero, 0, 0, 0);
        }
        return entries;
    }

    // import_tree_rle: read numCodes code-lengths, field width by maxBits, value 1 is
    // an escape (double-1 = a literal 1; else the next field is a value repeated
    // read()+3 times).
    private static int[] ImportTreeRle(BitReader bits, int numCodes, int maxBits)
    {
        int numBits = maxBits >= 16 ? 5 : maxBits >= 8 ? 4 : 3;
        var lengths = new int[numCodes];
        int cur = 0;
        while (cur < numCodes)
        {
            int nb = bits.Read(numBits);
            if (nb != 1) { lengths[cur++] = nb; continue; }
            nb = bits.Read(numBits);
            if (nb == 1) { lengths[cur++] = 1; continue; }
            int rep = bits.Read(numBits) + 3;
            while (rep-- > 0 && cur < numCodes) lengths[cur++] = nb;
        }
        return lengths;
    }

    // Canonical Huffman decoder matching MAME's assign_canonical_codes (lengths
    // processed longest-to-shortest) + a bit-at-a-time longest-prefix decode.
    private sealed class Huffman
    {
        private readonly Dictionary<(int len, int code), int> _map = new();
        private readonly int _maxBits;

        public Huffman(int[] lengths, int maxBits)
        {
            _maxBits = maxBits;
            var histo = new int[33];
            foreach (int l in lengths) if (l > 0 && l <= 32) histo[l]++;
            var start = new int[33];
            long curStart = 0;
            for (int codeLen = 32; codeLen > 0; codeLen--)
            {
                start[codeLen] = (int)curStart;
                curStart = (curStart + histo[codeLen]) >> 1;
            }
            var counter = (int[])start.Clone();
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
            throw new ChdFormatException("CHD map bitstream held an invalid Huffman code.");
        }
    }

    // MSB-first bit reader over a slice of the CHD.
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
                if (_bitPos >= _end) throw new ChdFormatException("CHD map bitstream ended early.");
                int bit = (_d[(int)(_bitPos >> 3)] >> (7 - (int)(_bitPos & 7))) & 1;
                v = (v << 1) | bit; _bitPos++;
            }
            return v;
        }
    }

    private static ushort Crc16(byte[] data)
    {
        int c = 0xFFFF;
        foreach (byte b in data)
        {
            c ^= b << 8;
            for (int i = 0; i < 8; i++)
                c = (c & 0x8000) != 0 ? ((c << 1) ^ 0x1021) & 0xFFFF : (c << 1) & 0xFFFF;
        }
        return (ushort)c;
    }
}
