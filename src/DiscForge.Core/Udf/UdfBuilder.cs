// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Util;

namespace DiscForge.Core.Udf;

/// <summary>
/// Writes a UDF 1.02 filesystem — the other half of <see cref="UdfReader"/>, and
/// the filesystem DVD-Video needs. ISO 9660 cannot describe a DVD-Video volume
/// the way players expect; UDF can, so authoring a shrunk or rebuilt DVD back to
/// a burnable image needs a UDF writer, not just a reader.
///
/// This produces the plain, universally-mounted shape: a single Type 1
/// (physical) partition, 2048-byte logical blocks, a File Set with a directory
/// tree of File Entries and File Identifier Descriptors. It is deliberately the
/// UDF 1.02 baseline — no metadata partition (that is Blu-ray's UDF 2.50, read
/// but not yet written). Extended attributes (ECMA-167 4/14.10) and named
/// streams (an Extended File Entry's Stream Directory ICB, 4/14.17) are supported
/// as opt-in per-node additions via <see cref="Node.WithAttribute"/> and
/// <see cref="Node.WithStream"/>; a node that carries neither is written exactly
/// as the plain baseline, so the DVD-Video shape stays byte-for-byte unchanged.
///
/// The layout, in physical sectors:
///
///   0–15    reserved system area (zero)
///   16–18   Volume Recognition Sequence: BEA01, NSR02, TEA01
///   32–47   Main Volume Descriptor Sequence (PVD, IUVD, PD, LVD, USD, TD)
///   48–63   Reserve Volume Descriptor Sequence (a mirror of the main)
///   64      Logical Volume Integrity Descriptor
///   256     Anchor Volume Descriptor Pointer (mirrored at the last sector)
///   272…    the partition: File Set Descriptor, then the File Entries,
///           directory data and file data of the tree
///
/// Every descriptor carries the ECMA-167 tag: a CRC-16/CCITT over its body and a
/// one-byte checksum over the tag itself, both of which <see cref="UdfReader"/>
/// and real mounts validate. Builds are deterministic — the timestamp is fixed —
/// so the same tree always yields byte-identical output, which is what makes the
/// round-trip test meaningful.
///
/// Two write paths share all the descriptor work: <see cref="Build"/> assembles
/// the whole image in memory (guarded at the 2 GB byte[] limit, kept for the
/// round-trip tests and small images), while <see cref="BuildToStream"/> writes to
/// a seekable stream — building the bounded metadata region in memory and
/// streaming only the bulk file content — so a full DVD-9 rebuild past 2 GB is
/// authored without holding it all in memory. Both produce byte-identical output
/// for the same in-memory tree.
/// </summary>
public static class UdfBuilder
{
    public const int SectorSize = 2048;

    // A fixed build timestamp keeps output deterministic (2026-01-01T00:00:00Z).
    private const ushort FixedYear = 2026;

    // Physical layout constants (sectors).
    private const uint VrsSector = 16;
    private const uint MainVdsSector = 32;
    private const uint ReserveVdsSector = 48;
    private const uint VdsSectors = 16;      // length reserved for each VDS
    private const uint LvidSector = 64;
    private const uint AnchorSector = 256;
    private const uint PartitionStart = 272;

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
    private const ushort TagExtendedAttrHeader = 262;      // ECMA-167 4/14.10.1
    private const ushort TagExtendedFileEntry = 266;       // ECMA-167 4/14.17

    private const byte FileTypeDirectory = 4;
    private const byte FileTypeRegular = 5;
    private const byte FileTypeStreamDirectory = 13;       // ECMA-167: stream directory

    // Extended-attribute types (ECMA-167 4/14.10). We author implementation-use
    // EAs: the attribute is keyed by a registered identifier (its regid) and
    // carries an arbitrary implementation-use payload.
    private const uint EaTypeImplementationUse = 2048;     // ECMA-167 4/14.10.8
    private const uint EaTypeApplicationUse = 65536;       // ECMA-167 4/14.10.9

    // Largest single allocation extent: block-aligned, under the 30-bit field.
    private const uint MaxExtentBytes = 0x3FFF_F800;

    // ---- the tree the caller supplies --------------------------------------

    /// <summary>A node in the tree to author: a file with bytes, or a directory
    /// with children.</summary>
    public abstract class Node
    {
        public string Name { get; }
        private protected Node(string name) => Name = name;

        private readonly List<EaSpec> _attributes = new();
        private readonly List<StreamSpec> _streams = new();

        /// <summary>Extended attributes attached to this node, in the order added.</summary>
        internal IReadOnlyList<EaSpec> Attributes => _attributes;
        /// <summary>Named streams attached to this node, in the order added.</summary>
        internal IReadOnlyList<StreamSpec> Streams => _streams;

        /// <summary>
        /// Attach an implementation-use extended attribute (ECMA-167 4/14.10.8):
        /// an arbitrary byte payload keyed by an identifier string (stored as the
        /// EA's registered identifier). Returns this node so calls chain. Adding
        /// one turns the node's File Entry into a form that carries an extended
        /// attribute area between its header and allocation descriptors; a node
        /// with no attributes is unaffected.
        /// </summary>
        public Node WithAttribute(string identifier, byte[] payload)
        {
            ArgumentException.ThrowIfNullOrEmpty(identifier);
            ArgumentNullException.ThrowIfNull(payload);
            _attributes.Add(new EaSpec(identifier, (byte[])payload.Clone()));
            return this;
        }

        /// <summary>
        /// Attach a named stream (ECMA-167 4/14.17 Stream Directory ICB): a second
        /// data fork keyed by a name, its bytes held in memory. Returns this node
        /// so calls chain. Adding one causes the node to be written as an Extended
        /// File Entry (tag 266) with a stream directory; a node with no streams is
        /// still written as a plain File Entry (tag 261).
        /// </summary>
        public Node WithStream(string name, byte[] data)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentNullException.ThrowIfNull(data);
            _streams.Add(new StreamSpec(name, (byte[])data.Clone()));
            return this;
        }

        /// <summary>A file whose whole content is in memory.</summary>
        public static Node File(string name, byte[] data) =>
            new FileNode(name, data.Length) { Data = data };

        /// <summary>A file streamed on demand from <paramref name="open"/> — its
        /// bytes are never all held in memory, so an image past the 2 GB byte[]
        /// limit can be written with <see cref="BuildToStream"/>.</summary>
        public static Node File(string name, long length, Func<Stream> open) =>
            new FileNode(name, length) { Open = open };

        /// <summary>A file streamed from a path on disk.</summary>
        public static Node FileFromPath(string name, string path) =>
            new FileNode(name, new FileInfo(path).Length) { Open = () => System.IO.File.OpenRead(path) };

