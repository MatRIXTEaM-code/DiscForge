// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Udf;

/// <summary>An extended attribute (ECMA-167 4/14.10): an identifier and payload.</summary>
public sealed record UdfExtendedAttribute
{
    /// <summary>The attribute's registered identifier (its regid identifier).</summary>
    public required string Identifier { get; init; }
    /// <summary>The attribute's raw implementation- or application-use payload.</summary>
    public required byte[] Bytes { get; init; }
}

/// <summary>A named stream (ECMA-167 4/14.17 Stream Directory ICB) attached to an
/// entry: a second data fork keyed by a name.</summary>
public sealed record UdfNamedStream
{
    public required string Name { get; init; }
    public required long Size { get; init; }
    /// <summary>Logical block of the stream's File Entry within the partition.</summary>
    public required uint IcbBlock { get; init; }
}

/// <summary>One entry in a UDF filesystem.</summary>
public sealed record UdfEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required long Size { get; init; }
    /// <summary>Logical block of this entry's ICB within the partition.</summary>
    public required uint IcbBlock { get; init; }
    /// <summary>Extended attributes carried by this entry; empty when none.</summary>
    public IReadOnlyList<UdfExtendedAttribute> Attributes { get; init; } = Array.Empty<UdfExtendedAttribute>();
    /// <summary>Named streams attached to this entry; empty when none.</summary>
    public IReadOnlyList<UdfNamedStream> Streams { get; init; } = Array.Empty<UdfNamedStream>();
}

/// <summary>What was found on a UDF volume.</summary>
public sealed record UdfVolume
{
    public required string VolumeId { get; init; }
    /// <summary>First sector of the partition — logical blocks are relative to it.</summary>
    public required uint PartitionStart { get; init; }
    public required uint RootBlock { get; init; }
    public required IReadOnlyList<UdfEntry> Entries { get; init; }

    public IEnumerable<UdfEntry> Files => Entries.Where(e => !e.IsDirectory);
    public IEnumerable<UdfEntry> Directories => Entries.Where(e => e.IsDirectory);
    public long TotalBytes => Files.Sum(f => f.Size);
}

public sealed class UdfFormatException(string message) : Exception(message);

/// <summary>
/// Reads UDF (ECMA-167 / OSTA) volumes — the filesystem DVD-Video and Blu-ray
/// use, and which ISO 9660 cannot describe.
///
/// The structure is a chain of pointers, each descriptor naming the next:
///
///   Anchor (sector 256) -> Main Volume Descriptor Sequence
///     -> Partition Descriptor      (where the partition starts)
///     -> Logical Volume Descriptor (block size, and where the File Set is)
///   File Set Descriptor -> root directory's ICB
///   File Entry / Extended File Entry -> a file's type, size and extents
///   Directory data -> File Identifier Descriptors (name + child ICB)
///
/// Ported from docs/reference/udf_read.py, which is validated against real images
/// produced by `genisoimage -udf`. Unlike ISO 9660 there is no `isoinfo`-style
/// oracle for UDF, so the reference builds volumes with known contents and reads
/// them back — the honest substitute.
///
/// Scope: UDF with a Type 1 (physical) partition and 2048-byte logical blocks
/// (DVD-Video / UDF 1.02 and plain data discs), plus UDF 2.50's **metadata
/// partition** used by Blu-ray, resolved via <see cref="UdfMetadataPartition"/>.
/// The full resolver path is validated end to end against a hand-built UDF 2.50
/// image with a real metadata partition (anchor → VDS → metadata map → Metadata
/// File extents → File Set → directory tree, with file data reached through the
/// metadata mapping); a pressed 25 GB Blu-ray would confirm nothing further about
/// the structure, only scale.
/// </summary>
public static class UdfReader
{
    public const int SectorSize = 2048;
    private const int MaxDepth = 32;

    // Descriptor tags we care about.
    private const ushort TagAnchor = 2;
    private const ushort TagPartition = 5;
    private const ushort TagLogicalVolume = 6;
    private const ushort TagTerminating = 8;
    private const ushort TagFileSet = 256;
    private const ushort TagFileIdentifier = 257;
    private const ushort TagFileEntry = 261;
    private const ushort TagExtendedAttrHeader = 262;      // ECMA-167 4/14.10.1
    private const ushort TagExtendedFileEntry = 266;

