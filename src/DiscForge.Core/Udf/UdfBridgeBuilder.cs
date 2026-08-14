// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Iso;
using DiscForge.Core.Util;

namespace DiscForge.Core.Udf;

/// <summary>
/// Builds a "UDF bridge" image: ONE file readable BOTH as ISO 9660 (with Joliet)
/// AND as UDF 1.02, where every file's DATA is stored exactly ONCE and both
/// filesystems' directory entries point at the SAME absolute sectors. That
/// shared-data property is the defining feature of a bridge disc — the same shape
/// genisoimage/mkisofs/ImgBurn produce with <c>-udf -J -r</c>, and how DVD-Video
/// and hybrid data discs are authored so any reader (an ISO 9660 driver, a UDF
/// driver, an old CD-ROM stack) can mount the volume.
///
/// This is pure authoring: it assembles a filesystem from a folder of files. It
/// never decrypts, cracks or circumvents anything.
///
/// The sector map it writes (mirroring the genisoimage reference):
///
///   16      ISO 9660 Primary Volume Descriptor        (CD001, type 1)
///   17      ISO 9660 Joliet Supplementary VD          (CD001, type 2)
///   18      ISO 9660 Volume Descriptor Set Terminator (CD001, type 255)
///   19–21   UDF Volume Recognition Sequence           (BEA01, NSR02, TEA01)
///   32–47   UDF Main Volume Descriptor Sequence
///   48–63   UDF Reserve Volume Descriptor Sequence     (a mirror of the main)
///   64      UDF Logical Volume Integrity Descriptor
///   256     UDF Anchor Volume Descriptor Pointer
///   257…    the partition: File Set Descriptor (block 0), root directory File
///           Entry (block 1), the other File Entries and File Identifier
///           Descriptors, then the ISO 9660 path tables and directory records,
///           then the shared file DATA — one copy, addressed by both filesystems.
///   last    a backup UDF Anchor Volume Descriptor Pointer
///
/// The ISO 9660 half (its descriptors, path tables, directory records and — the
/// point — the file-data placement) is planned and written by the mature
/// <see cref="IsoBuilder"/>, told via its <c>firstDataSector</c> parameter to put
/// its content inside the partition, past the UDF metadata. Each UDF File Entry is
/// then given an allocation descriptor resolving to the exact sector IsoBuilder
/// assigned that file, so both filesystems reference identical data blocks.
///
/// The UDF descriptors are written here directly (rather than through
/// <see cref="UdfBuilder"/>, whose fixed 272-sector partition start and contiguous
/// file-data placement do not fit a co-planned bridge) but carry the same,
/// udfinfo-validated byte layouts, and the descriptor CRC/checksum come from the
/// shared <see cref="Crc16"/> helper. Builds are deterministic.
/// </summary>
public static class UdfBridgeBuilder
{
    public const int SectorSize = 2048;

    private const ushort FixedYear = 2026;

    // Physical layout constants (sectors).
    private const uint IsoPvdSector = 16;      // written by IsoBuilder
    private const uint VrsSector = 19;         // BEA01/NSR02/TEA01 follow the ISO descriptors
    private const uint MainVdsSector = 32;
    private const uint ReserveVdsSector = 48;
    private const uint VdsSectors = 16;        // length reserved for each VDS
    private const uint LvidSector = 64;
    private const uint AnchorSector = 256;
    private const uint PartitionStart = 257;

    // Descriptor tag identifiers (ECMA-167).
    private const ushort TagPrimaryVolume = 1;
    private const ushort TagAnchor = 2;
    private const ushort TagImplUseVolume = 4;
    private const ushort TagPartition = 5;
    private const ushort TagLogicalVolume = 6;
    private const ushort TagUnallocatedSpace = 7;
    private const ushort TagTerminating = 8;
    private const ushort TagLogicalVolumeIntegrity = 9;
    private const ushort TagFileSet = 256;
    private const ushort TagFileIdentifier = 257;
    private const ushort TagFileEntry = 261;

    private const byte FileTypeDirectory = 4;
    private const byte FileTypeRegular = 5;

