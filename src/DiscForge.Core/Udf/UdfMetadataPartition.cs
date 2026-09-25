// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.Udf;

/// <summary>
/// Resolves the UDF 2.50 <b>metadata partition</b> that Blu-ray uses — the
/// piece that made BD images un-browsable. In a plain (Type 1) UDF volume a
/// logical block maps straight to a partition-relative sector, and the existing
/// <see cref="UdfReader"/> handles that. Blu-ray instead wraps the real File Set
/// and directory tree inside a <i>Metadata File</i>, addressed through a Type 2
/// "metadata partition map" in the Logical Volume Descriptor. To browse a BD you
/// must first read that Metadata File's extents, then translate every logical
/// block through them.
///
/// This class parses the LVD's partition maps, finds the metadata partition map
/// and its Metadata File, reads that file's allocation extents, and exposes a
/// <see cref="Translate"/> that turns a metadata-partition logical block into a
/// physical sector. With that, the ordinary File Entry / File Identifier walk
/// works unchanged — it just resolves blocks through here.
///
/// Structure only: this locates and maps files, it never decrypts anything. A
/// BD's AACS-encrypted payload is not touched; DiscForge browses the structure
/// of unprotected or personally-authored discs.
/// </summary>
public static class UdfMetadataPartition
{
    public const int SectorSize = 2048;

    /// <summary>A parsed metadata partition: the physical map from logical blocks
    /// (within the metadata partition) to absolute image sectors.</summary>
    public sealed record Map
    {
        /// <summary>Extents of the Metadata File, as (physicalStartSector, blockCount).
        /// Logical blocks are laid out consecutively across these extents.</summary>
        public required IReadOnlyList<(uint PhysicalStart, uint Blocks)> Extents { get; init; }
        /// <summary>Start sector of the physical partition the metadata file lives in.</summary>
        public required uint PhysicalPartitionStart { get; init; }
        public required uint TotalBlocks { get; init; }

        /// <summary>Translate a metadata-partition logical block to an absolute
        /// image sector, or null if it falls outside the mapped extents.</summary>
        public uint? Translate(uint logicalBlock)
        {
            uint seen = 0;
            foreach (var (start, blocks) in Extents)
            {
                if (logicalBlock < seen + blocks)
                    return PhysicalPartitionStart + start + (logicalBlock - seen);
                seen += blocks;
            }
            return null;
        }
    }

    /// <summary>A partition map entry parsed from the LVD.</summary>
    public readonly record struct PartitionMap(
        int Type, ushort PartitionNumber, uint MetadataFileLocation, bool IsMetadata);

    /// <summary>
    /// Parse the partition maps from a Logical Volume Descriptor sector. The map
    /// table starts at offset 440; each map is self-sizing (byte 1 = length).
    /// Type 1 maps are 6 bytes; Type 2 maps are 64 bytes and carry a UDF
    /// identifier that distinguishes metadata / virtual / sparable partitions.
    /// </summary>
    public static IReadOnlyList<PartitionMap> ParsePartitionMaps(ReadOnlySpan<byte> lvd)
    {
        uint mapCount = BinaryPrimitives.ReadUInt32LittleEndian(lvd.Slice(432, 4));
        int offset = 440;
        var maps = new List<PartitionMap>();

        for (uint i = 0; i < mapCount && offset + 2 <= lvd.Length; i++)
        {
            byte type = lvd[offset];
            byte length = lvd[offset + 1];
            if (length == 0 || offset + length > lvd.Length) break;

            if (type == 1)
            {
                // Type 1: [type][len=6][volSeqNum(2)][partitionNumber(2)]
                ushort partNum = BinaryPrimitives.ReadUInt16LittleEndian(lvd.Slice(offset + 4, 2));
                maps.Add(new PartitionMap(1, partNum, 0, false));
            }
            else if (type == 2)
            {
                // Type 2: [type][len=64][reserved(2)][ident(32)]…[partitionNumber]…
                // The UDF entity identifier at offset+4 names the sub-type.
                string ident = ReadIdentifier(lvd.Slice(offset + 4, 32));
                bool isMeta = ident.Contains("*UDF Metadata Partition", StringComparison.Ordinal);

                // For a metadata partition map the layout after the identifier is:
                //   volSeqNum(2) @ 36, partitionNumber(2) @ 38,
                //   metadataFileLocation(4) @ 40, metadataMirrorFileLocation(4) @ 44, …
                ushort partNum = BinaryPrimitives.ReadUInt16LittleEndian(lvd.Slice(offset + 38, 2));
                uint metaFileLoc = isMeta
                    ? BinaryPrimitives.ReadUInt32LittleEndian(lvd.Slice(offset + 40, 4))
                    : 0;
                maps.Add(new PartitionMap(2, partNum, metaFileLoc, isMeta));
            }
            // else: unknown map type; skip by its length.

            offset += length;
        }

        return maps;
    }