        public static Node Dir(string name, IEnumerable<Node> children) =>
            new DirNode(name, children.ToList());
    }

    internal sealed class FileNode(string name, long length) : Node(name)
    {
        public long Length { get; } = length;
        /// <summary>In-memory content, or null when the file is streamed.</summary>
        public byte[]? Data { get; init; }
        /// <summary>Opens the content stream, or null when the file is in memory.</summary>
        public Func<Stream>? Open { get; init; }
    }

    internal sealed class DirNode(string name, List<Node> children) : Node(name)
    {
        public IReadOnlyList<Node> Children { get; } = children;
    }

    /// <summary>One extended attribute: an identifier and its raw payload.</summary>
    internal readonly record struct EaSpec(string Identifier, byte[] Payload);

    /// <summary>One named stream: a name and its in-memory bytes.</summary>
    internal readonly record struct StreamSpec(string Name, byte[] Data);

    public sealed record BuildResult(byte[] Image, IReadOnlyList<string> Warnings);

    /// <summary>
    /// The UDF revision to stamp on the volume. 1.02 and 1.50 are structurally identical
    /// for a read-only random-access image (a Type-1 physical partition), so both are
    /// fully conformant here — 1.50 differs only in the revision recorded in the domain
    /// identifiers and the integrity descriptor. UDF 2.00+ needs extended file entries and
    /// descriptor-version 3 (2.50/2.60 also the metadata partition), which are separate
    /// structural work — see docs/UDF.md.
    ///
    /// UDF 2.60 for a MASTERED (read-only, random-access) image is structurally identical to 2.50 —
    /// the same metadata partition and descriptor-version 3 — with the revision stamped 0x0260. The
    /// feature 2.60 adds over 2.50 is the *pseudo-overwrite* (POW) partition for INCREMENTAL BD-R
    /// recording; that applies to sequential packet writing, not to authoring a complete image, so it
    /// is intentionally out of scope here (DiscForge masters whole images, it does not packet-write).
    /// </summary>
    public enum UdfRevision { Udf102, Udf150, Udf200, Udf201, Udf250, Udf260 }

    private static ushort RevisionNumber(UdfRevision r) => r switch
    {
        UdfRevision.Udf102 => 0x0102,
        UdfRevision.Udf150 => 0x0150,
        UdfRevision.Udf200 => 0x0200,
        UdfRevision.Udf201 => 0x0201,
        UdfRevision.Udf250 => 0x0250,
        UdfRevision.Udf260 => 0x0260,
        _ => 0x0102,
    };

    // The descriptor-tag version and the file-entry form are uniform across a whole
    // volume, so rather than thread them through every descriptor writer they are set
    // once per build (try/finally) and read by FinishTagBuffer / WriteFileEntry.
    // UDF ≤ 1.50 = ECMA-167 2nd edition (tag version 2, plain File Entries); UDF ≥ 2.00
    // = 3rd edition (tag version 3, Extended File Entries for every node).
    [ThreadStatic] private static ushort _tagVersion;
    [ThreadStatic] private static bool _extendedEntries;
    // UDF 2.50 (Blu-ray) stores the File Set / File Entries / directory data inside a
    // Metadata Partition — a Type-2 partition map whose blocks are addressed through a
    // Metadata File in the physical partition. We keep the whole content layout as the
    // metadata partition (partition reference 0, exactly as ≤2.01 lays it out) and wrap
    // it with a physical Type-1 map (reference 1) plus the Metadata + Mirror File Entries.
    [ThreadStatic] private static bool _metadataPartition;

    // File types for the metadata partition's special files (ECMA-167 / UDF 2.50).
    private const byte FileTypeMetadata = 250;
    private const byte FileTypeMetadataMirror = 251;
    // The Metadata File Entry and its Mirror sit in the physical partition after the content.
    private const uint MetadataFeCount = 2;

    private static void SetBuildRevision(ushort rev)
    {
        _tagVersion = rev >= 0x0200 ? (ushort)3 : (ushort)2;
        _extendedEntries = rev >= 0x0200;
        _metadataPartition = rev >= 0x0250;
    }
    private static void ClearBuildRevision() { _tagVersion = 0; _extendedEntries = false; _metadataPartition = false; }

    // ---- the public entry points -------------------------------------------

    /// <summary>Build a UDF image (revision 1.02 by default) from a tree of files and directories.</summary>
    public static byte[] Build(string volumeId, IEnumerable<Node> rootChildren,
                               UdfRevision revision = UdfRevision.Udf102)
        => BuildResultOf(volumeId, rootChildren, revision).Image;

    /// <summary>Build, returning any warnings alongside the image.</summary>
    public static BuildResult BuildResultOf(string volumeId, IEnumerable<Node> rootChildren,
                                            UdfRevision revision = UdfRevision.Udf102)
    {
        ArgumentNullException.ThrowIfNull(volumeId);
        ArgumentNullException.ThrowIfNull(rootChildren);
        ushort rev = RevisionNumber(revision);

        var plan = PlanTree(rootChildren);
        uint metaExtra = rev >= 0x0250 ? MetadataFeCount : 0;     // UDF 2.50 Metadata + Mirror FEs
        uint imageSectors = PartitionStart + plan.PartitionBlocks + metaExtra + 1;
        long imageBytes = (long)imageSectors * SectorSize;
        if (imageBytes > int.MaxValue)
            throw new NotSupportedException(
                $"This tree needs a {imageBytes / (1024 * 1024):N0} MB image, past the in-memory " +
                "builder's ~2 GB ceiling. Use BuildToStream (dforge create-udf writes to disk) for " +
                "full-size images.");

        var image = new byte[imageBytes];
        var root = plan.Root;

        SetBuildRevision(rev);
        try
        {
            WriteVolumeRecognition(image);
            WriteMainVds(image, MainVdsSector, volumeId, plan.PartitionBlocks, rev);
            WriteMainVds(image, ReserveVdsSector, volumeId, plan.PartitionBlocks, rev);
            WriteIntegrity(image, plan.PartitionBlocks, CountFiles(root), CountDirs(root), rev);
            WriteAnchor(image, AnchorSector);
            WriteAnchor(image, imageSectors - 1);
            WriteFileSetDescriptor(image, root.FeBlock, volumeId, rev);
            WriteTree(image, root);
            if (_metadataPartition) WriteMetadataFiles(image, plan.PartitionBlocks);
        }
        finally { ClearBuildRevision(); }

        return new BuildResult(image, plan.Warnings);
    }

    /// <summary>
    /// Write a UDF 1.02 image straight to a seekable stream — the streamed path with
    /// no 2 GB ceiling. All volume metadata and the File Entries / directory data
    /// are built in a bounded in-memory region; only the bulk file *content* is
    /// streamed from its source, so an image larger than RAM can be authored (files
    /// supplied via <see cref="Node.File(string, long, Func{Stream})"/> or
    /// <see cref="Node.FileFromPath"/>). Output must be seekable (a file).
    /// </summary>
    public static IReadOnlyList<string> BuildToStream(string volumeId, Stream output, IEnumerable<Node> rootChildren,
                                                      UdfRevision revision = UdfRevision.Udf102)
    {
        ArgumentNullException.ThrowIfNull(volumeId);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rootChildren);
        if (!output.CanSeek)
            throw new ArgumentException("Writing a UDF image needs a seekable stream (a file).", nameof(output));
        ushort rev = RevisionNumber(revision);

        var plan = PlanTree(rootChildren);
        var root = plan.Root;
        uint metaExtra = rev >= 0x0250 ? MetadataFeCount : 0;     // UDF 2.50 Metadata + Mirror FEs
        uint imageSectors = PartitionStart + plan.PartitionBlocks + metaExtra + 1;
        long imageBytes = (long)imageSectors * SectorSize;

        // Metadata region = everything up to the first file-data block: the volume
        // descriptors, the File Set Descriptor, every File Entry and all directory
        // data. This is bounded by the tree's shape, not by total file size.
        uint metaSectors = PartitionStart + plan.FileDataStart;
        long metaBytes = (long)metaSectors * SectorSize;
        if (metaBytes > int.MaxValue)
            throw new NotSupportedException(
                $"The filesystem metadata alone needs {metaBytes / (1024 * 1024):N0} MB — too many " +
                "files for even the streamed builder's in-memory metadata region.");

        var meta = new byte[metaBytes];
        SetBuildRevision(rev);
        try
        {
            WriteVolumeRecognition(meta);
            WriteMainVds(meta, MainVdsSector, volumeId, plan.PartitionBlocks, rev);
            WriteMainVds(meta, ReserveVdsSector, volumeId, plan.PartitionBlocks, rev);
            WriteIntegrity(meta, plan.PartitionBlocks, CountFiles(root), CountDirs(root), rev);
            WriteAnchor(meta, AnchorSector);
            WriteFileSetDescriptor(meta, root.FeBlock, volumeId, rev);
            WriteTreeMetadata(meta, root);   // File Entries + directory data only

            output.SetLength(imageBytes);    // zero-fills the gaps
            output.Seek(0, SeekOrigin.Begin);
            output.Write(meta, 0, meta.Length);

            StreamFileData(output, root);
            WriteStreamStructuresToStream(output, root);
            if (_metadataPartition) WriteMetadataFilesToStream(output, plan.PartitionBlocks);
            WriteMirrorAnchor(output, imageSectors - 1);
            output.Flush();
        }
        finally { ClearBuildRevision(); }

        return plan.Warnings;
    }

    private sealed record PlanResult(Planned Root, uint PartitionBlocks, uint FileDataStart, List<string> Warnings);

    private static PlanResult PlanTree(IEnumerable<Node> rootChildren)
    {
        var warnings = new List<string>();
        var root = new Planned(new DirNode("", rootChildren.ToList()), isDir: true, parent: null);
        BuildPlannedTree(root, warnings);

        // Pass A: give every File Entry a partition-relative logical block.
        // FSD sits at block 0, so entries start at 1.
        uint next = 1;
        AssignFeBlocks(root, ref next);

        // Pass B: all directory data (metadata), then Pass C: all file data (bulk).
        // Keeping metadata contiguous at the front lets the streamed writer build it
        // in one bounded buffer and stream only the file content that follows.
        AssignDirData(root, ref next);
        uint fileDataStart = next;
        AssignFileData(root, ref next);

        // Pass D: stream directories, their File Entries and stream data. Placed
        // after all baseline blocks so a tree with no streams is numbered exactly
        // as before (this pass allocates nothing for it), keeping that output
        // byte-identical. Stream blocks therefore live in the bulk region beyond
        // fileDataStart, alongside file content.
        AssignStreamStructures(root, ref next);

        return new PlanResult(root, next, fileDataStart, warnings);
    }

    // ---- planning -----------------------------------------------------------

    private sealed class Planned(Node source, bool isDir, Planned? parent)
    {
        public Node Source { get; } = source;
        public bool IsDir { get; } = isDir;
        public Planned? Parent { get; } = parent;
        public List<Planned> Children { get; } = new();

        public uint FeBlock;          // partition LB of the File Entry
        public uint DataBlock;        // partition LB of the first data block
        public uint DataBlocks;       // blocks the data occupies
        public long DataLength;       // bytes of data (file size, or dir-data length)
        public byte[]? DirData;       // built FID stream, for directories

        /// <summary>Extended attributes to write in this entry's EA area.</summary>
        public IReadOnlyList<EaSpec> Eas { get; } = source.Attributes;
        /// <summary>The node's stream directory, when it carries named streams.
        /// Its presence is what makes the entry an Extended File Entry.</summary>
        public PlannedStreamDir? StreamDir;
    }

    /// <summary>A planned named stream: its own File Entry and data extent.</summary>
    private sealed class PlannedStream(string name, byte[] data)
    {
        public string Name { get; } = name;
        public byte[] Data { get; } = data;
        public uint FeBlock;
        public uint DataBlock;
        public uint DataBlocks;
        public long DataLength;
    }

    /// <summary>A planned stream directory: a stream-directory File Entry plus the
    /// FID stream naming each stream (ECMA-167 4/14.17 / stream directory).</summary>
    private sealed class PlannedStreamDir(List<PlannedStream> streams)
    {
        public IReadOnlyList<PlannedStream> Streams { get; } = streams;
        public uint OwnerFeBlock;     // the entry these streams hang off (parent FID)
        public uint FeBlock;
        public uint DataBlock;
        public uint DataBlocks;
        public long DataLength;
        public byte[]? DirData;
    }

    private static void BuildPlannedTree(Planned parent, List<string> warnings)
    {
        if (parent.Source is not DirNode dir) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in dir.Children)
        {
            if (!seen.Add(child.Name))
                warnings.Add($"Duplicate name '{child.Name}' in '{Display(parent)}' — UDF requires " +
                             "unique names within a directory; the later entry may be unreachable.");

            var planned = new Planned(child, child is DirNode, parent);
            if (child.Streams.Count > 0)
                planned.StreamDir = new PlannedStreamDir(
                    child.Streams.Select(st => new PlannedStream(st.Name, st.Data)).ToList());
            parent.Children.Add(planned);
            if (child is DirNode) BuildPlannedTree(planned, warnings);
        }
    }

    private static void AssignFeBlocks(Planned node, ref uint next)
    {
        node.FeBlock = next++;
        foreach (var child in node.Children) AssignFeBlocks(child, ref next);
    }

    private static void AssignDirData(Planned node, ref uint next)
    {
        if (node.IsDir)
        {
            // Size the directory data first (from the FID lengths alone), so the
            // extent's start block is known before the FIDs are built — that block
            // is each FID's tag location, and building with it correct keeps the
            // descriptors valid for strict validators, not just the reader.
            node.DataLength = DirectoryDataLength(node);
            node.DataBlocks = Blocks(node.DataLength);
            node.DataBlock = next;
            next += node.DataBlocks;
            node.DirData = BuildDirectoryData(node);
        }

        foreach (var child in node.Children) AssignDirData(child, ref next);
    }

    private static void AssignFileData(Planned node, ref uint next)
    {
        if (!node.IsDir)
        {
            long length = ((FileNode)node.Source).Length;
            node.DataLength = length;
            node.DataBlocks = Blocks(length);
            node.DataBlock = node.DataBlocks == 0 ? 0 : next;
            next += node.DataBlocks;
        }

        foreach (var child in node.Children) AssignFileData(child, ref next);
    }

    /// <summary>Lay out each node's stream directory: the stream-directory File
    /// Entry, one File Entry per stream, the FID stream naming the streams, and the
    /// stream content — all block-placed so their descriptors' tag locations are
    /// correct before they are built.</summary>
    private static void AssignStreamStructures(Planned node, ref uint next)
    {
        if (node.StreamDir is { } sd)
        {
            sd.OwnerFeBlock = node.FeBlock;
            sd.FeBlock = next++;
            foreach (var st in sd.Streams) st.FeBlock = next++;

            sd.DataLength = StreamDirectoryDataLength(sd);
            sd.DataBlocks = Blocks(sd.DataLength);
            sd.DataBlock = next;
            next += sd.DataBlocks;
            sd.DirData = BuildStreamDirectoryData(sd);

            foreach (var st in sd.Streams)
            {
                st.DataLength = st.Data.Length;
                st.DataBlocks = Blocks(st.DataLength);
                st.DataBlock = st.DataBlocks == 0 ? 0 : next;
                next += st.DataBlocks;
            }
        }

        foreach (var child in node.Children) AssignStreamStructures(child, ref next);
    }

    private static long StreamDirectoryDataLength(PlannedStreamDir sd)
    {
        long total = FidLength(null);   // the parent FID
        foreach (var st in sd.Streams) total += FidLength(st.Name);
        return total;
    }

    /// <summary>Compose a stream directory's FIDs: a parent FID (pointing at the
    /// owning entry) then one FID per stream, each naming a stream and pointing at
    /// its File Entry. Streams are files, so their FIDs carry neither the directory
    /// nor the parent characteristic.</summary>
    private static byte[] BuildStreamDirectoryData(PlannedStreamDir sd)
    {
        using var ms = new MemoryStream();
        uint tagLocation = sd.DataBlock;
        ms.Write(BuildFid(name: null, isDirectory: true, isParent: true, childFe: sd.OwnerFeBlock, tagLocation));
        foreach (var st in sd.Streams)
            ms.Write(BuildFid(st.Name, isDirectory: false, isParent: false, st.FeBlock, tagLocation));
        return ms.ToArray();
    }

    /// <summary>The byte length a directory's FID stream will occupy — a parent
    /// entry plus one per child — computed without building it, so the extent can
    /// be placed before the FIDs (which need that placement as their tag location).</summary>
    private static long DirectoryDataLength(Planned dir)
    {
        long total = FidLength(null);
        foreach (var child in dir.Children) total += FidLength(child.Source.Name);
        return total;
    }

    private static int FidLength(string? name)
    {
        int lFi = string.IsNullOrEmpty(name) ? 0 : 1 + Encoding.Latin1.GetByteCount(name);
        int baseLen = 38 + lFi;
        return baseLen + Pad4(baseLen);
    }

    /// <summary>Compose a directory's File Identifier Descriptors: a parent entry
    /// first, then one per child.</summary>
    private static byte[] BuildDirectoryData(Planned dir)
    {
        using var ms = new MemoryStream();
        // The parent FID points at the parent directory (root points at itself).
        // Every FID's tag location is the block the directory data lives in.
        uint parentFe = (dir.Parent ?? dir).FeBlock;
        uint tagLocation = dir.DataBlock;
        ms.Write(BuildFid(name: null, isDirectory: true, isParent: true, childFe: parentFe, tagLocation));

        foreach (var child in dir.Children)
            ms.Write(BuildFid(child.Source.Name, child.IsDir, isParent: false, child.FeBlock, tagLocation));

        return ms.ToArray();
    }

    // ---- volume-level descriptors ------------------------------------------

    private static void WriteVolumeRecognition(byte[] image)
    {
        WriteVsd(image, VrsSector + 0, "BEA01");
        WriteVsd(image, VrsSector + 1, "NSR02");
        WriteVsd(image, VrsSector + 2, "TEA01");
    }

    private static void WriteVsd(byte[] image, uint sector, string id)
    {
        int at = (int)(sector * SectorSize);
        image[at] = 0;                                   // structure type
        Encoding.ASCII.GetBytes(id).CopyTo(image, at + 1);  // 5-char identifier
        image[at + 6] = 1;                               // structure version
    }

    private static void WriteMainVds(byte[] image, uint startSector, string volumeId, uint partitionBlocks,
                                     ushort revision)
    {
        WritePrimaryVolume(image, startSector + 0, volumeId);
        WriteImplUseVolume(image, startSector + 1, volumeId, revision);
        WritePartition(image, startSector + 2, partitionBlocks);
        WriteLogicalVolume(image, startSector + 3, volumeId, revision, partitionBlocks);
        WriteUnallocatedSpace(image, startSector + 4);
        WriteTerminating(image, startSector + 5);
    }

    private static void WritePrimaryVolume(byte[] image, uint sector, string volumeId)
    {
        int at = (int)(sector * SectorSize);
        var s = image.AsSpan(at, 512);
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
        FinishTag(image, at, 512, TagPrimaryVolume, sector);
    }

    private static void WriteImplUseVolume(byte[] image, uint sector, string volumeId, ushort revision)
    {
        int at = (int)(sector * SectorSize);
        var s = image.AsSpan(at, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0);          // VDS number
        WriteRegid(s.Slice(20, 32), "*UDF LV Info", udfSuffix: true, revision: revision);  // implementation id
        // implementationUse -> UDF LVInformation: charset, LV id, three info lines.
        var iu = s.Slice(52, 460);
        WriteCharspec(iu.Slice(0, 64));
        WriteDString(iu.Slice(64, 128), volumeId);
        WriteRegid(iu.Slice(64 + 128 + 36 * 3, 32), "*DiscForge");
        FinishTag(image, at, 512, TagImplUseVolume, sector);
    }

    private static void WritePartition(byte[] image, uint sector, uint partitionBlocks)
    {
        int at = (int)(sector * SectorSize);
        var s = image.AsSpan(at, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0);          // VDS number
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], 1);          // partition flags: allocated
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], 0);          // partition number
        WriteRegid(s.Slice(24, 32), "+NSR02");                         // partition contents
        BinaryPrimitives.WriteUInt32LittleEndian(s[184..], 1);         // access type: read-only
        BinaryPrimitives.WriteUInt32LittleEndian(s[188..], PartitionStart);       // starting location
        // For UDF 2.50 the physical partition also holds the two Metadata File Entries
        // that follow the content, so it is two blocks longer than the metadata content.
        uint physicalBlocks = _metadataPartition ? partitionBlocks + MetadataFeCount : partitionBlocks;
        BinaryPrimitives.WriteUInt32LittleEndian(s[192..], physicalBlocks);       // length in blocks
        WriteRegid(s.Slice(196, 32), "*DiscForge");                    // implementation id
        FinishTag(image, at, 512, TagPartition, sector);
    }

    private static void WriteLogicalVolume(byte[] image, uint sector, string volumeId, ushort revision,
                                           uint contentBlocks)
    {
        int at = (int)(sector * SectorSize);
        var s = image.AsSpan(at, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0);          // VDS number
        WriteCharspec(s.Slice(20, 64));                                // descriptor charset
        WriteDString(s.Slice(84, 128), volumeId);                      // logical volume id
        BinaryPrimitives.WriteUInt32LittleEndian(s[212..], SectorSize);// logical block size
        WriteRegid(s.Slice(216, 32), "*OSTA UDF Compliant", udfSuffix: true, revision: revision); // domain id
        // logicalVolumeContentsUse: a long_ad to the File Set Descriptor at
        // partition-reference-0 block 0. Reference 0 is the metadata partition on a
        // UDF 2.50 volume, the physical partition otherwise.
        WriteLongAd(s.Slice(248, 16), SectorSize, logicalBlock: 0);
        WriteRegid(s.Slice(272, 32), "*DiscForge");                    // implementation id
        WriteExtentAd(s.Slice(432, 8), SectorSize, LvidSector);        // integrity sequence extent

        if (_metadataPartition)
        {
            // Two maps: [0] Type-2 Metadata (holds the content), [1] Type-1 physical.
            // Keeping the metadata map at reference 0 lets the whole content layout stay
            // exactly as ≤2.01 wrote it (every long_ad already uses reference 0).
            BinaryPrimitives.WriteUInt32LittleEndian(s[264..], 70);    // map table length (64 + 6)
            BinaryPrimitives.WriteUInt32LittleEndian(s[268..], 2);     // number of partition maps

            var meta = s.Slice(440, 64);
            meta[0] = 2;                                               // Type 2
            meta[1] = 64;                                              // length
            WriteRegid(meta.Slice(4, 32), "*UDF Metadata Partition", udfSuffix: true, revision: revision);
            BinaryPrimitives.WriteUInt16LittleEndian(meta[36..], 1);   // volume sequence number
            BinaryPrimitives.WriteUInt16LittleEndian(meta[38..], 0);   // physical partition number
            BinaryPrimitives.WriteUInt32LittleEndian(meta[40..], contentBlocks);       // metadata file location
            BinaryPrimitives.WriteUInt32LittleEndian(meta[44..], contentBlocks + 1);   // metadata mirror location
            BinaryPrimitives.WriteUInt32LittleEndian(meta[48..], 0xFFFF_FFFF);         // no bitmap file (read-only)
            BinaryPrimitives.WriteUInt32LittleEndian(meta[52..], 0);   // allocation unit size (blocks)
            BinaryPrimitives.WriteUInt16LittleEndian(meta[56..], 0);   // alignment unit size (blocks)
            meta[58] = 0;                                             // flags: mirror is not a distinct duplicate

            var phys = s.Slice(504, 6);
            phys[0] = 1;                                               // Type 1 physical
            phys[1] = 6;
            BinaryPrimitives.WriteUInt16LittleEndian(phys[2..], 1);    // volume sequence number
            BinaryPrimitives.WriteUInt16LittleEndian(phys[4..], 0);    // partition number
            FinishTag(image, at, 440 + 70, TagLogicalVolume, sector);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[264..], 6);     // map table length
            BinaryPrimitives.WriteUInt32LittleEndian(s[268..], 1);     // number of partition maps
            var map = s.Slice(440, 6);                                 // Type 1, physical
            map[0] = 1;
            map[1] = 6;
            BinaryPrimitives.WriteUInt16LittleEndian(map[2..], 1);     // volume sequence number
            BinaryPrimitives.WriteUInt16LittleEndian(map[4..], 0);     // partition number
            FinishTag(image, at, 446, TagLogicalVolume, sector);
        }
    }

    private static void WriteUnallocatedSpace(byte[] image, uint sector)
    {
        int at = (int)(sector * SectorSize);
        var s = image.AsSpan(at, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0);          // VDS number
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], 0);          // number of allocation descriptors
        FinishTag(image, at, 24, TagUnallocatedSpace, sector);
    }

    private static void WriteTerminating(byte[] image, uint sector)
    {
        int at = (int)(sector * SectorSize);
        FinishTag(image, at, 512, TagTerminating, sector);
    }

    private static void WriteIntegrity(byte[] image, uint partitionBlocks, int files, int dirs, ushort revision)
    {
        int at = (int)(LvidSector * SectorSize);
        var s = image.AsSpan(at, 512);
        WriteTimestamp(s.Slice(16, 12));                               // recording time
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], 1);          // integrity type: close
        // logicalVolumeContentsUse (32): next uniqueId in the low 8 bytes.
        BinaryPrimitives.WriteUInt64LittleEndian(s[40..], (ulong)(files + dirs + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(s[72..], 1);          // number of partitions
        BinaryPrimitives.WriteUInt32LittleEndian(s[76..], 46);         // length of implementation use
        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], 0);          // free space (partition 0)
        // The physical partition (number 0) holds the content plus, on UDF 2.50, the two
        // Metadata File Entries that follow it.
        uint physicalBlocks = _metadataPartition ? partitionBlocks + MetadataFeCount : partitionBlocks;
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], physicalBlocks);   // size (partition 0)
        // implementationUse: regid, then file/dir counts and UDF revision limits.
        var iu = s.Slice(88, 46);
        WriteRegid(iu.Slice(0, 32), "*DiscForge");
        BinaryPrimitives.WriteUInt32LittleEndian(iu[32..], (uint)files);
        BinaryPrimitives.WriteUInt32LittleEndian(iu[36..], (uint)dirs);
        BinaryPrimitives.WriteUInt16LittleEndian(iu[40..], revision);  // min UDF read
        BinaryPrimitives.WriteUInt16LittleEndian(iu[42..], revision);  // min UDF write
        BinaryPrimitives.WriteUInt16LittleEndian(iu[44..], revision);  // max UDF write
        FinishTag(image, at, 88 + 46, TagLogicalVolumeIntegrity, LvidSector);
    }

    private static void WriteAnchor(byte[] image, uint sector)
    {
        int at = (int)((long)sector * SectorSize);
        var s = image.AsSpan(at, 512);
        WriteExtentAd(s.Slice(16, 8), VdsSectors * SectorSize, MainVdsSector);
        WriteExtentAd(s.Slice(24, 8), VdsSectors * SectorSize, ReserveVdsSector);
        FinishTag(image, at, 512, TagAnchor, sector);
    }

    private static void WriteFileSetDescriptor(byte[] image, uint rootFeBlock, string volumeId, ushort revision)
    {
        int at = (int)(PartitionStart * SectorSize);   // partition block 0
        var s = image.AsSpan(at, 512);
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
        // rootDirectoryICB: a long_ad to the root directory's File Entry.
        WriteLongAd(s.Slice(400, 16), SectorSize, rootFeBlock);
        WriteRegid(s.Slice(416, 32), "*OSTA UDF Compliant", udfSuffix: true, revision: revision); // domain id
        // Descriptors recorded inside a partition carry a PARTITION-RELATIVE tag location
        // (ECMA-167 4/7.2.1); the FSD sits at partition block 0, so its tag location is 0,
        // not the absolute sector. Writing the absolute sector here made strict readers
        // (udfinfo, and some OS drivers) fail to locate the File Set Descriptor.
        FinishTag(image, at, 512, TagFileSet, 0);                      // tag location: partition block 0
    }

    // ---- the tree -----------------------------------------------------------

    private static void WriteTree(byte[] image, Planned node)
    {
        WriteFileEntry(image, node);
        if (node.IsDir)
            WriteDirectoryData(image, node);
        else
            WriteFileData(image, node);

        if (node.StreamDir is not null)
            WriteStreamStructures(image, node.StreamDir);

        foreach (var child in node.Children) WriteTree(image, child);
    }

    // The metadata half of WriteTree, for the streamed path: File Entries and
    // directory data (both bounded), but not the bulk file content.
    private static void WriteTreeMetadata(byte[] image, Planned node)
    {
        WriteFileEntry(image, node);
        if (node.IsDir) WriteDirectoryData(image, node);
        foreach (var child in node.Children) WriteTreeMetadata(image, child);
    }

    // Stream every file's content to its extent in the output, without buffering
    // whole files. In-memory files are written directly; streamed files are copied
    // from their source.
    private static void StreamFileData(Stream output, Planned node)
    {
        if (!node.IsDir && node.DataBlocks > 0)
        {
            var file = (FileNode)node.Source;
            long at = (long)(PartitionStart + node.DataBlock) * SectorSize;
            output.Seek(at, SeekOrigin.Begin);
            if (file.Data is not null)
                output.Write(file.Data, 0, file.Data.Length);
            else if (file.Open is not null)
                CopyExact(file.Open(), output, file.Length);
        }
        foreach (var child in node.Children) StreamFileData(output, child);
    }

    private static void WriteMirrorAnchor(Stream output, uint sector)
    {
        // The mirror anchor lives in the final sector, outside the metadata region,
        // so build it in a one-sector buffer and place it directly.
        var s = new byte[SectorSize];
        WriteExtentAd(s.AsSpan(16, 8), VdsSectors * SectorSize, MainVdsSector);
        WriteExtentAd(s.AsSpan(24, 8), VdsSectors * SectorSize, ReserveVdsSector);
        FinishTagBuffer(s, 0, 512, TagAnchor, sector);
        output.Seek((long)sector * SectorSize, SeekOrigin.Begin);
        output.Write(s, 0, s.Length);
    }

    private static void CopyExact(Stream source, Stream dest, long count)
    {
        using (source)
        {
            var buffer = new byte[64 * 1024];
            long remaining = count;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int n = source.Read(buffer, 0, want);
                if (n <= 0) break;
                dest.Write(buffer, 0, n);
                remaining -= n;
            }
        }
    }

    private static void WriteFileEntry(byte[] image, Planned node)
    {
        uint physSector = PartitionStart + node.FeBlock;
        int at = (int)((long)physSector * SectorSize);
        var s = image.AsSpan(at, SectorSize);

        byte[] eaArea = BuildExtendedAttributeArea(node.Eas, node.FeBlock);
        byte fileType = node.IsDir ? FileTypeDirectory : FileTypeRegular;
        ushort linkCount = (ushort)(node.IsDir ? 1 + node.Children.Count(c => c.IsDir) : 1);

        if (node.StreamDir is null && !_extendedEntries)
        {
            // Plain File Entry (tag 261). With no extended attributes this writes
            // exactly the baseline bytes: L_EA = 0 and the ADs begin at offset 176.
            // (UDF ≤ 1.50; UDF ≥ 2.00 uses the Extended File Entry below for every node.)
            var icb = s.Slice(16, 20);
            BinaryPrimitives.WriteUInt16LittleEndian(icb[4..], 4);         // strategy type 4
            BinaryPrimitives.WriteUInt16LittleEndian(icb[8..], 1);         // number of entries
            icb[11] = fileType;                                           // file type
            BinaryPrimitives.WriteUInt16LittleEndian(icb[18..], 0);        // flags: short_ad (type 0)

            BinaryPrimitives.WriteUInt32LittleEndian(s[36..], 0xFFFF_FFFF);// uid: none
            BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 0xFFFF_FFFF);// gid: none
            BinaryPrimitives.WriteUInt32LittleEndian(s[44..], node.IsDir ? 0x14A5u : 0x10A5u); // permissions
            BinaryPrimitives.WriteUInt16LittleEndian(s[48..], linkCount);  // link count
            BinaryPrimitives.WriteUInt64LittleEndian(s[56..], (ulong)node.DataLength);       // information length
            BinaryPrimitives.WriteUInt64LittleEndian(s[64..], node.DataBlocks);              // logical blocks recorded
            WriteTimestamp(s.Slice(72, 12));                               // access time
            WriteTimestamp(s.Slice(84, 12));                               // modification time
            WriteTimestamp(s.Slice(96, 12));                               // attribute time
            BinaryPrimitives.WriteUInt32LittleEndian(s[108..], 1);         // checkpoint
            WriteRegid(s.Slice(128, 32), "*DiscForge");                    // implementation id
            BinaryPrimitives.WriteUInt64LittleEndian(s[160..], (ulong)node.FeBlock + 16);    // unique id

            int eaLen = eaArea.Length;
            if (eaLen > 0) eaArea.CopyTo(s.Slice(176, eaLen));
            int adOffset = 176 + eaLen;
            int adLen = WriteShortAds(s, adOffset, node.DataLength, node.DataBlock);
            BinaryPrimitives.WriteUInt32LittleEndian(s[168..], (uint)eaLen);  // length of EA
            BinaryPrimitives.WriteUInt32LittleEndian(s[172..], (uint)adLen);  // length of ADs

            FinishTag(image, at, 176 + eaLen + adLen, TagFileEntry, node.FeBlock);
        }
        else
        {
            // Extended File Entry (tag 266): the only entry form with a Stream
            // Directory ICB, so a node with named streams must be written this way.
            var icb = s.Slice(16, 20);
            BinaryPrimitives.WriteUInt16LittleEndian(icb[4..], 4);         // strategy type 4
            BinaryPrimitives.WriteUInt16LittleEndian(icb[8..], 1);         // number of entries
            icb[11] = fileType;                                           // file type
            BinaryPrimitives.WriteUInt16LittleEndian(icb[18..], 0);        // flags: short_ad (type 0)

            BinaryPrimitives.WriteUInt32LittleEndian(s[36..], 0xFFFF_FFFF);// uid: none
            BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 0xFFFF_FFFF);// gid: none
            BinaryPrimitives.WriteUInt32LittleEndian(s[44..], node.IsDir ? 0x14A5u : 0x10A5u); // permissions
            BinaryPrimitives.WriteUInt16LittleEndian(s[48..], linkCount);  // link count
            BinaryPrimitives.WriteUInt64LittleEndian(s[56..], (ulong)node.DataLength);       // information length
            BinaryPrimitives.WriteUInt64LittleEndian(s[64..], (ulong)node.DataLength);       // object size
            BinaryPrimitives.WriteUInt64LittleEndian(s[72..], node.DataBlocks);              // logical blocks recorded
            WriteTimestamp(s.Slice(80, 12));                               // access time
            WriteTimestamp(s.Slice(92, 12));                               // modification time
            WriteTimestamp(s.Slice(104, 12));                              // creation time
            WriteTimestamp(s.Slice(116, 12));                              // attribute time
            BinaryPrimitives.WriteUInt32LittleEndian(s[128..], 1);         // checkpoint
            // 132: reserved (4), 136: Extended Attribute ICB long_ad (zero — EAs
            // are stored inline in the EA area below, not in a separate EA file).
            // The Stream Directory ICB (152) points at the named-stream directory when
            // there is one; a node with no streams (every node on a plain UDF 2.00 tree)
            // leaves it a zero long_ad.
            if (node.StreamDir is not null)
                WriteLongAd(s.Slice(152, 16), SectorSize, node.StreamDir.FeBlock); // Stream Directory ICB
            WriteRegid(s.Slice(168, 32), "*DiscForge");                    // implementation id
            BinaryPrimitives.WriteUInt64LittleEndian(s[200..], (ulong)node.FeBlock + 16);    // unique id

            int eaLen = eaArea.Length;
            if (eaLen > 0) eaArea.CopyTo(s.Slice(216, eaLen));
            int adOffset = 216 + eaLen;
            int adLen = WriteShortAds(s, adOffset, node.DataLength, node.DataBlock);
            BinaryPrimitives.WriteUInt32LittleEndian(s[208..], (uint)eaLen);  // length of EA
            BinaryPrimitives.WriteUInt32LittleEndian(s[212..], (uint)adLen);  // length of ADs

            FinishTag(image, at, 216 + eaLen + adLen, TagExtendedFileEntry, node.FeBlock);
        }
    }

    /// <summary>
    /// Write a Metadata File Entry (file type 250) or its Mirror (251) for a UDF 2.50
    /// volume. It is an Extended File Entry in the physical partition whose single
    /// short_ad extent covers the whole metadata content (physical blocks 0..N-1), so a
    /// reader resolves each metadata-partition block through it.
    /// </summary>
    private static void WriteMetadataFileEntry(byte[] image, int at, byte fileType, uint feBlock, uint contentBlocks)
    {
        var s = image.AsSpan(at, SectorSize);
        s.Clear();
        var icb = s.Slice(16, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(icb[4..], 4);         // strategy type 4
        BinaryPrimitives.WriteUInt16LittleEndian(icb[8..], 1);         // number of entries
        icb[11] = fileType;                                           // 250 metadata / 251 mirror
        BinaryPrimitives.WriteUInt16LittleEndian(icb[18..], 0);        // flags: short_ad

        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], 0xFFFF_FFFF);// uid
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 0xFFFF_FFFF);// gid
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 0x14A5u);    // permissions
        BinaryPrimitives.WriteUInt16LittleEndian(s[48..], 1);          // link count
        ulong bytes = (ulong)contentBlocks * SectorSize;
        BinaryPrimitives.WriteUInt64LittleEndian(s[56..], bytes);      // information length
        BinaryPrimitives.WriteUInt64LittleEndian(s[64..], bytes);      // object size
        BinaryPrimitives.WriteUInt64LittleEndian(s[72..], contentBlocks); // logical blocks recorded
        WriteTimestamp(s.Slice(80, 12));
        WriteTimestamp(s.Slice(92, 12));
        WriteTimestamp(s.Slice(104, 12));
        WriteTimestamp(s.Slice(116, 12));
        BinaryPrimitives.WriteUInt32LittleEndian(s[128..], 1);         // checkpoint
        WriteRegid(s.Slice(168, 32), "*DiscForge");                    // implementation id
        BinaryPrimitives.WriteUInt64LittleEndian(s[200..], 0);         // unique id: 0 for the metadata system files

        int adLen = WriteShortAds(s, 216, (long)bytes, dataBlock: 0);  // physical blocks 0..N-1
        BinaryPrimitives.WriteUInt32LittleEndian(s[208..], 0);         // length of EA
        BinaryPrimitives.WriteUInt32LittleEndian(s[212..], (uint)adLen);// length of ADs
        FinishTagBuffer(image, at, 216 + adLen, TagExtendedFileEntry, feBlock);
    }

    /// <summary>Write the Metadata File Entry and its Mirror into the in-memory image,
    /// at the two physical blocks that follow the content.</summary>
    private static void WriteMetadataFiles(byte[] image, uint contentBlocks)
    {
        int mainAt = (int)((long)(PartitionStart + contentBlocks) * SectorSize);
        int mirrorAt = (int)((long)(PartitionStart + contentBlocks + 1) * SectorSize);
        WriteMetadataFileEntry(image, mainAt, FileTypeMetadata, contentBlocks, contentBlocks);
        WriteMetadataFileEntry(image, mirrorAt, FileTypeMetadataMirror, contentBlocks + 1, contentBlocks);
    }

    /// <summary>The streamed counterpart — the two Metadata File Entries written straight
    /// to the output at their physical blocks (they sit past the bounded metadata region).</summary>
    private static void WriteMetadataFilesToStream(Stream output, uint contentBlocks)
    {
        var buf = new byte[2 * SectorSize];
        WriteMetadataFileEntry(buf, 0, FileTypeMetadata, contentBlocks, contentBlocks);
        WriteMetadataFileEntry(buf, SectorSize, FileTypeMetadataMirror, contentBlocks + 1, contentBlocks);
        output.Seek((long)(PartitionStart + contentBlocks) * SectorSize, SeekOrigin.Begin);
        output.Write(buf, 0, buf.Length);
    }

    /// <summary>Write one or more short_ads covering a data extent, and return the
    /// bytes written. An empty extent produces no descriptors.</summary>
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
            WriteShortAd(s.Slice(o, 8), chunk, pos);
            o += 8;
            written += 8;
            uint blocks = Blocks(chunk);
            pos += blocks;
            remaining -= chunk;
        }
        return written;
    }

    /// <summary>
    /// Build a File Entry's extended-attribute area (ECMA-167 4/14.10): an Extended
    /// Attribute Header Descriptor (tag 262) whose Implementation- and Application-
    /// Attributes-Location fields give the byte offsets, from the start of the area,
    /// of the first implementation-use and application-use attribute; followed by
    /// the attributes themselves. We author implementation-use attributes only
    /// (type 2048): each is keyed by its registered identifier and carries the raw
    /// payload as its implementation-use bytes. Returns an empty array when the node
    /// has no attributes, so the caller writes the baseline entry unchanged.
    /// </summary>
    private static byte[] BuildExtendedAttributeArea(IReadOnlyList<EaSpec> eas, uint tagLocation)
    {
        if (eas.Count == 0) return Array.Empty<byte>();

        using var body = new MemoryStream();
        foreach (var ea in eas)
        {
            int iuLen = ea.Payload.Length;
            int attrLen = 48 + iuLen;
            attrLen += Pad4(attrLen);                 // each attribute is 4-byte aligned
            var attr = new byte[attrLen];
            var a = attr.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(a, EaTypeImplementationUse); // attribute type
            a[4] = 1;                                                              // attribute subtype
            BinaryPrimitives.WriteUInt32LittleEndian(a[8..], (uint)attrLen);       // attribute length
            BinaryPrimitives.WriteUInt32LittleEndian(a[12..], (uint)iuLen);        // impl-use length
            WriteRegid(a.Slice(16, 32), ea.Identifier);                            // implementation id
            ea.Payload.CopyTo(a.Slice(48, iuLen));                                 // implementation use
            body.Write(attr);
        }

        var attrs = body.ToArray();
        const int headerLen = 24;                     // tag (16) + two location fields (8)
        int total = headerLen + attrs.Length;
        var area = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(16), headerLen);      // impl-attrs location
        BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(20), (uint)total);    // app-attrs location: none
        attrs.CopyTo(area, headerLen);
        FinishTagBuffer(area, 0, headerLen, TagExtendedAttrHeader, tagLocation);
        return area;
    }

    // ---- named streams ------------------------------------------------------

    /// <summary>Write a node's stream directory, its stream File Entries and each
    /// stream's content into the in-memory image.</summary>
    private static void WriteStreamStructures(byte[] image, PlannedStreamDir sd)
    {
        WriteStreamDirectoryEntry(image, (int)((long)(PartitionStart + sd.FeBlock) * SectorSize), sd);
        WriteExtentBytes(image, sd.DataBlock, sd.DirData!);
        foreach (var st in sd.Streams)
        {
            WriteStreamFileEntry(image, (int)((long)(PartitionStart + st.FeBlock) * SectorSize), st);
            if (st.DataBlocks > 0)
                Array.Copy(st.Data, 0, image, (long)(PartitionStart + st.DataBlock) * SectorSize, st.Data.Length);
        }
    }

    /// <summary>The streamed-writer counterpart: the same structures written
    /// straight to the output, since they live beyond the bounded metadata region.</summary>
    private static void WriteStreamStructuresToStream(Stream output, Planned node)
    {
        if (node.StreamDir is { } sd)
        {
            var feBuf = new byte[SectorSize];
            WriteStreamDirectoryEntry(feBuf, 0, sd);
            SeekWriteBlock(output, sd.FeBlock, feBuf, feBuf.Length);
            SeekWriteBlock(output, sd.DataBlock, sd.DirData!, sd.DirData!.Length);
            foreach (var st in sd.Streams)
            {
                var sfe = new byte[SectorSize];
                WriteStreamFileEntry(sfe, 0, st);
                SeekWriteBlock(output, st.FeBlock, sfe, sfe.Length);
                if (st.DataBlocks > 0) SeekWriteBlock(output, st.DataBlock, st.Data, st.Data.Length);
            }
        }
        foreach (var child in node.Children) WriteStreamStructuresToStream(output, child);
    }

    private static void SeekWriteBlock(Stream output, uint partitionBlock, byte[] data, int length)
    {
        output.Seek((long)(PartitionStart + partitionBlock) * SectorSize, SeekOrigin.Begin);
        output.Write(data, 0, length);
    }

    /// <summary>Write a stream directory as an Extended File Entry with file type
    /// 13 (stream directory); its allocation descriptors point at the FID stream
    /// naming the streams.</summary>
    private static void WriteStreamDirectoryEntry(byte[] buffer, int at, PlannedStreamDir sd)
    {
        var s = buffer.AsSpan(at, SectorSize);
        s.Clear();
        var icb = s.Slice(16, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(icb[4..], 4);         // strategy type 4
        BinaryPrimitives.WriteUInt16LittleEndian(icb[8..], 1);         // number of entries
        icb[11] = FileTypeStreamDirectory;                            // file type 13
        BinaryPrimitives.WriteUInt16LittleEndian(icb[18..], 0);        // flags: short_ad

        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], 0xFFFF_FFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 0xFFFF_FFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 0x14A5u);     // directory-like permissions
        BinaryPrimitives.WriteUInt16LittleEndian(s[48..], 1);          // link count
        BinaryPrimitives.WriteUInt64LittleEndian(s[56..], (ulong)sd.DataLength);   // information length
        BinaryPrimitives.WriteUInt64LittleEndian(s[64..], (ulong)sd.DataLength);   // object size
        BinaryPrimitives.WriteUInt64LittleEndian(s[72..], sd.DataBlocks);          // logical blocks recorded
        WriteTimestamp(s.Slice(80, 12));
        WriteTimestamp(s.Slice(92, 12));
        WriteTimestamp(s.Slice(104, 12));
        WriteTimestamp(s.Slice(116, 12));
        BinaryPrimitives.WriteUInt32LittleEndian(s[128..], 1);         // checkpoint
        WriteRegid(s.Slice(168, 32), "*DiscForge");
        BinaryPrimitives.WriteUInt64LittleEndian(s[200..], (ulong)sd.FeBlock + 16);

        int adLen = WriteShortAds(s, 216, sd.DataLength, sd.DataBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(s[208..], 0);          // length of EA
        BinaryPrimitives.WriteUInt32LittleEndian(s[212..], (uint)adLen); // length of ADs
        FinishTagBuffer(buffer, at, 216 + adLen, TagExtendedFileEntry, sd.FeBlock);
    }

    /// <summary>Write one stream's File Entry — a plain File Entry (tag 261),
    /// exactly like a regular file's, addressing the stream's data extent.</summary>
    private static void WriteStreamFileEntry(byte[] buffer, int at, PlannedStream st)
    {
        var s = buffer.AsSpan(at, SectorSize);
        s.Clear();
        var icb = s.Slice(16, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(icb[4..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(icb[8..], 1);
        icb[11] = FileTypeRegular;
        BinaryPrimitives.WriteUInt16LittleEndian(icb[18..], 0);

        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], 0xFFFF_FFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 0xFFFF_FFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 0x10A5u);
        BinaryPrimitives.WriteUInt16LittleEndian(s[48..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(s[56..], (ulong)st.DataLength);
        BinaryPrimitives.WriteUInt64LittleEndian(s[64..], st.DataBlocks);
        WriteTimestamp(s.Slice(72, 12));
        WriteTimestamp(s.Slice(84, 12));
        WriteTimestamp(s.Slice(96, 12));
        BinaryPrimitives.WriteUInt32LittleEndian(s[108..], 1);
        WriteRegid(s.Slice(128, 32), "*DiscForge");
        BinaryPrimitives.WriteUInt64LittleEndian(s[160..], (ulong)st.FeBlock + 16);

        int adLen = WriteShortAds(s, 176, st.DataLength, st.DataBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(s[168..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[172..], (uint)adLen);
        FinishTagBuffer(buffer, at, 176 + adLen, TagFileEntry, st.FeBlock);
    }

    private static void WriteDirectoryData(byte[] image, Planned node)
    {
        var data = node.DirData!;
        // Patch each FID's tag location to the real data block now that it is known,
        // then copy the stream into the allocated extent.
        WriteExtentBytes(image, node.DataBlock, data);
    }

    private static void WriteFileData(byte[] image, Planned node)
    {
        if (node.DataBlocks == 0) return;
        var file = (FileNode)node.Source;
        long at = (long)(PartitionStart + node.DataBlock) * SectorSize;
        if (file.Data is not null)
            Array.Copy(file.Data, 0, image, at, file.Data.Length);
        else if (file.Open is not null)
        {
            // Streamed source used on the in-memory path: read it into the extent.
            using var src = file.Open();
            long remaining = file.Length;
            long o = at;
            var buffer = new byte[64 * 1024];
            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int n = src.Read(buffer, 0, want);
                if (n <= 0) break;
                Array.Copy(buffer, 0, image, o, n);
                o += n;
                remaining -= n;
            }
        }
    }

    private static void WriteExtentBytes(byte[] image, uint partitionBlock, byte[] data)
    {
        long at = (long)(PartitionStart + partitionBlock) * SectorSize;
        Array.Copy(data, 0, image, at, data.Length);
        // The rest of the final block stays zero (the buffer is zero-initialised).
    }

    // ---- File Identifier Descriptors ---------------------------------------

    private static byte[] BuildFid(string? name, bool isDirectory, bool isParent, uint childFe, uint tagLocation)
    {
        // Name encoding: OSTA compressed Unicode, compression id 8 (8-bit) for the
        // ASCII/Latin-1 names DVD-Video uses. The parent entry has no name.
        byte[] encoded = name is null || name.Length == 0
            ? Array.Empty<byte>()
            : EncodeOstaName(name);

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

        FinishTagBuffer(fid, 0, total, TagFileIdentifier, tagLocation);
        return fid;
    }

    // ---- tag, CRC and checksum ---------------------------------------------

    private static void FinishTag(byte[] image, int at, int descriptorLength, ushort tagId, uint tagLocation)
        => FinishTagBuffer(image, at, descriptorLength, tagId, tagLocation);

    /// <summary>Fill in a descriptor tag: identifier, version, serial, the CRC-16
    /// over the descriptor body, and finally the one-byte tag checksum.</summary>
    private static void FinishTagBuffer(byte[] buffer, int at, int descriptorLength, ushort tagId, uint tagLocation)
    {
        var tag = buffer.AsSpan(at, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(tag, tagId);          // tag identifier
        BinaryPrimitives.WriteUInt16LittleEndian(tag[2..], _tagVersion == 0 ? (ushort)2 : _tagVersion); // descriptor version (2 = UDF ≤1.50, 3 = ≥2.00)
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

    private static void WriteRegid(Span<byte> s, string id, bool udfSuffix = false, ushort revision = 0x0102)
    {
        s.Clear();
        s[0] = 0;                                                      // flags
        var idBytes = Encoding.ASCII.GetBytes(id);
        idBytes.AsSpan(0, Math.Min(idBytes.Length, 23)).CopyTo(s[1..]);
        if (udfSuffix)
        {
            // UDF identifier suffix: UDF revision (LE) then OS class/identifier.
            s[24] = (byte)revision;
            s[25] = (byte)(revision >> 8);
        }
    }

    private static void WriteDString(Span<byte> field, string value)
    {
        field.Clear();
        if (string.IsNullOrEmpty(value)) return;

        var bytes = Encoding.Latin1.GetBytes(value);
        int maxChars = field.Length - 2;                              // 1 for id, 1 for length
        int take = Math.Min(bytes.Length, maxChars);
        field[0] = 8;                                                 // 8-bit compression
        bytes.AsSpan(0, take).CopyTo(field[1..]);
        field[^1] = (byte)(take + 1);                                 // length of the used portion
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
        // typeAndTimezone: type 1 (local), timezone 0.
        BinaryPrimitives.WriteUInt16LittleEndian(s, 0x1000);
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

    private static void WriteShortAd(Span<byte> s, uint lengthBytes, uint logicalBlock)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(s, lengthBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], logicalBlock);
    }

    private static void WriteExtentAd(Span<byte> s, uint lengthBytes, uint location)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(s, lengthBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], location);
    }

    // ---- helpers ------------------------------------------------------------

    private static uint Blocks(long bytes) => (uint)((bytes + SectorSize - 1) / SectorSize);
    private static int Pad4(int n) => (4 - (n % 4)) % 4;

    private static int CountFiles(Planned node) =>
        (node.IsDir ? 0 : 1) + node.Children.Sum(CountFiles);
    private static int CountDirs(Planned node) =>
        (node.IsDir ? 1 : 0) + node.Children.Sum(CountDirs);

    private static string Display(Planned node)
    {
        var parts = new List<string>();
        for (var n = node; n?.Parent is not null; n = n.Parent) parts.Insert(0, n.Source.Name);
        return "/" + string.Join('/', parts);
    }
}
