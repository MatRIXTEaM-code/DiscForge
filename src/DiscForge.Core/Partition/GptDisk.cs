// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Partition;

/// <summary>One entry from a GUID Partition Table.</summary>
public sealed record GptPartition
{
    /// <summary>1-based position among the non-empty entries.</summary>
    public required int Index { get; init; }
    /// <summary>Partition-type GUID, upper-case canonical form.</summary>
    public required string TypeGuid { get; init; }
    /// <summary>A human name for the type GUID (e.g. "EFI System").</summary>
    public required string TypeName { get; init; }
    /// <summary>Unique partition GUID, upper-case canonical form.</summary>
    public required string UniqueGuid { get; init; }
    public required long FirstLba { get; init; }
    public required long LastLba { get; init; }
    public long StartByte => FirstLba * 512;
    public long SizeBytes => (LastLba - FirstLba + 1) * 512;
    /// <summary>UTF-16LE partition name, trimmed of trailing NULs.</summary>
    public required string Name { get; init; }
}

/// <summary>A parsed GPT: the disk GUID and the non-empty partition entries.</summary>
public sealed record GptDisk
{
    public required string DiskGuid { get; init; }
    public required IReadOnlyList<GptPartition> Partitions { get; init; }
}

/// <summary>
/// Reads a GUID Partition Table. Clean-room, from the public UEFI description.
/// LBA 0 holds a protective MBR (a single 0xEE entry); the GPT header lives at
/// LBA 1 (byte offset 0x200) and opens with the ASCII signature "EFI PART". The
/// header is entirely little-endian and points at the partition-entry array
/// (starting LBA, entry count, and per-entry size — usually 128 × 128 bytes).
/// Each entry carries a type GUID, a unique GUID, first/last LBA, attributes, and
/// a 72-byte UTF-16LE name; an all-zero type GUID marks an unused slot. The three
/// GUID sub-fields are stored mixed-endian, which <see cref="System.Guid"/>
/// reconstructs from the 16 raw bytes.
/// </summary>
public static class GptReader
{
    /// <summary>Byte offset of the GPT header (LBA 1).</summary>
    public const int HeaderOffset = 0x200;

