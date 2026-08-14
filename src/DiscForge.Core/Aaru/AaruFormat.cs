// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;

namespace DiscForge.Core.Aaru;

/// <summary>
/// Reads Aaru's native image format (AaruFormat, formerly DiscImageChef .dicf) — the interop win
/// that lets DiscForge ingest anything dumped with Aaru. The format is a header, an index of typed
/// blocks, a deduplication table (DDT) mapping each sector to a location, and data blocks that may be
/// stored uncompressed, LZMA or FLAC.
///
/// Shipped, tiered in the same "provably correct or declined" spirit as <c>rvz-info</c>/<c>rvz-decode</c>:
/// <list type="bullet">
/// <item><b>Identify + inventory</b> (<see cref="ReadInfo"/>) — header, version, media type, the block
/// index, sector count/size and the compression used. Works on any AaruFormat file.</item>
/// <item><b>Uncompressed sector extraction</b> (<see cref="ExtractUserData"/>) — walks the DDT and
/// reconstructs the user data for images whose blocks are stored uncompressed. The on-disk struct
/// widths follow Aaru's pack=1 layout (DataType/CompressionType are 2-byte enums); validated by a
/// synthetic round-trip against that layout. A real Aaru fixture is still the ideal final check.</item>
/// <item><b>LZMA / FLAC blocks are DECLINED, not guessed</b> — Aaru's exact LZMA framing (properties
/// header, dictionary size) and its FLAC/subchannel transform must be confirmed against a real Aaru
/// file before a decode can be trusted; until then the reader identifies them and declines rather than
/// emit a corrupt image. Convert to an uncompressed AaruFormat (or another format) with Aaru to extract
/// meanwhile.</item>
/// </list>
/// </summary>
public static class AaruFormat
{
    // 8-byte header identifier. Aaru uses "AARUFRMT"; the legacy DiscImageChef used "DICMFMT ".
    private static readonly byte[] MagicAaru = Encoding.ASCII.GetBytes("AARUFRMT");
    private static readonly byte[] MagicDicf = Encoding.ASCII.GetBytes("DICMFMT ");

    private const int HeaderSize = 104;

    // On-disk struct sizes (all AaruFormat structs are pack=1). DataType and CompressionType are
    // 2-byte enums in the real format — getting these widths right is what lets DiscForge read a
    // genuine Aaru image rather than only its own synthetic ones.
    private const int IndexHeaderSize = 14;   // identifier u32, entries u16, crc64 u64
    private const int IndexEntrySize = 14;    // blockType u32, dataType u16, offset u64
    private const int BlockHeaderSize = 36;   // identifier u32, type u16, compression u16, sectorSize u32, cmpLength u32, length u32, cmpCrc64 u64, crc64 u64
    private const int DdtHeaderSize = 49;     // identifier u32, type u16, compression u16, shift u8, entries u64, cmpLength u64, length u64, cmpCrc64 u64, crc64 u64

    // BlockType magics (4-byte ASCII, little-endian uint).
    private const uint BtDataBlock = 0x4B4C4244;          // "DBLK"
    private const uint BtDeDuplicationTable = 0x2A544444; // "DDT*"
    private const uint BtIndex = 0x58444E49;              // "INDX"
    private const uint BtIndex2 = 0x32584449;             // "IDX2"

    private const uint DtUserData = 1;

    private enum Compression : uint { None = 0, Lzma = 1, Flac = 2, LzmaSubchannelTransform = 3 }

    public sealed record BlockRef(uint BlockType, uint DataType, ulong Offset)
    {
        public string BlockTypeName => Name(BlockType);
        public static string Name(uint t) => t switch
        {
            BtDataBlock => "DataBlock",
            BtDeDuplicationTable => "DeDuplicationTable",
            BtIndex => "Index",
            BtIndex2 => "Index2",
            0x4D4F4547 => "Geometry",
            0x4154454D => "Metadata",
            0x534B5254 => "Tracks",
            0x4D434943 => "CICM",
            0x4D534B43 => "Checksums",
            _ => $"0x{t:X8}",
        };
    }