    // Extended-attribute types (ECMA-167 4/14.10).
    private const uint EaTypeImplementationUse = 2048;
    private const uint EaTypeApplicationUse = 65536;

    private const byte FileTypeDirectory = 4;

    /// <summary>True if this stream carries a UDF volume at all.</summary>
    public static bool IsUdf(Stream image)
    {
        ArgumentNullException.ThrowIfNull(image);
        try { return TryFindAnchor(image) is not null; }
        catch { return false; }
    }

    public static UdfVolume Read(Stream image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanSeek)
            throw new ArgumentException("Reading UDF requires a seekable stream.", nameof(image));

        var anchor = TryFindAnchor(image)
            ?? throw new UdfFormatException(
                "No UDF Anchor Volume Descriptor Pointer at sector 256 — this image has no UDF " +
                "filesystem. (An ISO 9660 disc is read with IsoReader instead.)");

        // '?? throw' already unwraps the nullable, so `anchor` is an Anchor, not
        // an Anchor? — no .Value needed.
        var (partition, logical, volumeId, lvdSector) = ReadVolumeStructures(image, anchor);

        // Blu-ray / UDF 2.50: the real File Set and directory tree live inside a
        // Metadata File addressed through a Type 2 metadata partition map. When
        // one is present, resolve logical blocks through it; otherwise blocks map
        // straight to partition-relative sectors (the plain Type 1 path).
        var resolve = BuildResolver(image, partition.Start, lvdSector);

        uint fsdSector = resolve(logical.FileSetBlock);
        if (TagAt(image, fsdSector) != TagFileSet)
            throw new UdfFormatException(
                "The File Set Descriptor is not where the Logical Volume Descriptor says it is; " +
                "the volume may be damaged.");

        var fsd = ReadSector(image, fsdSector);
        uint rootBlock = BinaryPrimitives.ReadUInt32LittleEndian(fsd.AsSpan(400 + 4, 4));

        var entries = new List<UdfEntry>();
        Walk(image, resolve, rootBlock, "", entries, 0);

