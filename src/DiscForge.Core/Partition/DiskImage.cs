// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Identify;

namespace DiscForge.Core.Partition;

/// <summary>Thrown when a partition table cannot be parsed.</summary>
public sealed class PartitionFormatException(string message) : Exception(message);

/// <summary>
/// One partition surfaced by the composed reader: its position and geometry from
/// the partition table, plus the filesystem DiscForge detected by peeking the
/// partition's first sectors.
/// </summary>
public sealed record Partition
{
    public required int Index { get; init; }
    /// <summary>Type name from the partition table (e.g. "FAT16", "EFI System").</summary>
    public required string TypeName { get; init; }
    public required bool Bootable { get; init; }
    public required long StartByte { get; init; }
    public required long SizeBytes { get; init; }
    /// <summary>Filesystem detected inside the partition, or "unknown"/"extended".</summary>
    public required string FileSystem { get; init; }
}

/// <summary>A whole-disk image: which partitioning scheme it uses and its partitions.</summary>
public sealed record DiskImage
{
    /// <summary>"MBR", "GPT", or "APA".</summary>
    public required string Scheme { get; init; }
    /// <summary>The GPT disk GUID, when <see cref="Scheme"/> is "GPT".</summary>
    public string? DiskGuid { get; init; }
    public required IReadOnlyList<Partition> Partitions { get; init; }

    /// <summary>Detect the scheme and read the partition list (see <see cref="PartitionTable.Read"/>).</summary>
    public static DiskImage Read(Stream stream) => PartitionTable.Read(stream);
}

/// <summary>
/// Top-level whole-disk reader. Detects GPT (protective MBR plus an "EFI PART"
/// header at LBA 1) versus a classic MBR versus a PS2 APA disk, parses the
/// partition list with the scheme-specific reader, and — the payoff — reports the
/// filesystem in each partition by seeking to its start, reading a bounded chunk,
/// and running it through <see cref="FormatIdentifier"/>. The peek is capped and
/// clamped to the stream length, so it is safe even when the table claims more
/// space than the image actually holds.
/// </summary>
public static class PartitionTable
{
    // How much of each partition to sample when detecting its filesystem. 128 KB
    // covers every fixed-offset signature FormatIdentifier looks for (ISO 9660's
    // is the deepest, near 0x9000).
    private const int PeekBytes = 0x20000;

    public static DiskImage Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("Reading a partition table needs a seekable stream.", nameof(stream));
        long len = stream.Length;
        if (len < 512) throw new PartitionFormatException("Image is too small to hold a partition table.");

        var lba0 = new byte[512];
        stream.Seek(0, SeekOrigin.Begin);
        if (ReadFull(stream, lba0, 512) < 512)
            throw new PartitionFormatException("Could not read the first sector.");

        // GPT first: its protective MBR sits at LBA 0, the real table at LBA 1.
        if (GptReader.IsGpt(stream))
            return FromGpt(GptReader.Read(stream), stream);

        // APA next: a strong, distinctive magic in the first (1 KB) header.
        if (ApaReader.IsApa(stream))
            return FromApa(ApaReader.Read(stream), stream);

        // Classic MBR: 0x55AA plus at least one plausible entry.
        if (lba0[0x1FE] == 0x55 && lba0[0x1FF] == 0xAA && MbrReader.HasPlausibleEntry(lba0))
            return FromMbr(MbrReader.Read(stream), stream);

        throw new PartitionFormatException("No MBR, GPT, or APA partition table found.");
    }

    private static DiskImage FromMbr(MbrDisk mbr, Stream stream)
    {
        var parts = new List<Partition>();
        foreach (var e in mbr.Partitions)
        {
            string fs = e.IsExtended ? "extended" : DetectFileSystem(stream, e.StartByte, e.SizeBytes);
            parts.Add(new Partition
            {
                Index = e.Index,
                TypeName = e.TypeName,
                Bootable = e.Bootable,
                StartByte = e.StartByte,
                SizeBytes = e.SizeBytes,
                FileSystem = fs,
            });
        }
        return new DiskImage { Scheme = "MBR", Partitions = parts };
    }

    private static DiskImage FromGpt(GptDisk gpt, Stream stream)
    {
        var parts = new List<Partition>();
        foreach (var p in gpt.Partitions)
        {
            string label = string.IsNullOrEmpty(p.Name) ? p.TypeName : $"{p.TypeName} \"{p.Name}\"";
            parts.Add(new Partition
            {
                Index = p.Index,
                TypeName = label,
                Bootable = false,
                StartByte = p.StartByte,
                SizeBytes = p.SizeBytes,
                FileSystem = DetectFileSystem(stream, p.StartByte, p.SizeBytes),
            });
        }
        return new DiskImage { Scheme = "GPT", DiskGuid = gpt.DiskGuid, Partitions = parts };
    }

    private static DiskImage FromApa(ApaDisk apa, Stream stream)
    {
        var parts = new List<Partition>();
        foreach (var p in apa.Partitions)
        {
            string label = string.IsNullOrEmpty(p.Id) ? p.TypeName : $"{p.Id} ({p.TypeName})";
            parts.Add(new Partition
            {
                Index = p.Index,
                TypeName = label,
                Bootable = false,
                StartByte = p.StartByte,
                SizeBytes = p.SizeBytes,
                FileSystem = DetectFileSystem(stream, p.StartByte, p.SizeBytes),
            });
        }
        return new DiskImage { Scheme = "APA", Partitions = parts };
    }

    // Peek the partition's first sectors and let FormatIdentifier name the
    // filesystem. Bounded by the peek cap, the partition size, and the actual
    // stream length so a short image (or a bogus size) can never over-read.
    private static string DetectFileSystem(Stream stream, long startByte, long sizeBytes)
    {
        try
        {
            long len = stream.Length;
            if (startByte < 0 || startByte >= len) return "unknown";
            long available = len - startByte;
            long want = Math.Min(PeekBytes, Math.Min(sizeBytes > 0 ? sizeBytes : available, available));
            if (want < 512) return "unknown";

            var buffer = new byte[(int)want];
            stream.Seek(startByte, SeekOrigin.Begin);
            int got = ReadFull(stream, buffer, buffer.Length);
            if (got < 512) return "unknown";
            if (got < buffer.Length) buffer = buffer[..got];

            var id = FormatIdentifier.Identify(buffer);
            return id.Recognised ? id.Name : "unknown";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return "unknown";
        }
    }

    private static int ReadFull(Stream s, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int r = s.Read(buffer, total, count - total);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }
}
