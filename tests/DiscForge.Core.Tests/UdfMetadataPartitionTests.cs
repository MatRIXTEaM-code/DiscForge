// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Udf;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Unit tests for UDF 2.50 metadata-partition resolution — the Blu-ray indirection
/// where the real File Set and directory tree live inside a Metadata File addressed
/// through a Type 2 partition map. The structures here are hand-built to the UDF
/// 2.50 field offsets (independent of the reader), so a wrong offset in the reader
/// would fail these. This exercises the exact logic; browsing a pressed Blu-ray end
/// to end still wants a real disc, which can't be synthesised here.
/// </summary>
public class UdfMetadataPartitionTests
{
    private static void U16(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
    private static void U32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);

    // A Logical Volume Descriptor sector carrying a Type 1 map then a Type 2
    // metadata partition map (per UDF 2.50 2.2.10).
    private static byte[] BuildLvd(uint metadataFileBlock)
    {
        var lvd = new byte[2048];
        U32(lvd, 432, 2);              // number of partition maps
        int o = 440;

        // Type 1 map: [1][6][volSeq(2)][partNum(2)]
        lvd[o] = 1; lvd[o + 1] = 6; U16(lvd, o + 4, 0);
        o += 6;

        // Type 2 metadata partition map: [2][64][reserved(2)][ident(32)] ...
        lvd[o] = 2; lvd[o + 1] = 64;
        lvd[o + 4] = 0;               // identifier flags byte
        Encoding.ASCII.GetBytes("*UDF Metadata Partition").CopyTo(lvd, o + 5);
        U16(lvd, o + 36, 1);          // volume sequence number
        U16(lvd, o + 38, 7);          // partition number
        U32(lvd, o + 40, metadataFileBlock);        // metadata file location
        U32(lvd, o + 44, metadataFileBlock + 1);    // mirror file location
        return lvd;
    }

    // A File Entry (tag 261) for the Metadata File, with short_ad extents.
    private static void WriteMetadataFileEntry(byte[] image, uint sector, (uint pos, uint bytes)[] extents)
    {
        int b = (int)sector * 2048;
        U16(image, b + 0, 261);        // descriptor tag id = File Entry
        U16(image, b + 34, 0);         // ICB tag flags: adType 0 (short_ad)
        U32(image, b + 0xA8, 0);       // length of extended attributes
        U32(image, b + 0xAC, (uint)(extents.Length * 8));   // length of allocation descriptors
        int ad = b + 0xB0;
        foreach (var (pos, bytes) in extents)
        {
            U32(image, ad, bytes);     // extent length (bytes, low 30 bits)
            U32(image, ad + 4, pos);   // extent position (block in the physical partition)
            ad += 8;
        }
    }

    [Fact]
    public void ParsePartitionMaps_FindsTheMetadataMap()
    {
        var maps = UdfMetadataPartition.ParsePartitionMaps(BuildLvd(metadataFileBlock: 5));
        Assert.Equal(2, maps.Count);
        Assert.False(maps[0].IsMetadata);
        Assert.True(maps[1].IsMetadata);
        Assert.Equal(5u, maps[1].MetadataFileLocation);
    }

    [Fact]
    public void BuildMap_ReadsExtents_AndTranslatesBlocks()
    {
        const uint partStart = 300, metaFileBlock = 5;
        var image = new byte[400 * 2048];
        // Metadata File Entry at partStart + metaFileBlock, two extents:
        // 3 blocks at physical 100, then 2 blocks at physical 200.
        WriteMetadataFileEntry(image, partStart + metaFileBlock,
            new (uint, uint)[] { (100, 3 * 2048), (200, 2 * 2048) });

        using var ms = new MemoryStream(image);
        var map = UdfMetadataPartition.BuildMap(ms, partStart, metaFileBlock);
        Assert.NotNull(map);
        Assert.Equal(5u, map!.TotalBlocks);

        // First extent: logical 0..2 -> physical partStart+100..102.
        Assert.Equal(partStart + 100, map.Translate(0));
        Assert.Equal(partStart + 102, map.Translate(2));
        // Second extent: logical 3..4 -> physical partStart+200..201.
        Assert.Equal(partStart + 200, map.Translate(3));
        Assert.Equal(partStart + 201, map.Translate(4));
        // Past the end -> unmapped.
        Assert.Null(map.Translate(5));
    }

    [Fact]
    public void NonMetadataLvd_HasNoMetadataMap()
    {
        var lvd = new byte[2048];
        U32(lvd, 432, 1);
        lvd[440] = 1; lvd[441] = 6;    // a lone Type 1 map
        var maps = UdfMetadataPartition.ParsePartitionMaps(lvd);
        Assert.Single(maps);
        Assert.DoesNotContain(maps, m => m.IsMetadata);
    }
}