    /// <summary>True if "EFI PART" is present at LBA 1.</summary>
    public static bool IsGpt(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length < HeaderOffset + 8) return false;
        var sig = new byte[8];
        stream.Seek(HeaderOffset, SeekOrigin.Begin);
        if (ReadFull(stream, sig, 8) < 8) return false;
        return IsSignature(sig, 0);
    }

    public static GptDisk Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var ms = new MemoryStream(data, writable: false);
        return Read(ms);
    }

    public static GptDisk Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("Reading a partition table needs a seekable stream.", nameof(stream));
        long len = stream.Length;
        if (len < HeaderOffset + 92)
            throw new PartitionFormatException("Image is too small to hold a GPT header.");

        var header = new byte[92];
        stream.Seek(HeaderOffset, SeekOrigin.Begin);
        if (ReadFull(stream, header, header.Length) < header.Length)
            throw new PartitionFormatException("Truncated GPT header.");
        if (!IsSignature(header, 0))
            throw new PartitionFormatException("No GPT signature (\"EFI PART\" at LBA 1).");

        var diskGuid = new Guid(header.AsSpan(0x38, 16).ToArray()).ToString().ToUpperInvariant();
        long entriesLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x48));
        long count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x50));
        int entrySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x54));

        if (entrySize < 128 || entrySize > 4096)
            throw new PartitionFormatException($"Implausible GPT entry size {entrySize}.");
        if (count < 0) count = 0;

        long entriesOffset = entriesLba * 512;
        if (entriesLba <= 0 || entriesOffset >= len)
            throw new PartitionFormatException("GPT entry array lies outside the image.");

        // Cap the count both to a sane ceiling and to what the image can hold.
        long maxByLen = (len - entriesOffset) / entrySize;
        if (maxByLen < 0) maxByLen = 0;
        count = Math.Min(count, Math.Min(maxByLen, 4096));

        var partitions = new List<GptPartition>();
        var entry = new byte[entrySize];
        stream.Seek(entriesOffset, SeekOrigin.Begin);

        int index = 1;
        for (long i = 0; i < count; i++)
        {
            if (ReadFull(stream, entry, entrySize) < entrySize) break;
            if (IsAllZero(entry, 0, 16)) continue;   // empty type GUID → unused slot

            string typeGuid = new Guid(entry.AsSpan(0x00, 16).ToArray()).ToString().ToUpperInvariant();
            string uniqueGuid = new Guid(entry.AsSpan(0x10, 16).ToArray()).ToString().ToUpperInvariant();
            long first = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(0x20));
            long last = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(0x28));
            string name = ReadUtf16Name(entry, 0x38, 72);

            partitions.Add(new GptPartition
            {
                Index = index++,
                TypeGuid = typeGuid,
                TypeName = GptTypes.Name(typeGuid),
                UniqueGuid = uniqueGuid,
                FirstLba = first,
                LastLba = last,
                Name = name,
            });
        }

        return new GptDisk { DiskGuid = diskGuid, Partitions = partitions };
    }

    private static string ReadUtf16Name(byte[] data, int at, int byteLen)
    {
        string s = Encoding.Unicode.GetString(data, at, byteLen);
        int nul = s.IndexOf('\0');
        if (nul >= 0) s = s.Substring(0, nul);
        return s;
    }

    private static bool IsSignature(byte[] data, int at) =>
        at + 8 <= data.Length &&
        data[at] == (byte)'E' && data[at + 1] == (byte)'F' && data[at + 2] == (byte)'I' &&
        data[at + 3] == (byte)' ' && data[at + 4] == (byte)'P' && data[at + 5] == (byte)'A' &&
        data[at + 6] == (byte)'R' && data[at + 7] == (byte)'T';

    private static bool IsAllZero(byte[] data, int at, int len)
    {
        for (int i = 0; i < len; i++) if (data[at + i] != 0) return false;
        return true;
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

/// <summary>Names for the well-known GPT partition-type GUIDs.</summary>
public static class GptTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C12A7328-F81F-11D2-BA4B-00A0C93EC93B"] = "EFI System",
        ["EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"] = "Microsoft Basic Data",
        ["E3C9E316-0B5C-4DB8-817D-F92DF00215AE"] = "Microsoft Reserved",
        ["DE94BBA4-06D1-4D40-A16A-BFD50179D6AC"] = "Windows Recovery",
        ["0FC63DAF-8483-4772-8E79-3D69D8477DE4"] = "Linux filesystem",
        ["0657FD6D-A4AB-43C4-84E5-0933C84B4F4F"] = "Linux swap",
        ["E6D6D379-F507-44C2-A23C-238F2A3DF928"] = "Linux LVM",
        ["A19D880F-05FC-4D3B-A006-743F0F84911E"] = "Linux RAID",
        ["933AC7E1-2EB4-4F13-B844-0E14E2AEF915"] = "Linux /home",
        ["21686148-6449-6E6F-744E-656564454649"] = "BIOS boot",
        ["48465300-0000-11AA-AA11-00306543ECAC"] = "Apple HFS+",
        ["7C3457EF-0000-11AA-AA11-00306543ECAC"] = "Apple APFS",
        ["55465300-0000-11AA-AA11-00306543ECAC"] = "Apple UFS",
        ["6A898CC3-1DD2-11B2-99A6-080020736631"] = "Solaris /usr / Apple ZFS",
        ["516E7CB4-6ECF-11D6-8FF8-00022D09712B"] = "FreeBSD data",
    };

    public static string Name(string typeGuid) =>
        Map.TryGetValue(typeGuid, out var name) ? name : "Unknown";
}