    // Largest single allocation extent: block-aligned, under the 30-bit field.
    private const uint MaxExtentBytes = 0x3FFF_F800;

    public sealed record BuildResult(byte[] Image, IReadOnlyList<string> Warnings);

    // ---- public entry points ------------------------------------------------

    /// <summary>Build a bridge image from a tree of files and directories.</summary>
    public static byte[] Build(string volumeId, IReadOnlyList<IsoBuilder.Node> rootChildren)
        => BuildResultOf(volumeId, rootChildren).Image;

    /// <summary>Build, returning any warnings alongside the image.</summary>
    public static BuildResult BuildResultOf(string volumeId, IReadOnlyList<IsoBuilder.Node> rootChildren)
    {
        ArgumentNullException.ThrowIfNull(volumeId);
        ArgumentNullException.ThrowIfNull(rootChildren);

        using var ms = new MemoryStream();
        var warnings = WriteBridge(volumeId, ms, rootChildren, inMemoryGuard: true);
        return new BuildResult(ms.ToArray(), warnings);
    }

    /// <summary>
    /// Write a bridge image straight to a seekable stream. The ISO 9660 half — and
    /// with it the bulk file content — is streamed by <see cref="IsoBuilder"/> with
    /// constant memory; only the bounded UDF descriptors are then overlaid, so an
    /// image larger than RAM can be authored (files supplied via
    /// <see cref="IsoBuilder.Node.FromPath"/> stream from disk). Output must be
    /// seekable (a file).
    /// </summary>
    public static IReadOnlyList<string> BuildToStream(
        string volumeId, Stream output, IReadOnlyList<IsoBuilder.Node> rootChildren)
    {
        ArgumentNullException.ThrowIfNull(volumeId);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rootChildren);
        if (!output.CanSeek)
            throw new ArgumentException("Writing a bridge image needs a seekable stream (a file).", nameof(output));
        return WriteBridge(volumeId, output, rootChildren, inMemoryGuard: false);
    }

    // ---- the co-planned build ----------------------------------------------

    private static IReadOnlyList<string> WriteBridge(
        string volumeId, Stream output, IReadOnlyList<IsoBuilder.Node> rootChildren, bool inMemoryGuard)
    {
        var warnings = new List<string>();

        // 1. Plan the UDF metadata region: File Set Descriptor (partition block 0),
        //    root File Entry (block 1), every other File Entry, then the directory
        //    (File Identifier Descriptor) streams. This fixes udfMetaBlocks — the
        //    partition block where the ISO 9660 content and file data begin.
        var root = new UNode { Name = "", IsDir = true, Source = null, Parent = null };
        BuildTree(root, rootChildren, warnings);

        // Partition block 0 is the File Set Descriptor and block 1 its Terminating
        // Descriptor (the File Set sequence terminator), matching genisoimage; File
        // Entries therefore begin at block 2, the root directory's included.
        uint next = 2;
        AssignFeBlocks(root, ref next);        // one File Entry per node
        AssignDirData(root, ref next);         // directory FID streams
        uint udfMetaBlocks = next;

        // 2. Plan the ISO 9660 half with its content relocated into the partition,
        //    just past the UDF metadata. IsoBuilder assigns each file an absolute
        //    extent; those extents are the shared data sectors.
        int firstDataSector = (int)(PartitionStart + udfMetaBlocks);
        var iso = IsoBuilder.Plan(volumeId, rootChildren, joliet: true, boot: null,
                                  rockRidge: false, firstDataSector: firstDataSector);

        int isoEnd = iso.VolumeSectors;        // one past the last file's last sector
        uint imageSectors = (uint)isoEnd + 1;  // + the backup anchor
        uint partitionBlocks = imageSectors - 1 - PartitionStart;

        long imageBytes = (long)imageSectors * SectorSize;
        if (inMemoryGuard && imageBytes > int.MaxValue)
            throw new NotSupportedException(
                $"This tree needs a {imageBytes / (1024 * 1024):N0} MB image, past the in-memory " +
                "builder's ~2 GB ceiling. Use BuildToStream (dforge create-udf-bridge writes to disk).");

        // 3. Bind each UDF File Entry to the sector IsoBuilder placed the file at —
        //    the crux of the shared-data property. Directories keep their planned
        //    FID-stream extents; empty files carry no extent (block 0).
        var extents = new Dictionary<string, int>(StringComparer.Ordinal);
        MapExtents(iso.Root, "", extents);
        BindFileData(root, extents);

        int files = CountFiles(root);
        int dirs = CountDirs(root);

        // 4. Write the ISO 9660 image (descriptors at 16/17/18, path tables and
        //    directory records inside the partition, and the shared file data),
        //    streamed with constant memory. Its Primary VD reports the whole volume
        //    size including the UDF backup anchor.
        iso.VolumeSectors = (int)imageSectors;
        output.SetLength(imageBytes);
        output.Seek(0, SeekOrigin.Begin);
        iso.WriteTo(output);                   // fills 0..isoEnd; the final sector stays zero

        // 5. Overlay the UDF descriptors. Everything they occupy (19–21, 32–64, 256,
        //    the FSD/FE/FID blocks 257…) sits where IsoBuilder wrote zeros, so no ISO
        //    9660 structure is disturbed, and the file data is already in place.
        WriteVolumeRecognition(output);
        WriteMainVds(output, MainVdsSector, volumeId, partitionBlocks);
        WriteMainVds(output, ReserveVdsSector, volumeId, partitionBlocks);
        WriteIntegrity(output, partitionBlocks, files, dirs);
        WriteAnchor(output, AnchorSector);
        WriteAnchor(output, imageSectors - 1);
        WriteFileSetDescriptor(output, root.FeBlock, volumeId);
        WriteFileSetTerminator(output);
        WriteTree(output, root);

        output.Flush();
        warnings.AddRange(iso.Warnings);
        return warnings;
    }

    // ---- the planned tree ---------------------------------------------------

    private sealed class UNode
    {
        public required string Name { get; init; }
        public required bool IsDir { get; init; }
        public required IsoBuilder.Node? Source { get; init; }
        public required UNode? Parent { get; init; }
        public List<UNode> Children { get; } = new();

        public uint FeBlock;          // partition block of this node's File Entry
        public uint DataBlock;        // partition block of the first data block
        public uint DataBlocks;       // blocks the data occupies
        public long DataLength;       // bytes of data (file size, or FID-stream length)
        public byte[]? DirData;       // built FID stream, for directories
    }

    private static void BuildTree(UNode parent, IReadOnlyList<IsoBuilder.Node> children, List<string> warnings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
        {
            if (!seen.Add(child.Name))
                warnings.Add($"Duplicate name '{child.Name}' in '{Display(parent)}' — UDF requires " +
                             "unique names within a directory; the later entry may be unreachable.");

            var node = new UNode { Name = child.Name, IsDir = child.IsDir, Source = child, Parent = parent };
            parent.Children.Add(node);
            if (child.IsDir) BuildTree(node, child.Children, warnings);
        }
    }

    private static void AssignFeBlocks(UNode node, ref uint next)
    {
        node.FeBlock = next++;
        foreach (var child in node.Children) AssignFeBlocks(child, ref next);
    }

    private static void AssignDirData(UNode node, ref uint next)
    {
        if (node.IsDir)
        {
            node.DataLength = DirectoryDataLength(node);
            node.DataBlocks = Blocks(node.DataLength);
            node.DataBlock = next;
            next += node.DataBlocks;
            node.DirData = BuildDirectoryData(node);
        }
        foreach (var child in node.Children) AssignDirData(child, ref next);
    }

    private static void BindFileData(UNode node, IReadOnlyDictionary<string, int> extents)
    {
        if (!node.IsDir)
        {
            // The length comes from the source's FileSource (never reads the data),
            // exactly as IsoBuilder sized the extent it placed.
            long length = node.Source!.Source?.Length ?? 0;
            node.DataLength = length;
            node.DataBlocks = Blocks(length);
            if (length == 0)
            {
                node.DataBlock = 0;
            }
            else
            {
                string path = FullPath(node);
                int extent = extents.TryGetValue(path, out var e) ? e : 0;
                node.DataBlock = (uint)(extent - PartitionStart);
            }
        }
        foreach (var child in node.Children) BindFileData(child, extents);
    }

    private static void MapExtents(IsoBuilder.Dir dir, string prefix, Dictionary<string, int> map)
    {
        foreach (var c in dir.Children)
        {
            string path = prefix + "/" + c.Name;
            if (c is IsoBuilder.Dir sub) MapExtents(sub, path, map);
            else map[path] = c.Extent;
        }
    }

    private static string FullPath(UNode node)
    {
        var parts = new List<string>();
        for (var n = node; n?.Parent is not null; n = n.Parent) parts.Insert(0, n.Name);
        return "/" + string.Join('/', parts);
    }

    // ---- volume-recognition and volume descriptors --------------------------

    private static void WriteVolumeRecognition(Stream output)
    {
        WriteVsd(output, VrsSector + 0, "BEA01");
        WriteVsd(output, VrsSector + 1, "NSR02");
        WriteVsd(output, VrsSector + 2, "TEA01");
    }

    private static void WriteVsd(Stream output, uint sector, string id)
    {
        var s = new byte[SectorSize];
        s[0] = 0;                                          // structure type
        Encoding.ASCII.GetBytes(id).CopyTo(s, 1);          // 5-char standard identifier
        s[6] = 1;                                          // structure version
        PutSector(output, sector, s);
    }

    private static void WriteMainVds(Stream output, uint startSector, string volumeId, uint partitionBlocks)
    {
        WritePrimaryVolume(output, startSector + 0, volumeId);
        WriteImplUseVolume(output, startSector + 1, volumeId);
        WritePartition(output, startSector + 2, partitionBlocks);
        WriteLogicalVolume(output, startSector + 3, volumeId);
        WriteUnallocatedSpace(output, startSector + 4);
        WriteTerminating(output, startSector + 5);
    }

    private static void WritePrimaryVolume(Stream output, uint sector, string volumeId)
    {
        var buf = new byte[SectorSize];
        var s = buf.AsSpan(0, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0);          // VDS number
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], 0);          // PVD number
        WriteDString(s.Slice(24, 32), volumeId);                       // volume identifier
        BinaryPrimitives.WriteUInt16LittleEndian(s[56..], 1);          // volume seq number
        BinaryPrimitives.WriteUInt16LittleEndian(s[58..], 1);          // max volume seq
        BinaryPrimitives.WriteUInt16LittleEndian(s[60..], 2);          // interchange level
        BinaryPrimitives.WriteUInt16LittleEndian(s[62..], 2);          // max interchange level
        BinaryPrimitives.WriteUInt32LittleEndian(s[64..], 1);          // charset list
        BinaryPrimitives.WriteUInt32LittleEndian(s[68..], 1);          // max charset list
        WriteDString(s.Slice(72, 128), "DiscForge Volume Set");        // volume set identifier
        WriteCharspec(s.Slice(200, 64));                               // descriptor charset
        WriteCharspec(s.Slice(264, 64));                               // explanatory charset
        WriteTimestamp(s.Slice(376, 12));                              // recording time
        WriteRegid(s.Slice(388, 32), "*DiscForge");                    // implementation id
        FinishTag(buf, 0, 512, TagPrimaryVolume, sector);
        PutSector(output, sector, buf);
    }

    private static void WriteImplUseVolume(Stream output, uint sector, string volumeId)
    {
        var buf = new byte[SectorSize];
        var s = buf.AsSpan(0, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0);          // VDS number
        WriteRegid(s.Slice(20, 32), "*UDF LV Info", udfSuffix: true);  // implementation id
        var iu = s.Slice(52, 460);
        WriteCharspec(iu.Slice(0, 64));
        WriteDString(iu.Slice(64, 128), volumeId);
        WriteRegid(iu.Slice(64 + 128 + 36 * 3, 32), "*DiscForge");
        FinishTag(buf, 0, 512, TagImplUseVolume, sector);
        PutSector(output, sector, buf);
    }

    private static void WritePartition(Stream output, uint sector, uint partitionBlocks)
    {
        var buf = new byte[SectorSize];
        var s = buf.AsSpan(0, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0);          // VDS number
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], 1);          // partition flags: allocated
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], 0);          // partition number
        WriteRegid(s.Slice(24, 32), "+NSR02");                         // partition contents
        BinaryPrimitives.WriteUInt32LittleEndian(s[184..], 1);         // access type: read-only
        BinaryPrimitives.WriteUInt32LittleEndian(s[188..], PartitionStart);   // starting location
        BinaryPrimitives.WriteUInt32LittleEndian(s[192..], partitionBlocks);  // length in blocks
        WriteRegid(s.Slice(196, 32), "*DiscForge");                    // implementation id
        FinishTag(buf, 0, 512, TagPartition, sector);
        PutSector(output, sector, buf);
    }

    private static void WriteLogicalVolume(Stream output, uint sector, string volumeId)
    {
        var buf = new byte[SectorSize];
        var s = buf.AsSpan(0, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0);          // VDS number
        WriteCharspec(s.Slice(20, 64));                                // descriptor charset
        WriteDString(s.Slice(84, 128), volumeId);                      // logical volume id
        BinaryPrimitives.WriteUInt32LittleEndian(s[212..], SectorSize);// logical block size
        WriteRegid(s.Slice(216, 32), "*OSTA UDF Compliant", udfSuffix: true); // domain id
        // File Set at partition block 0; the extent spans two blocks so it covers
        // the File Set Descriptor and its Terminating Descriptor at block 1.
        WriteLongAd(s.Slice(248, 16), 2 * SectorSize, logicalBlock: 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[264..], 6);         // map table length
        BinaryPrimitives.WriteUInt32LittleEndian(s[268..], 1);         // number of partition maps
        WriteRegid(s.Slice(272, 32), "*DiscForge");                    // implementation id
        WriteExtentAd(s.Slice(432, 8), SectorSize, LvidSector);        // integrity sequence extent
        var map = s.Slice(440, 6);                                     // partition map 1 (Type 1)
        map[0] = 1;
        map[1] = 6;
        BinaryPrimitives.WriteUInt16LittleEndian(map[2..], 1);         // volume sequence number
        BinaryPrimitives.WriteUInt16LittleEndian(map[4..], 0);         // partition number
        FinishTag(buf, 0, 446, TagLogicalVolume, sector);
        PutSector(output, sector, buf);
    }

    private static void WriteUnallocatedSpace(Stream output, uint sector)
    {
        var buf = new byte[SectorSize];
        var s = buf.AsSpan(0, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0);          // VDS number
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], 0);          // number of allocation descriptors
        FinishTag(buf, 0, 24, TagUnallocatedSpace, sector);
        PutSector(output, sector, buf);
    }

    private static void WriteTerminating(Stream output, uint sector)
    {
        var buf = new byte[SectorSize];
        FinishTag(buf, 0, 512, TagTerminating, sector);
        PutSector(output, sector, buf);
    }

    private static void WriteIntegrity(Stream output, uint partitionBlocks, int files, int dirs)
    {
        var buf = new byte[SectorSize];
        var s = buf.AsSpan(0, 512);
        WriteTimestamp(s.Slice(16, 12));                               // recording time
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], 1);          // integrity type: close
        BinaryPrimitives.WriteUInt64LittleEndian(s[40..], (ulong)(files + dirs + 16)); // next uniqueId
        BinaryPrimitives.WriteUInt32LittleEndian(s[72..], 1);          // number of partitions
        BinaryPrimitives.WriteUInt32LittleEndian(s[76..], 46);         // length of implementation use
        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], 0);          // free space (partition 0)
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], partitionBlocks); // size (partition 0)
        var iu = s.Slice(88, 46);
        WriteRegid(iu.Slice(0, 32), "*DiscForge");
        BinaryPrimitives.WriteUInt32LittleEndian(iu[32..], (uint)files);
        BinaryPrimitives.WriteUInt32LittleEndian(iu[36..], (uint)dirs);
        BinaryPrimitives.WriteUInt16LittleEndian(iu[40..], 0x0102);    // min UDF read
        BinaryPrimitives.WriteUInt16LittleEndian(iu[42..], 0x0102);    // min UDF write
        BinaryPrimitives.WriteUInt16LittleEndian(iu[44..], 0x0102);    // max UDF write
        FinishTag(buf, 0, 88 + 46, TagLogicalVolumeIntegrity, LvidSector);
        PutSector(output, LvidSector, buf);
    }

    private static void WriteAnchor(Stream output, uint sector)
    {
        var buf = new byte[SectorSize];
        var s = buf.AsSpan(0, 512);
        WriteExtentAd(s.Slice(16, 8), VdsSectors * SectorSize, MainVdsSector);
        WriteExtentAd(s.Slice(24, 8), VdsSectors * SectorSize, ReserveVdsSector);
        FinishTag(buf, 0, 512, TagAnchor, sector);
        PutSector(output, sector, buf);
    }

    private static void WriteFileSetDescriptor(Stream output, uint rootFeBlock, string volumeId)
    {
        var buf = new byte[SectorSize];
        var s = buf.AsSpan(0, 512);
        WriteTimestamp(s.Slice(16, 12));                               // recording time
        BinaryPrimitives.WriteUInt16LittleEndian(s[28..], 3);          // interchange level
        BinaryPrimitives.WriteUInt16LittleEndian(s[30..], 3);          // max interchange level
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], 1);          // charset list
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], 1);          // max charset list
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 0);          // file set number
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 0);          // file set descriptor number
        WriteCharspec(s.Slice(48, 64));                                // LV id charset
        WriteDString(s.Slice(112, 128), volumeId);                     // logical volume id
        WriteCharspec(s.Slice(240, 64));                               // file set charset
        WriteDString(s.Slice(304, 32), volumeId);                      // file set id
        WriteLongAd(s.Slice(400, 16), SectorSize, rootFeBlock);        // root directory ICB
        WriteRegid(s.Slice(416, 32), "*OSTA UDF Compliant", udfSuffix: true); // domain id
        // A descriptor recorded inside a partition carries a PARTITION-RELATIVE tag
        // location (ECMA-167 4/7.2.1); the FSD is at partition block 0.
        FinishTag(buf, 0, 512, TagFileSet, 0);
        PutSector(output, PartitionStart, buf);
    }

    /// <summary>A Terminating Descriptor (tag 8) at partition block 1, closing the
    /// File Set descriptor sequence — the "File Set Terminator" strict UDF readers
    /// (and genisoimage's own images) expect after the File Set Descriptor.</summary>
    private static void WriteFileSetTerminator(Stream output)
    {
        var buf = new byte[SectorSize];
        FinishTag(buf, 0, 512, TagTerminating, 1);     // partition-relative block 1
        PutSector(output, PartitionStart + 1, buf);
    }

    // ---- the tree -----------------------------------------------------------

    private static void WriteTree(Stream output, UNode node)
    {
        WriteFileEntry(output, node);
        if (node.IsDir)
            PutBytes(output, PartitionStart + node.DataBlock, node.DirData!);
        // File data is already on disk (IsoBuilder wrote it); the File Entry above
        // simply points at it — the shared-data property.
        foreach (var child in node.Children) WriteTree(output, child);
    }

    private static void WriteFileEntry(Stream output, UNode node)
    {
        var buf = new byte[SectorSize];
        var s = buf.AsSpan(0, SectorSize);
        byte fileType = node.IsDir ? FileTypeDirectory : FileTypeRegular;
        ushort linkCount = (ushort)(node.IsDir ? 1 + node.Children.Count(c => c.IsDir) : 1);

        var icb = s.Slice(16, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(icb[4..], 4);         // strategy type 4
        BinaryPrimitives.WriteUInt16LittleEndian(icb[8..], 1);         // number of entries
        icb[11] = fileType;                                            // file type
        BinaryPrimitives.WriteUInt16LittleEndian(icb[18..], 0);        // flags: short_ad (type 0)

        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], 0xFFFF_FFFF);// uid: none
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 0xFFFF_FFFF);// gid: none
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], node.IsDir ? 0x14A5u : 0x10A5u); // permissions
        BinaryPrimitives.WriteUInt16LittleEndian(s[48..], linkCount);  // link count
        BinaryPrimitives.WriteUInt64LittleEndian(s[56..], (ulong)node.DataLength);   // information length
        BinaryPrimitives.WriteUInt64LittleEndian(s[64..], node.DataBlocks);          // logical blocks recorded
        WriteTimestamp(s.Slice(72, 12));                               // access time
        WriteTimestamp(s.Slice(84, 12));                               // modification time
        WriteTimestamp(s.Slice(96, 12));                               // attribute time
        BinaryPrimitives.WriteUInt32LittleEndian(s[108..], 1);         // checkpoint
        WriteRegid(s.Slice(128, 32), "*DiscForge");                    // implementation id
        BinaryPrimitives.WriteUInt64LittleEndian(s[160..], (ulong)node.FeBlock + 16); // unique id

        int adLen = WriteShortAds(s, 176, node.DataLength, node.DataBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(s[168..], 0);         // length of EA
        BinaryPrimitives.WriteUInt32LittleEndian(s[172..], (uint)adLen); // length of ADs

        FinishTag(buf, 0, 176 + adLen, TagFileEntry, node.FeBlock);
        PutSector(output, PartitionStart + node.FeBlock, buf);
    }

    private static int WriteShortAds(Span<byte> s, int adOffset, long dataLength, uint dataBlock)
    {
        if (dataLength == 0) return 0;
        long remaining = dataLength;
        uint pos = dataBlock;
        int written = 0;
        int o = adOffset;
        while (remaining > 0)
        {
            uint chunk = (uint)Math.Min(remaining, MaxExtentBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), chunk);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4, 4), pos);
            o += 8;
            written += 8;
            pos += Blocks(chunk);
            remaining -= chunk;
        }
        return written;
    }

    // ---- File Identifier Descriptors ---------------------------------------

    private static long DirectoryDataLength(UNode dir)
    {
        long total = FidLength(null);
        foreach (var child in dir.Children) total += FidLength(child.Name);
        return total;
    }

    private static int FidLength(string? name)
    {
        int lFi = string.IsNullOrEmpty(name) ? 0 : 1 + Encoding.Latin1.GetByteCount(name);
        int baseLen = 38 + lFi;
        return baseLen + Pad4(baseLen);
    }

    private static byte[] BuildDirectoryData(UNode dir)
    {
        using var ms = new MemoryStream();
        uint parentFe = (dir.Parent ?? dir).FeBlock;
        uint tagLocation = dir.DataBlock;
        ms.Write(BuildFid(name: null, isDirectory: true, isParent: true, childFe: parentFe, tagLocation));
        foreach (var child in dir.Children)
            ms.Write(BuildFid(child.Name, child.IsDir, isParent: false, child.FeBlock, tagLocation));
        return ms.ToArray();
    }

    private static byte[] BuildFid(string? name, bool isDirectory, bool isParent, uint childFe, uint tagLocation)
    {
        byte[] encoded = string.IsNullOrEmpty(name) ? Array.Empty<byte>() : EncodeOstaName(name);
        int lFi = encoded.Length;
        int baseLen = 38 + lFi;
        int total = baseLen + Pad4(baseLen);
        var fid = new byte[total];
        var s = fid.AsSpan();

        BinaryPrimitives.WriteUInt16LittleEndian(s[16..], 1);          // file version number
        byte characteristics = 0;
        if (isParent) characteristics |= 0x08;
        if (isDirectory) characteristics |= 0x02;
        s[18] = characteristics;
        s[19] = (byte)lFi;                                             // length of file identifier
        WriteLongAd(s.Slice(20, 16), SectorSize, childFe);            // ICB of the child / parent
        BinaryPrimitives.WriteUInt16LittleEndian(s[36..], 0);          // length of implementation use
        if (lFi > 0) encoded.CopyTo(s.Slice(38, lFi));

        FinishTag(fid, 0, total, TagFileIdentifier, tagLocation);
        return fid;
    }

    // ---- tag, CRC and checksum ---------------------------------------------

    private static void FinishTag(byte[] buffer, int at, int descriptorLength, ushort tagId, uint tagLocation)
    {
        var tag = buffer.AsSpan(at, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(tag, tagId);          // tag identifier
        BinaryPrimitives.WriteUInt16LittleEndian(tag[2..], 2);         // descriptor version (UDF 1.02)
        tag[4] = 0;                                                    // checksum, set last
        tag[5] = 0;                                                    // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(tag[6..], 1);         // tag serial number

        int crcLength = descriptorLength - 16;
        ushort crc = Crc16.Compute(buffer.AsSpan(at + 16, crcLength));
        BinaryPrimitives.WriteUInt16LittleEndian(tag[8..], crc);       // descriptor CRC
        BinaryPrimitives.WriteUInt16LittleEndian(tag[10..], (ushort)crcLength); // CRC length
        BinaryPrimitives.WriteUInt32LittleEndian(tag[12..], tagLocation);

        int sum = 0;
        for (int i = 0; i < 4; i++) sum += tag[i];
        for (int i = 5; i < 16; i++) sum += tag[i];
        tag[4] = (byte)(sum & 0xFF);                                   // tag checksum
    }

    // ---- small field writers -----------------------------------------------

    private static void WriteCharspec(Span<byte> s)
    {
        s.Clear();
        s[0] = 0;                                                      // CS0
        Encoding.ASCII.GetBytes("OSTA Compressed Unicode").CopyTo(s[1..]);
    }

    private static void WriteRegid(Span<byte> s, string id, bool udfSuffix = false)
    {
        s.Clear();
        s[0] = 0;                                                      // flags
        var idBytes = Encoding.ASCII.GetBytes(id);
        idBytes.AsSpan(0, Math.Min(idBytes.Length, 23)).CopyTo(s[1..]);
        if (udfSuffix)
        {
            s[24] = 0x02;                                              // UDF revision 1.02
            s[25] = 0x01;
        }
    }

    private static void WriteDString(Span<byte> field, string value)
    {
        field.Clear();
        if (string.IsNullOrEmpty(value)) return;
        var bytes = Encoding.Latin1.GetBytes(value);
        int maxChars = field.Length - 2;
        int take = Math.Min(bytes.Length, maxChars);
        field[0] = 8;                                                 // 8-bit compression
        bytes.AsSpan(0, take).CopyTo(field[1..]);
        field[^1] = (byte)(take + 1);
    }

    private static byte[] EncodeOstaName(string name)
    {
        var bytes = Encoding.Latin1.GetBytes(name);
        var result = new byte[bytes.Length + 1];
        result[0] = 8;                                                // 8-bit compression id
        bytes.CopyTo(result, 1);
        return result;
    }

    private static void WriteTimestamp(Span<byte> s)
    {
        s.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(s, 0x1000);          // type 1 (local), timezone 0
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], FixedYear);
        s[4] = 1;                                                     // month
        s[5] = 1;                                                     // day
    }

    private static void WriteLongAd(Span<byte> s, uint lengthBytes, uint logicalBlock, ushort partRef = 0)
    {
        s.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(s, lengthBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], logicalBlock);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], partRef);
    }

    private static void WriteExtentAd(Span<byte> s, uint lengthBytes, uint location)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(s, lengthBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], location);
    }

    // ---- stream placement ---------------------------------------------------

    private static void PutSector(Stream output, uint sector, byte[] sectorBytes)
        => PutBytes(output, sector, sectorBytes);

    private static void PutBytes(Stream output, uint sector, byte[] data)
    {
        output.Seek((long)sector * SectorSize, SeekOrigin.Begin);
        output.Write(data, 0, data.Length);
    }

    // ---- helpers ------------------------------------------------------------

    private static uint Blocks(long bytes) => (uint)((bytes + SectorSize - 1) / SectorSize);
    private static int Pad4(int n) => (4 - (n % 4)) % 4;

    private static int CountFiles(UNode node) =>
        (node.IsDir ? 0 : 1) + node.Children.Sum(CountFiles);
    private static int CountDirs(UNode node) =>
        (node.IsDir ? 1 : 0) + node.Children.Sum(CountDirs);

    private static string Display(UNode node)
    {
        var parts = new List<string>();
        for (var n = node; n?.Parent is not null; n = n.Parent) parts.Insert(0, n.Name);
        return "/" + string.Join('/', parts);
    }
}