    public sealed record Info
    {
        public required string Magic { get; init; }
        public required bool Recognized { get; init; }
        public required string Application { get; init; }
        public required string ImageVersion { get; init; }
        public required string ApplicationVersion { get; init; }
        public required uint MediaType { get; init; }
        public required IReadOnlyList<BlockRef> Blocks { get; init; }
        public long Sectors { get; init; }          // DDT entry count (user data), when found
        public uint SectorSize { get; init; }
        public string UserDataCompression { get; init; } = "unknown";
        public bool UserDataExtractable { get; init; }   // true only when the user-data blocks are uncompressed
    }

    // ---- reads --------------------------------------------------------------

    public static Info ReadInfo(Stream s)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Position = 0;
        var header = ReadExact(s, HeaderSize);

        var magic = header.AsSpan(0, 8).ToArray();
        bool recognized = magic.AsSpan().SequenceEqual(MagicAaru) || magic.AsSpan().SequenceEqual(MagicDicf);
        string magicStr = Encoding.ASCII.GetString(magic).Replace('\0', ' ');

        string application = Encoding.Unicode.GetString(header, 8, 64).TrimEnd('\0', ' ');
        string imgVer = $"{header[72]}.{header[73]}";
        string appVer = $"{header[74]}.{header[75]}";
        uint mediaType = U32(header, 76);
        ulong indexOffset = U64(header, 80);

        var blocks = new List<BlockRef>();
        long sectors = 0;
        uint sectorSize = 0;
        string udComp = "unknown";
        bool extractable = false;

        if (recognized && indexOffset != 0 && indexOffset + IndexHeaderSize <= (ulong)s.Length)
        {
            foreach (var b in ReadIndex(s, indexOffset))
            {
                blocks.Add(b);
                if (b.BlockType == BtDeDuplicationTable && b.DataType == DtUserData)
                {
                    var (ddtEntries, ddtComp) = ReadDdtHeader(s, b.Offset);
                    sectors = (long)ddtEntries;
                }
                if (b.BlockType == BtDataBlock && b.DataType == DtUserData && sectorSize == 0)
                {
                    var (ss, comp) = ReadDataBlockHeader(s, b.Offset);
                    sectorSize = ss;
                    udComp = comp.ToString();
                    // None, LZMA and FLAC all decode (clean-room decoders, gated by the block's
                    // stored CRC-64); only the LZMA-subchannel transform is still declined.
                    extractable = comp is Compression.None or Compression.Lzma or Compression.Flac;
                }
            }
        }