        return new UdfVolume
        {
            VolumeId = volumeId,
            PartitionStart = partition.Start,
            RootBlock = rootBlock,
            Entries = entries,
        };
    }

    /// <summary>Copy a file's bytes out of the volume.</summary>
    public static void ExtractFile(Stream image, UdfVolume volume, UdfEntry entry, Stream output)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        if (entry.IsDirectory)
            throw new ArgumentException($"'{entry.Path}' is a directory.", nameof(entry));

        // Rebuild the resolver so a Blu-ray file (whose File Entry lives in the
        // metadata partition) is located correctly. For a Type 1 volume this is
        // the identity mapping and behaves exactly as before.
        var anchor = TryFindAnchor(image)
            ?? throw new UdfFormatException("The image no longer has a UDF anchor.");
        var (partition, _, _, lvdSector) = ReadVolumeStructures(image, anchor);
        var resolve = BuildResolver(image, partition.Start, lvdSector);

        var fe = ReadFileEntry(image, resolve(entry.IcbBlock))
            ?? throw new UdfFormatException($"'{entry.Path}' has no File Entry.");

        WriteFileData(image, fe, resolve, output);
    }

    /// <summary>Copy a named stream's bytes out of the volume — the stream analogue
    /// of <see cref="ExtractFile"/>. The stream is found by name in the owning
    /// entry's stream directory.</summary>
    public static void ExtractStream(Stream image, UdfVolume volume, UdfEntry entry, string streamName, Stream output)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        ArgumentNullException.ThrowIfNull(output);

        var anchor = TryFindAnchor(image)
            ?? throw new UdfFormatException("The image no longer has a UDF anchor.");
        var (partition, _, _, lvdSector) = ReadVolumeStructures(image, anchor);
        var resolve = BuildResolver(image, partition.Start, lvdSector);

        var fe = ReadFileEntry(image, resolve(entry.IcbBlock))
            ?? throw new UdfFormatException($"'{entry.Path}' has no File Entry.");
        if (fe.StreamDirBlock == 0)
            throw new ArgumentException($"'{entry.Path}' has no named streams.", nameof(entry));

        var sd = ReadFileEntry(image, resolve(fe.StreamDirBlock))
            ?? throw new UdfFormatException($"'{entry.Path}' has no stream directory.");
        var data = ReadDirectoryData(image, sd, resolve);
        foreach (var fid in ReadFids(data))
        {
            if (fid.IsParent || fid.IsDeleted || fid.Name != streamName) continue;
            var streamFe = ReadFileEntry(image, resolve(fid.IcbBlock))
                ?? throw new UdfFormatException($"Stream '{streamName}' has no File Entry.");
            WriteFileData(image, streamFe, resolve, output);
            return;
        }
        throw new ArgumentException($"'{entry.Path}' has no stream named '{streamName}'.", nameof(streamName));
    }

    // ---- block resolution (Type 1 physical, or UDF 2.50 metadata partition) --

    /// <summary>
    /// Build the logical-block → physical-sector resolver for a volume. For a
    /// plain Type 1 volume this is just <c>partitionStart + block</c>. When the
    /// LVD declares a Type 2 metadata partition (Blu-ray), directory structure
    /// blocks are translated through the Metadata File's extents; blocks outside
    /// the mapped range fall back to the physical mapping, which is where file
    /// content extents (referenced from the physical partition) resolve.
    /// </summary>
    private static Func<uint, uint> BuildResolver(Stream image, uint partitionStart, byte[]? lvdSector)
    {
        if (lvdSector is null) return block => partitionStart + block;

        var maps = UdfMetadataPartition.ParsePartitionMaps(lvdSector);
        var metaMap = maps.FirstOrDefault(m => m.IsMetadata);
        if (!metaMap.IsMetadata) return block => partitionStart + block;

        var map = UdfMetadataPartition.BuildMap(image, partitionStart, metaMap.MetadataFileLocation);
        if (map is null) return block => partitionStart + block;

        // Metadata-partition blocks translate through the Metadata File extents;
        // anything outside falls back to the physical partition.
        return block => map.Translate(block) ?? (partitionStart + block);
    }

    // ---- volume structures --------------------------------------------------

    private readonly record struct Anchor(uint VdsLocation, uint VdsLength);
    private readonly record struct Partition(uint Start, uint Length);
    private readonly record struct Logical(uint BlockSize, uint FileSetBlock);

    private static Anchor? TryFindAnchor(Stream image)
    {
        long sectors = image.Length / SectorSize;
        // The anchor is mirrored so a scratched disc still has one.
        foreach (long sector in new[] { 256L, sectors - 256, sectors - 1 })
        {
            if (sector < 0 || sector >= sectors) continue;
            if (TagAt(image, (uint)sector) != TagAnchor) continue;

            var s = ReadSector(image, (uint)sector);
            if (!TagChecksumOk(s)) continue;

            uint len = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(16, 4));
            uint loc = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(20, 4));
            return new Anchor(loc, len);
        }
        return null;
    }

    private static (Partition, Logical, string VolumeId, byte[]? LvdSector) ReadVolumeStructures(Stream image, Anchor anchor)
    {
        Partition? partition = null;
        Logical? logical = null;
        string volumeId = "";
        byte[]? lvdSector = null;

        int count = Math.Max(1, (int)(anchor.VdsLength / SectorSize));
        for (int i = 0; i < count; i++)
        {
            uint sector = anchor.VdsLocation + (uint)i;
            if ((long)sector * SectorSize >= image.Length) break;

            var s = ReadSector(image, sector);
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(s);
            if (tag == 0 || !TagChecksumOk(s)) continue;

            switch (tag)
            {
                case 1:   // Primary Volume Descriptor — the label
                    volumeId = ReadDString(s.AsSpan(24, 32));
                    break;

                case TagPartition:
                    partition = new Partition(
                        BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(188, 4)),
                        BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(192, 4)));
                    break;

                case TagLogicalVolume:
                    uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(212, 4));
                    // logicalVolumeContentsUse holds a long_ad pointing at the File Set.
                    uint fsdBlock = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(248 + 4, 4));
                    logical = new Logical(blockSize, fsdBlock);
                    lvdSector = s;   // keep for partition-map (metadata partition) parsing
                    break;

                case TagTerminating:
                    i = count;   // end of the sequence
                    break;
            }
        }

        if (partition is not { } p)
            throw new UdfFormatException("UDF volume has no Partition Descriptor.");
        if (logical is not { } l)
            throw new UdfFormatException("UDF volume has no Logical Volume Descriptor.");

        if (l.BlockSize != SectorSize)
            throw new UdfFormatException(
                $"UDF logical block size is {l.BlockSize}; only {SectorSize} is supported.");

        return (p, l, volumeId, lvdSector);
    }

    // ---- ICBs ---------------------------------------------------------------

    private sealed record FileEntry(bool IsDirectory, int AdType, long Size, int AdLength, byte[] Raw,
                                    int AdOffset, int EaOffset, int EaLength, uint StreamDirBlock);

    private static FileEntry? ReadFileEntry(Stream image, uint sector)
    {
        if ((long)sector * SectorSize >= image.Length) return null;
        var s = ReadSector(image, sector);
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(s);
        if (tag is not (TagFileEntry or TagExtendedFileEntry)) return null;

        // The ICB tag sits straight after the 16-byte descriptor tag.
        byte fileType = s[16 + 11];
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(16 + 18, 2));

        long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(s.AsSpan(0x38, 8));

        // A File Entry and an Extended File Entry keep their lengths in different
        // places and have different header sizes. Mixing them up reads garbage.
        // Only the Extended File Entry carries a Stream Directory ICB (at 0x98).
        int lengthOfEa, lengthOfAd, eaOffset;
        uint streamDirBlock = 0;
        if (tag == TagFileEntry)
        {
            lengthOfEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0xA8, 4));
            lengthOfAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0xAC, 4));
            eaOffset = 0xB0;
        }
        else
        {
            lengthOfEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0xD0, 4));
            lengthOfAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0xD4, 4));
            eaOffset = 0xD8;
            // Stream Directory ICB is a long_ad: its logical block sits at 0x98 + 4.
            streamDirBlock = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0x98 + 4, 4));
        }

        int adOffset = eaOffset + lengthOfEa;
        if (adOffset < 0 || adOffset > s.Length) return null;

        return new FileEntry(
            IsDirectory: fileType == FileTypeDirectory,
            AdType: flags & 0x07,
            Size: size,
            AdLength: lengthOfAd,
            Raw: s,
            AdOffset: adOffset,
            EaOffset: eaOffset,
            EaLength: lengthOfEa,
            StreamDirBlock: streamDirBlock);
    }

    // ---- extended attributes and named streams ------------------------------

    /// <summary>Parse an entry's extended-attribute area (ECMA-167 4/14.10): the
    /// Extended Attribute Header Descriptor (tag 262) followed by implementation-
    /// and application-use attributes. Each surfaces as its identifier and payload.
    /// Returns empty when the entry has no EA area.</summary>
    private static IReadOnlyList<UdfExtendedAttribute> ParseExtendedAttributes(FileEntry fe)
    {
        if (fe.EaLength < 24) return Array.Empty<UdfExtendedAttribute>();
        int end = Math.Min(fe.EaOffset + fe.EaLength, fe.Raw.Length);
        if (fe.EaOffset + 24 > end) return Array.Empty<UdfExtendedAttribute>();
        if (BinaryPrimitives.ReadUInt16LittleEndian(fe.Raw.AsSpan(fe.EaOffset, 2)) != TagExtendedAttrHeader)
            return Array.Empty<UdfExtendedAttribute>();

        var list = new List<UdfExtendedAttribute>();
        int p = fe.EaOffset + 24;                    // attributes follow the 24-byte header
        while (p + 16 <= end)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(fe.Raw.AsSpan(p, 4));
            int attrLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(fe.Raw.AsSpan(p + 8, 4));
            if (attrLen < 16 || p + attrLen > end) break;

            if (type is EaTypeImplementationUse or EaTypeApplicationUse && p + 48 <= end)
            {
                int useLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(fe.Raw.AsSpan(p + 12, 4));
                string id = ReadRegId(fe.Raw.AsSpan(p + 16, 32));
                int dataStart = p + 48;
                int dataLen = Math.Min(useLen, Math.Max(0, p + attrLen - dataStart));
                var payload = fe.Raw.AsSpan(dataStart, dataLen).ToArray();
                list.Add(new UdfExtendedAttribute { Identifier = id, Bytes = payload });
            }
            p += attrLen;
        }
        return list;
    }

    /// <summary>Follow an Extended File Entry's Stream Directory ICB and surface its
    /// named streams (name, size, ICB). Returns empty when the entry has none.</summary>
    private static IReadOnlyList<UdfNamedStream> ReadNamedStreams(
        Stream image, Func<uint, uint> resolve, FileEntry fe)
    {
        if (fe.StreamDirBlock == 0) return Array.Empty<UdfNamedStream>();
        var sd = ReadFileEntry(image, resolve(fe.StreamDirBlock));
        if (sd is null) return Array.Empty<UdfNamedStream>();

        var data = ReadDirectoryData(image, sd, resolve);
        var list = new List<UdfNamedStream>();
        foreach (var fid in ReadFids(data))
        {
            if (fid.IsParent || fid.IsDeleted || fid.Name.Length == 0) continue;
            var streamFe = ReadFileEntry(image, resolve(fid.IcbBlock));
            list.Add(new UdfNamedStream
            {
                Name = fid.Name,
                Size = streamFe?.Size ?? 0,
                IcbBlock = fid.IcbBlock,
            });
        }
        return list;
    }

    /// <summary>Read a regid's identifier field: a flags byte then up to 23 bytes
    /// of ASCII identifier, right-padded with NULs.</summary>
    private static string ReadRegId(ReadOnlySpan<byte> regid)
    {
        if (regid.Length < 24) return "";
        return Encoding.ASCII.GetString(regid.Slice(1, 23)).TrimEnd('\0', ' ');
    }

    /// <summary>Read all of a File Entry's data into memory. Only used for
    /// directories, which are small; files stream via WriteFileData.</summary>
    private static byte[] ReadAllData(Stream image, FileEntry fe, uint partitionStart)
    {
        using var ms = new MemoryStream();
        WriteFileData(image, fe, block => partitionStart + block, ms);
        return ms.ToArray();
    }

    private static void WriteFileData(Stream image, FileEntry fe, Func<uint, uint> resolve, Stream output)
    {
        // ad_type 3 means the data is embedded in the File Entry itself — common
        // for tiny files, and silently missed if you only handle extents.
        if (fe.AdType == 3)
        {
            int available = Math.Min((int)fe.Size, fe.Raw.Length - fe.AdOffset);
            if (available > 0) output.Write(fe.Raw, fe.AdOffset, available);
            return;
        }

        long remaining = fe.Size;
        int o = fe.AdOffset;
        int end = fe.AdOffset + fe.AdLength;
        var buffer = new byte[SectorSize];

        while (o < end && remaining > 0)
        {
            uint rawLength, position;
            if (fe.AdType == 0)          // short_ad
            {
                if (o + 8 > fe.Raw.Length) break;
                rawLength = BinaryPrimitives.ReadUInt32LittleEndian(fe.Raw.AsSpan(o, 4));
                position = BinaryPrimitives.ReadUInt32LittleEndian(fe.Raw.AsSpan(o + 4, 4));
                o += 8;
            }
            else if (fe.AdType == 1)     // long_ad
            {
                if (o + 16 > fe.Raw.Length) break;
                rawLength = BinaryPrimitives.ReadUInt32LittleEndian(fe.Raw.AsSpan(o, 4));
                position = BinaryPrimitives.ReadUInt32LittleEndian(fe.Raw.AsSpan(o + 4, 4));
                o += 16;
            }
            else
            {
                throw new UdfFormatException(
                    $"Allocation descriptor type {fe.AdType} is not supported.");
            }

            // The top two bits of the length are an extent type, not length.
            long length = rawLength & 0x3FFFFFFF;
            int extentType = (int)(rawLength >> 30);
            if (length == 0) break;

            length = Math.Min(length, remaining);

            if (extentType == 0)         // recorded and allocated
            {
                // Read the extent one logical block at a time, resolving each
                // through the partition mapping. For a Type 1 volume the blocks
                // are contiguous; for a metadata-mapped extent they may not be.
                long left = length;
                uint blockInExtent = 0;
                while (left > 0)
                {
                    uint physSector = resolve(position + blockInExtent);
                    image.Seek((long)physSector * SectorSize, SeekOrigin.Begin);
                    int want = (int)Math.Min(SectorSize, left);
                    int got = 0;
                    while (got < want)
                    {
                        int n = image.Read(buffer, got, want - got);
                        if (n <= 0)
                            throw new EndOfStreamException(
                                "The image ends inside a file's data — it may be truncated.");
                        got += n;
                    }
                    output.Write(buffer, 0, want);
                    left -= want;
                    blockInExtent++;
                }
            }
            else                          // sparse / not recorded: reads as zeros
            {
                long left = length;
                Array.Clear(buffer);
                while (left > 0)
                {
                    int n = (int)Math.Min(buffer.Length, left);
                    output.Write(buffer, 0, n);
                    left -= n;
                }
            }

            remaining -= length;
        }
    }

    // ---- directories --------------------------------------------------------

    private static void Walk(Stream image, Func<uint, uint> resolve, uint block, string prefix,
                             List<UdfEntry> acc, int depth)
    {
        if (depth > MaxDepth)
            throw new UdfFormatException(
                $"Directory nesting exceeds {MaxDepth} levels at '{prefix}' — possible loop.");

        var fe = ReadFileEntry(image, resolve(block));
        if (fe is null || !fe.IsDirectory) return;

        var data = ReadDirectoryData(image, fe, resolve);

        foreach (var fid in ReadFids(data))
        {
            if (fid.IsParent || fid.IsDeleted || fid.Name.Length == 0) continue;

            var child = ReadFileEntry(image, resolve(fid.IcbBlock));
            string path = prefix + "/" + fid.Name;

            acc.Add(new UdfEntry
            {
                Name = fid.Name,
                Path = path,
                IsDirectory = fid.IsDirectory,
                Size = fid.IsDirectory || child is null ? 0 : child.Size,
                IcbBlock = fid.IcbBlock,
                Attributes = child is null ? Array.Empty<UdfExtendedAttribute>() : ParseExtendedAttributes(child),
                Streams = child is null ? Array.Empty<UdfNamedStream>() : ReadNamedStreams(image, resolve, child),
            });

            if (fid.IsDirectory)
                Walk(image, resolve, fid.IcbBlock, path, acc, depth + 1);
        }
    }

    /// <summary>Read a directory File Entry's data, resolving its extents through
    /// the block resolver (so metadata-partition directories work on Blu-ray).</summary>
    private static byte[] ReadDirectoryData(Stream image, FileEntry fe, Func<uint, uint> resolve)
    {
        // ad_type 3: embedded in the File Entry — no resolution needed.
        if (fe.AdType == 3)
            return ReadAllData(image, fe, 0);

        // ad_type 2 (extended_ad) uses 20-byte descriptors, not the 8-byte short_ad stride below.
        // Decode isn't implemented, so decline rather than walk the directory at the wrong stride and
        // silently drop its children (the file-data path declines this too — keep the two consistent).
        if (fe.AdType == 2)
            throw new NotSupportedException("UDF directory uses extended_ad (ad_type 2), which DiscForge does not decode yet — declined.");

        using var ms = new MemoryStream();
        int adSize = fe.AdType == 1 ? 16 : 8;
        for (int p = fe.AdOffset; p + adSize <= fe.AdOffset + fe.AdLength && p + adSize <= fe.Raw.Length; p += adSize)
        {
            uint lenField = BinaryPrimitives.ReadUInt32LittleEndian(fe.Raw.AsSpan(p, 4));
            uint extentBytes = lenField & 0x3FFF_FFFF;
            uint logicalBlock = BinaryPrimitives.ReadUInt32LittleEndian(fe.Raw.AsSpan(p + 4, 4));
            if (extentBytes == 0) continue;

            uint blocks = (extentBytes + SectorSize - 1) / SectorSize;
            for (uint b = 0; b < blocks; b++)
            {
                var sec = ReadSector(image, resolve(logicalBlock + b));
                int take = (int)Math.Min(SectorSize, extentBytes - b * SectorSize);
                ms.Write(sec, 0, take);
            }
        }
        return ms.ToArray();
    }

    private readonly record struct Fid(string Name, bool IsParent, bool IsDirectory,
                                       bool IsDeleted, uint IcbBlock);

    private static IEnumerable<Fid> ReadFids(byte[] data)
    {
        int p = 0;
        while (p + 38 <= data.Length)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p, 2)) != TagFileIdentifier)
                yield break;

            byte characteristics = data[p + 18];
            byte lengthOfFileId = data[p + 19];
            uint icbBlock = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 20 + 4, 4));
            ushort lengthOfImplUse = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 36, 2));

            int nameOffset = p + 38 + lengthOfImplUse;
            string name = nameOffset + lengthOfFileId <= data.Length
                ? DecodeOstaName(data.AsSpan(nameOffset, lengthOfFileId))
                : "";

            int total = 38 + lengthOfImplUse + lengthOfFileId;
            total += (4 - (total % 4)) % 4;      // FIDs are padded to 4 bytes
            if (total <= 0) yield break;

            yield return new Fid(
                name,
                IsParent: (characteristics & 0x08) != 0,
                IsDirectory: (characteristics & 0x02) != 0,
                IsDeleted: (characteristics & 0x04) != 0,
                icbBlock);

            p += total;
        }
    }

    // ---- strings ------------------------------------------------------------

    /// <summary>
    /// OSTA compressed Unicode: the first byte is a compression ID (8 = 8-bit,
    /// 16 = UTF-16BE) and is NOT part of the name.
    /// </summary>
    private static string DecodeOstaName(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0) return "";
        return raw[0] switch
        {
            8 => Encoding.Latin1.GetString(raw[1..]),
            16 => Encoding.BigEndianUnicode.GetString(raw[1..]),
            _ => Encoding.Latin1.GetString(raw).TrimEnd('\0'),
        };
    }

    /// <summary>A dstring keeps its length in the LAST byte of the field.</summary>
    private static string ReadDString(ReadOnlySpan<byte> field)
    {
        if (field.Length == 0) return "";
        int length = field[^1];
        if (length == 0 || length > field.Length - 1) return "";
        return DecodeOstaName(field[..length]).TrimEnd('\0');
    }

    // ---- raw access ---------------------------------------------------------

    private static ushort TagAt(Stream image, uint sector)
    {
        long o = (long)sector * SectorSize;
        if (o + 16 > image.Length) return 0;
        image.Seek(o, SeekOrigin.Begin);
        Span<byte> b = stackalloc byte[2];
        return image.Read(b) == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b) : (ushort)0;
    }

    private static byte[] ReadSector(Stream image, uint sector)
    {
        long o = (long)sector * SectorSize;
        if (o < 0 || o >= image.Length)
            throw new UdfFormatException($"Sector {sector} lies past the end of the image.");

        int length = (int)Math.Min(SectorSize, image.Length - o);
        var buf = new byte[SectorSize];
        image.Seek(o, SeekOrigin.Begin);
        image.ReadExactly(buf, 0, length);
        return buf;
    }

    /// <summary>Descriptor tag checksum: bytes 0-3 and 5-15 sum to byte 4.</summary>
    private static bool TagChecksumOk(byte[] sector)
    {
        if (sector.Length < 16) return false;
        int sum = 0;
        for (int i = 0; i < 4; i++) sum += sector[i];
        for (int i = 5; i < 16; i++) sum += sector[i];
        return (sum & 0xFF) == sector[4];
    }
}