    /// <summary>
    /// Given a detected metadata partition map, read its Metadata File (at
    /// <paramref name="metadataFileBlock"/> within the physical partition) and
    /// build the logical→physical <see cref="Map"/> from the file's extents.
    /// </summary>
    public static Map? BuildMap(Stream image, uint physicalPartitionStart, uint metadataFileBlock)
    {
        uint feSector = physicalPartitionStart + metadataFileBlock;
        if ((long)feSector * SectorSize >= image.Length) return null;

        var s = ReadSector(image, feSector);
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(s);
        if (tag is not (261 or 266)) return null;   // File Entry / Extended File Entry

        // Locate the allocation descriptors, same header math as UdfReader.
        int lengthOfEa, lengthOfAd, adOffset;
        if (tag == 261)
        {
            lengthOfEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0xA8, 4));
            lengthOfAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0xAC, 4));
            adOffset = 0xB0 + lengthOfEa;
        }
        else
        {
            lengthOfEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0xD0, 4));
            lengthOfAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0xD4, 4));
            adOffset = 0xD8 + lengthOfEa;
        }
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(16 + 18, 2));
        int adType = flags & 0x07;

        var extents = new List<(uint, uint)>();
        uint totalBlocks = 0;

        // short_ad (type 0): 8 bytes each — length(4), position(4).
        // long_ad  (type 1): 16 bytes each — length(4), position(4)+partRef(2)+impl(6).
        int adSize = adType == 1 ? 16 : 8;
        for (int p = adOffset; p + adSize <= adOffset + lengthOfAd && p + adSize <= s.Length; p += adSize)
        {
            uint lenField = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(p, 4));
            uint extentLen = lenField & 0x3FFF_FFFF;      // low 30 bits = bytes
            uint pos = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(p + 4, 4));
            if (extentLen == 0) continue;

            uint blocks = (extentLen + SectorSize - 1) / SectorSize;
            extents.Add((pos, blocks));
            totalBlocks += blocks;
        }

        if (extents.Count == 0) return null;

        return new Map
        {
            Extents = extents,
            PhysicalPartitionStart = physicalPartitionStart,
            TotalBlocks = totalBlocks,
        };
    }

    private static string ReadIdentifier(ReadOnlySpan<byte> id)
    {
        // A UDF entity identifier is 32 bytes: byte 0 is flags, bytes 1..22 are
        // the id text (the rest is suffix). Skip the flags byte, read the text.
        if (id.Length < 2) return "";
        var text = id[1..];
        int end = 0;
        while (end < text.Length && text[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(text[..end]).TrimEnd();
    }

    private static byte[] ReadSector(Stream image, uint sector)
    {
        var buf = new byte[SectorSize];
        image.Position = (long)sector * SectorSize;
        int read = 0;
        while (read < buf.Length)
        {
            int n = image.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return buf;
    }
}