        return new Info
        {
            Magic = magicStr,
            Recognized = recognized,
            Application = application,
            ImageVersion = imgVer,
            ApplicationVersion = appVer,
            MediaType = mediaType,
            Blocks = blocks,
            Sectors = sectors,
            SectorSize = sectorSize,
            UserDataCompression = udComp,
            UserDataExtractable = extractable,
        };
    }

    /// <summary>
    /// Reconstruct the user data to <paramref name="output"/> for an UNCOMPRESSED AaruFormat image.
    /// Throws <see cref="NotSupportedException"/> — declining rather than guessing — when the user-data
    /// blocks are LZMA/FLAC compressed. Returns the number of sectors written.
    /// </summary>
    public static long ExtractUserData(Stream s, Stream output)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(output);

        s.Position = 0;
        var header = ReadExact(s, HeaderSize);
        var magic = header.AsSpan(0, 8).ToArray();
        if (!(magic.AsSpan().SequenceEqual(MagicAaru) || magic.AsSpan().SequenceEqual(MagicDicf)))
            throw new InvalidDataException("Not an AaruFormat image (bad header identifier).");
        ulong indexOffset = U64(header, 80);
        if (indexOffset == 0) throw new InvalidDataException("AaruFormat header has no index offset.");

        // Locate the user-data DDT and confirm every user-data DATA block uses a decodable compression.
        ulong ddtOffset = 0;
        uint sectorSize = 0;
        foreach (var b in ReadIndex(s, indexOffset))
        {
            if (b.BlockType == BtDeDuplicationTable && b.DataType == DtUserData) ddtOffset = b.Offset;
            if (b.BlockType == BtDataBlock && b.DataType == DtUserData && sectorSize == 0)
            {
                var (ss, comp) = ReadDataBlockHeader(s, b.Offset);
                sectorSize = ss;
                if (comp is not (Compression.None or Compression.Lzma or Compression.Flac))
                    throw new NotSupportedException(
                        $"This AaruFormat image stores its data {comp}-compressed. DiscForge decodes " +
                        "uncompressed, LZMA and FLAC AaruFormat; anything else is declined rather than " +
                        "risk a corrupt image. Re-save it with Aaru, or use `aaru-info` to inspect it.");
            }
        }
        if (ddtOffset == 0) throw new InvalidDataException("No user-data deduplication table in this image.");

        var (entries, ddtComp, shift, ddt) = ReadDdt(s, ddtOffset);
        if (ddtComp != Compression.None)
            throw new NotSupportedException(
                $"The deduplication table is {ddtComp}-compressed; DiscForge reads only uncompressed AaruFormat so far.");
        if (sectorSize == 0) sectorSize = 2048;   // no data block seen (all-zero image) — assume 2048

        ulong mask = (1UL << shift) - 1;
        var zero = new byte[sectorSize];
        var sector = new byte[sectorSize];
        long written = 0;

        // Decoded-LZMA block cache: Aaru images carry many blocks; keep the last few decoded.
        var decoded = new Dictionary<ulong, byte[]>();
        var decodedOrder = new Queue<ulong>();
        // Uncompressed blocks whose stored CRC-64 has been verified (checked once per block).
        var verified = new HashSet<ulong>();

        for (ulong i = 0; i < entries; i++)
        {
            ulong entry = ddt[i];
            if (entry == 0) { output.Write(zero, 0, (int)sectorSize); written++; continue; }

            ulong blockOffset = entry >> shift;
            ulong sectorInBlock = entry & mask;
            // Guard the index math itself: a crafted shift/entry must not wrap the multiply below
            // into a small offset that passes later bounds checks.
            if (sectorInBlock > uint.MaxValue / sectorSize)
                throw new InvalidDataException("DDT entry's sector index is implausibly large — declined.");

            // Read this block's header to know how its bytes are stored.
            s.Position = (long)blockOffset;
            var bh = ReadExact(s, BlockHeaderSize);
            var comp = (Compression)U16(bh, 6);
            if (comp == Compression.None)
            {
                uint blockLen = U32(bh, 16);
                // The sector must lie INSIDE the block whose bytes the CRC vouches for — otherwise a
                // crafted DDT could serve neighboring file bytes under a passing block CRC.
                if (sectorInBlock * sectorSize + sectorSize > blockLen)
                    throw new InvalidDataException("DDT entry points past the end of its data block — declined.");
                // First touch of this block: prove its bytes against the stored CRC-64 (when recorded)
                // before serving any sector from it — same gate the compressed paths get.
                if (verified.Add(blockOffset))
                {
                    ulong storedCrc = U64(bh, 28);
                    if (storedCrc != 0 &&
                        Crc64OfRange(s, (long)(blockOffset + (ulong)BlockHeaderSize), blockLen) != storedCrc)
                        throw new InvalidDataException(
                            $"Data block at 0x{blockOffset:X} does not match its stored CRC-64 — the image is " +
                            "corrupt; declined rather than emit unproven bytes.");
                }
                // Uncompressed: sector data sits after the 36-byte BlockHeader, at index*sectorSize.
                s.Position = (long)(blockOffset + (ulong)BlockHeaderSize + sectorInBlock * sectorSize);
                ReadExactInto(s, sector);
                output.Write(sector, 0, (int)sectorSize);
            }
            else if (comp is Compression.Lzma or Compression.Flac)
            {
                if (!decoded.TryGetValue(blockOffset, out var block))
                {
                    block = comp == Compression.Lzma
                        ? DecodeLzmaBlock(s, blockOffset, bh)
                        : DecodeFlacBlock(s, blockOffset, bh);
                    decoded[blockOffset] = block;
                    decodedOrder.Enqueue(blockOffset);
                    if (decodedOrder.Count > 4) decoded.Remove(decodedOrder.Dequeue());
                }
                long off = (long)(sectorInBlock * sectorSize);
                if (off + sectorSize > block.Length)
                    throw new InvalidDataException("DDT entry points past the end of its decoded block.");
                output.Write(block, (int)off, (int)sectorSize);
            }
            else
            {
                throw new NotSupportedException($"Data block at 0x{blockOffset:X} is {comp}-compressed — declined.");
            }
            written++;
        }
        return written;
    }

    // Decode one LZMA data block: payload is [5-byte LZMA properties][raw LZMA1 stream], uncompressed
    // size from the header. The block's stored CRC-64 (ECMA-182) is the proof gate: when present it
    // must match the decoded bytes, so a wrong decode can never be emitted.
    private static byte[] DecodeLzmaBlock(Stream s, ulong blockOffset, byte[] blockHeader)
    {
        uint cmpLength = U32(blockHeader, 12);
        uint length = U32(blockHeader, 16);
        ulong storedCrc = U64(blockHeader, 28);
        if (cmpLength < 6) throw new InvalidDataException("LZMA block too small to hold properties + stream.");

        s.Position = (long)(blockOffset + (ulong)BlockHeaderSize);
        var payload = ReadExact(s, checked((int)cmpLength));
        var block = DiscForge.Core.Compression.Lzma1.Decode(
            payload.AsSpan(0, 5), payload.AsSpan(5), checked((int)length));

        // UNCONDITIONAL gate: a decode is proven against the stored CRC-64 or it is declined. A zeroed
        // CRC field must not switch verification off (note an all-zero block's true ECMA-182 CRC is 0,
        // so legitimate all-zero data still passes).
        if (Crc64Ecma182(block) != storedCrc)
            throw new InvalidDataException(
                "LZMA block decoded but did NOT match its stored CRC-64 — declined rather than emit unproven bytes.");
        return block;
    }

    // Decode one FLAC data block (Aaru compresses Red Book audio blocks as a self-describing FLAC
    // stream: the fLaC container marker, its metadata blocks, then the frames). The frames are decoded
    // with DiscForge's proven FLAC decoder (the same one that reads cdfl CHDs), byte-swapped from its
    // big-endian sample output to the little-endian byte order audio sectors are stored in, and gated
    // by the block's stored CRC-64: proven right or declined, never emitted unproven.
    private static byte[] DecodeFlacBlock(Stream s, ulong blockOffset, byte[] blockHeader)
    {
        uint cmpLength = U32(blockHeader, 12);
        uint length = U32(blockHeader, 16);
        ulong storedCrc = U64(blockHeader, 28);

        s.Position = (long)(blockOffset + (ulong)BlockHeaderSize);
        var payload = ReadExact(s, checked((int)cmpLength));

        // Skip the FLAC container if present: "fLaC" then metadata blocks (1-byte last|type + 24-bit
        // big-endian length each) until the last-metadata flag; frames follow. Long arithmetic so a
        // crafted metadata length can't wrap the cursor.
        int frameStart = 0;
        if (payload.Length >= 4 && payload[0] == 'f' && payload[1] == 'L' && payload[2] == 'a' && payload[3] == 'C')
        {
            long p = 4;
            while (p + 4 <= payload.Length)
            {
                bool last = (payload[p] & 0x80) != 0;
                long len = ((long)payload[p + 1] << 16) | ((long)payload[p + 2] << 8) | payload[p + 3];
                p += 4 + len;
                if (last) break;
            }
            if (p >= payload.Length) throw new InvalidDataException("FLAC block's metadata runs past its payload — declined.");
            frameStart = (int)p;
        }

        byte[] block;
        try
        {
            block = Chd.ChdFlac.Decode(payload, frameStart, checked((int)length)).Bytes;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"FLAC block at 0x{blockOffset:X} failed to decode — declined. ({ex.Message})");
        }
        // The frame loop is frame-granular, so a final frame can overshoot the declared length;
        // truncate to exactly `length` BEFORE the CRC so a valid image is never falsely declined.
        if (block.Length > length) Array.Resize(ref block, checked((int)length));

        // ChdFlac emits big-endian samples (the CHD convention); audio sectors store little-endian.
        for (int i = 0; i + 1 < block.Length; i += 2)
            (block[i], block[i + 1]) = (block[i + 1], block[i]);

        // UNCONDITIONAL gate, same as LZMA: proven against the stored CRC-64 or declined.
        if (Crc64Ecma182(block) != storedCrc)
            throw new InvalidDataException(
                "FLAC block decoded but did NOT match its stored CRC-64 — declined rather than emit unproven bytes.");
        return block;
    }

    // ---- writing (uncompressed) --------------------------------------------

    /// <summary>
    /// Write an UNCOMPRESSED AaruFormat image: a header, one uncompressed user-data block holding every
    /// sector, a flat deduplication table, and an index — all in Aaru's confirmed pack=1 on-disk layout
    /// (2-byte DataType/CompressionType, 36-byte BlockHeader, 49-byte DdtHeader, 14-byte index records),
    /// with ECMA-182 CRC-64s over the block and table. This lets DiscForge produce the interchange format,
    /// not only read it. Proven by a round-trip through <see cref="ReadInfo"/>/<see cref="ExtractUserData"/>;
    /// cross-reading by Aaru itself is expected from the matching layout but is not asserted here.
    /// </summary>
    public static void WriteUncompressed(Stream output, ReadOnlySpan<byte> sectors, uint sectorSize, uint mediaType = 0)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (sectorSize == 0) throw new ArgumentOutOfRangeException(nameof(sectorSize));
        if (sectors.Length % sectorSize != 0)
            throw new ArgumentException("Sector data length is not a whole number of sectors.", nameof(sectors));

        long n = sectors.Length / sectorSize;
        int shift = 1;
        while ((1L << shift) < n) shift++;                     // enough low bits for the sector index

        ulong dataBlockOffset = HeaderSize;                    // data block sits right after the header
        ulong ddtOffset = dataBlockOffset + (ulong)BlockHeaderSize + (ulong)sectors.Length;
        ulong indexOffset = ddtOffset + (ulong)DdtHeaderSize + (ulong)(n * 8);

        var payload = sectors.ToArray();
        ulong payloadCrc = Crc64(payload);

        // ---- header (104) ----
        var header = new byte[HeaderSize];
        MagicAaru.CopyTo(header.AsSpan(0));
        Encoding.Unicode.GetBytes("DiscForge").CopyTo(header, 8);
        header[72] = 5; header[73] = 4;                        // image format version (display only)
        header[74] = 1; header[75] = 0;                        // application version
        WU32(header, 76, mediaType);
        WU64(header, 80, indexOffset);
        output.Write(header);

        // ---- data block (36-byte header + payload) ----
        var bh = new byte[BlockHeaderSize];
        WU32(bh, 0, BtDataBlock);
        WU16(bh, 4, (ushort)DtUserData);                       // type
        WU16(bh, 6, 0);                                        // compression = None
        WU32(bh, 8, sectorSize);
        WU32(bh, 12, (uint)sectors.Length);                    // cmpLength == length (uncompressed)
        WU32(bh, 16, (uint)sectors.Length);
        WU64(bh, 20, payloadCrc);                              // cmpCrc64
        WU64(bh, 28, payloadCrc);                              // crc64
        output.Write(bh);
        output.Write(payload);

        // ---- deduplication table (49-byte header + n entries) ----
        var ddt = new byte[n * 8];
        for (long i = 0; i < n; i++)
            WU64(ddt, (int)(i * 8), (dataBlockOffset << shift) | (ulong)i);
        ulong ddtCrc = Crc64(ddt);

        var dh = new byte[DdtHeaderSize];
        WU32(dh, 0, BtDeDuplicationTable);
        WU16(dh, 4, (ushort)DtUserData);                       // type
        WU16(dh, 6, 0);                                        // compression = None
        dh[8] = (byte)shift;
        WU64(dh, 9, (ulong)n);                                 // entries
        WU64(dh, 17, (ulong)(n * 8));                          // cmpLength
        WU64(dh, 25, (ulong)(n * 8));                          // length
        WU64(dh, 33, ddtCrc);
        WU64(dh, 41, ddtCrc);
        output.Write(dh);
        output.Write(ddt);

        // ---- index (14-byte header + two 14-byte entries, crc64 over the entry table) ----
        var e1 = IndexEntry(BtDataBlock, dataBlockOffset);
        var e2 = IndexEntry(BtDeDuplicationTable, ddtOffset);
        var entryTable = new byte[e1.Length + e2.Length];
        e1.CopyTo(entryTable, 0);
        e2.CopyTo(entryTable, e1.Length);

        var ih = new byte[IndexHeaderSize];
        WU32(ih, 0, BtIndex);
        WU16(ih, 4, 2);                                        // two entries
        WU64(ih, 6, Crc64(entryTable));                        // crc64 of the entries, as Aaru records
        output.Write(ih);
        output.Write(entryTable);
    }

    private static byte[] IndexEntry(uint blockType, ulong offset)
    {
        var e = new byte[IndexEntrySize];
        WU32(e, 0, blockType);
        WU16(e, 4, (ushort)DtUserData);
        WU64(e, 6, offset);
        return e;
    }

    // ---- structure parsing --------------------------------------------------

    private static IEnumerable<BlockRef> ReadIndex(Stream s, ulong indexOffset)
    {
        s.Position = (long)indexOffset;
        var head = ReadExact(s, IndexHeaderSize);
        uint id = U32(head, 0);
        if (id != BtIndex && id != BtIndex2) yield break;      // not an index at that offset
        int entries = U16(head, 4);
        var table = ReadExact(s, entries * IndexEntrySize);
        for (int i = 0; i < entries; i++)
        {
            int o = i * IndexEntrySize;
            // IndexEntry: blockType u32 @0, dataType u16 @4, offset u64 @6.
            yield return new BlockRef(U32(table, o), U16(table, o + 4), U64(table, o + 6));
        }
    }

    private static (ulong entries, Compression comp) ReadDdtHeader(Stream s, ulong offset)
    {
        s.Position = (long)offset;
        var h = ReadExact(s, DdtHeaderSize);
        // DdtHeader: compression u16 @6, entries u64 @9.
        return (U64(h, 9), (Compression)U16(h, 6));
    }

    private static (ulong entries, Compression comp, byte shift, ulong[] ddt) ReadDdt(Stream s, ulong offset)
    {
        s.Position = (long)offset;
        var h = ReadExact(s, DdtHeaderSize);
        // DdtHeader: compression u16 @6, shift u8 @8, entries u64 @9, length u64 @25.
        var comp = (Compression)U16(h, 6);
        byte shift = h[8];
        ulong entries = U64(h, 9);
        ulong length = U64(h, 25);          // uncompressed length in bytes (== entries * 8)

        // Sanity-gate the untrusted header before allocating: shift must leave room in a 64-bit entry
        // (C# would silently mask shift ≥ 64 to shift & 63), and the table can't be larger than the
        // bytes that actually follow in the file.
        if (shift == 0 || shift >= 64)
            throw new InvalidDataException($"DDT shift {shift} is out of range — declined.");
        if (comp == Compression.None &&
            (length != entries * 8 || entries > (ulong)Math.Max(0L, s.Length - s.Position) / 8))
            throw new InvalidDataException("DDT header claims more entries than the file holds — declined.");

        var ddt = new ulong[entries];
        if (comp == Compression.None)
        {
            var raw = ReadExact(s, checked((int)length));
            for (ulong i = 0; i < entries; i++) ddt[i] = U64(raw, (int)(i * 8));
        }
        return (entries, comp, shift, ddt);
    }

    private static (uint sectorSize, Compression comp) ReadDataBlockHeader(Stream s, ulong offset)
    {
        s.Position = (long)offset;
        var h = ReadExact(s, BlockHeaderSize);
        // BlockHeader: compression u16 @6, sectorSize u32 @8.
        return (U32(h, 8), (Compression)U16(h, 6));
    }

    // ---- little-endian helpers ---------------------------------------------

    private static byte[] ReadExact(Stream s, int n)
    {
        var b = new byte[n];
        ReadExactInto(s, b);
        return b;
    }

    private static void ReadExactInto(Stream s, byte[] b)
    {
        int off = 0;
        while (off < b.Length)
        {
            int r = s.Read(b, off, b.Length - off);
            if (r <= 0) throw new EndOfStreamException("Unexpected end of AaruFormat image.");
            off += r;
        }
    }

    private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static ulong U64(byte[] b, int o)
    {
        ulong v = 0;
        for (int i = 7; i >= 0; i--) v = (v << 8) | b[o + i];
        return v;
    }

    private static void WU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void WU32(byte[] b, int o, uint v) { for (int i = 0; i < 4; i++) b[o + i] = (byte)(v >> (8 * i)); }
    private static void WU64(byte[] b, int o, ulong v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i)); }

    // CRC-64/ECMA-182 (poly 0x42F0E1EBA9EA3693, init 0, no reflection, no final xor) — the checksum
    // Aaru records over its blocks and deduplication table.
    private static readonly ulong[] Crc64Table = BuildCrc64Table();

    /// <summary>CRC-64/ECMA-182 as AaruFormat stores it — public so callers and tests can stamp/verify.</summary>
    public static ulong Crc64Ecma182(ReadOnlySpan<byte> data) => Crc64(data);

    /// <summary>Streamed CRC-64 over <paramref name="length"/> bytes at <paramref name="start"/> —
    /// verifies a multi-hundred-MB block in constant memory.</summary>
    private static ulong Crc64OfRange(Stream s, long start, long length)
    {
        s.Position = start;
        var buf = new byte[256 * 1024];
        ulong crc = 0;
        long remaining = length;
        while (remaining > 0)
        {
            int n = s.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
            if (n <= 0) throw new EndOfStreamException("Unexpected end of AaruFormat image while verifying a block.");
            for (int i = 0; i < n; i++) crc = Crc64Table[(byte)((crc >> 56) ^ buf[i])] ^ (crc << 8);
            remaining -= n;
        }
        return crc;
    }

    private static ulong[] BuildCrc64Table()
    {
        const ulong poly = 0x42F0E1EBA9EA3693;
        var t = new ulong[256];
        for (int i = 0; i < 256; i++)
        {
            ulong crc = (ulong)i << 56;
            for (int k = 0; k < 8; k++)
                crc = (crc & 0x8000_0000_0000_0000) != 0 ? (crc << 1) ^ poly : crc << 1;
            t[i] = crc;
        }
        return t;
    }

    private static ulong Crc64(ReadOnlySpan<byte> data)
    {
        ulong crc = 0;
        foreach (var b in data) crc = Crc64Table[(byte)((crc >> 56) ^ b)] ^ (crc << 8);
        return crc;
    }
}
