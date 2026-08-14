// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.GameCube;

/// <summary>The role a Wii partition plays, from its partition-table type word.</summary>
public enum WiiPartitionType
{
    Data = 0,
    Update = 1,
    Channel = 2,
    /// <summary>Any other value — a title-specific type. See <see cref="WiiPartition.RawType"/>.</summary>
    TitleSpecific = -1,
}

/// <summary>
/// One entry from the Wii partition table. Only its position and declared type are read.
/// The partition's <em>contents</em> are AES-encrypted and are intentionally never touched
/// by DiscForge — see <see cref="WiiDisc"/>.
/// </summary>
public sealed record WiiPartition
{
    public required WiiPartitionType Type { get; init; }
    /// <summary>The raw 32-bit type value as stored, preserved even for unknown types.</summary>
    public required uint RawType { get; init; }
    /// <summary>Absolute byte offset of the partition on the disc (the stored value shifted left by 2).</summary>
    public required long Offset { get; init; }
}

/// <summary>
/// The unencrypted structure of a Wii disc: its volume header and partition table.
/// </summary>
public sealed record WiiVolume
{
    /// <summary>Four-character game code from the volume header (offset 0x00).</summary>
    public required string GameCode { get; init; }
    /// <summary>The internal game title (offset 0x20, NUL-terminated).</summary>
    public required string GameName { get; init; }
    /// <summary>The partitions declared in the partition table, across all groups.</summary>
    public required IReadOnlyList<WiiPartition> Partitions { get; init; }
}

/// <summary>
/// The plaintext, signed-but-unencrypted metadata of a single Wii partition: the fields
/// read from its ticket and TMD, plus the declared position/size of the (encrypted) data
/// region. Everything here is descriptive metadata carried in the clear.
///
/// IMPORTANT — protection boundary: this record deliberately carries NO decryption key.
/// The encrypted title key (ticket offset 0x1BF) is never read; no key is derived; the
/// data region (see <see cref="DataOffset"/>/<see cref="DataSize"/>) is only ever
/// described, never read. DiscForge never seeks into it.
/// </summary>
public sealed record WiiPartitionDetails
{
    /// <summary>The partition's declared role (mirrors <see cref="WiiPartition.Type"/>).</summary>
    public required WiiPartitionType Type { get; init; }
    /// <summary>Absolute byte offset of the partition on the disc.</summary>
    public required long Offset { get; init; }
    /// <summary>Ticket title id (8 bytes at ticket offset 0x1DC) as an uppercase hex string.</summary>
    public required string TitleId { get; init; }
    /// <summary>The four-character game id (last 4 bytes of the title id) when they are
    /// printable ASCII (e.g. "RMCE"); otherwise <c>null</c>.</summary>
    public required string? GameId { get; init; }
    /// <summary>Title version from the TMD (u16 at TMD offset 0x1DC).</summary>
    public required int TitleVersion { get; init; }
    /// <summary>Number of contents declared in the TMD (u16 at TMD offset 0x1DE).</summary>
    public required int ContentCount { get; init; }
    /// <summary>Absolute byte offset of the (encrypted) data region. DESCRIBED ONLY —
    /// DiscForge never reads here.</summary>
    public required long DataOffset { get; init; }
    /// <summary>Declared size in bytes of the (encrypted) data region.</summary>
    public required long DataSize { get; init; }
    /// <summary>Absolute byte offset of the TMD within the partition.</summary>
    public required long TmdOffset { get; init; }
}

/// <summary>
/// Reads the UNENCRYPTED structure of a Wii disc only. Everything here is BIG-ENDIAN.
///
/// Volume header:
///   0x00  4   game code
///   0x18  4   magic word 0x5D1C9EA3 (validates a Wii disc)
///   0x20  ..  game name (NUL-terminated)
///
/// Partition table (at 0x40000): four partition groups (max), each 8 bytes —
///   u32 partition count, u32 (table offset >> 2). For each group, at that table
///   offset sits an array of 8-byte partition entries — u32 (partition offset >> 2),
///   u32 type (0 = DATA, 1 = UPDATE, 2 = CHANNEL, other = title-specific).
///
/// IMPORTANT — protection boundary: a Wii game partition's contents are AES-encrypted
/// under a title key protected by the console's common key. DiscForge reads ONLY the
/// plaintext structure: the partition <em>table</em> (offsets and types) and each
/// partition's signed-but-unencrypted ticket + TMD metadata (title id, title version,
/// content count, and the offset/size of the data region — see
/// <see cref="ReadPartitionDetails"/>). It does NOT read the encrypted title key,
/// decrypt partition data, derive or use title keys, or read the encrypted partition
/// FST. Going inside an encrypted partition would defeat console security and is
/// deliberately out of scope; this reader never seeks to a partition's data offset.
///
/// Clean-room from the public Wii disc-layout description; validated by a synthetic
/// volume header + partition table (no encrypted content is present or required).
/// </summary>
public static class WiiDisc
{
    /// <summary>The magic word at 0x18 that every Wii disc carries.</summary>
    public const uint Magic = 0x5D1C9EA3;

    /// <summary>Where the partition table lives on the disc.</summary>
    public const long PartitionTableOffset = 0x40000;

    private const int MaxGroups = 4;

    /// <summary>True if the stream begins with a valid Wii volume header (magic at 0x18).
    /// Never throws; leaves the position at 0.</summary>
    public static bool IsWii(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length < 0x1C) return false;
        try
        {
            stream.Seek(0x18, SeekOrigin.Begin);
            Span<byte> m = stackalloc byte[4];
            stream.ReadExactly(m);
            stream.Seek(0, SeekOrigin.Begin);
            return BinaryPrimitives.ReadUInt32BigEndian(m) == Magic;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Parse the volume header and partition table. Does NOT read partition contents.</summary>
    public static WiiVolume Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new GameCubeFormatException("Reading a Wii image needs a seekable stream.");
        if (stream.Length < 0x100)
            throw new GameCubeFormatException(
                $"Too small for a Wii volume header: have {stream.Length} bytes.");

        var header = new byte[0x100];
        stream.Seek(0, SeekOrigin.Begin);
        ReadExact(stream, header, "volume header");

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18, 4));
        if (magic != Magic)
            throw new GameCubeFormatException(
                $"Not a Wii disc: magic at 0x18 was 0x{magic:X8}, expected 0x{Magic:X8}.");

        string gameCode = Ascii(header.AsSpan(0x00, 4));
        string gameName = Ascii(header.AsSpan(0x20, 0x60));

        var partitions = ReadPartitionTable(stream);

        return new WiiVolume
        {
            GameCode = gameCode,
            GameName = gameName,
            Partitions = partitions,
        };
    }

    private static List<WiiPartition> ReadPartitionTable(Stream stream)
    {
        var partitions = new List<WiiPartition>();
        if (PartitionTableOffset + MaxGroups * 8 > stream.Length)
            throw new GameCubeFormatException(
                "Image ends before the Wii partition table at 0x40000.");

        var groupBytes = new byte[MaxGroups * 8];
        stream.Seek(PartitionTableOffset, SeekOrigin.Begin);
        ReadExact(stream, groupBytes, "partition group table");

        for (int g = 0; g < MaxGroups; g++)
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(groupBytes.AsSpan(g * 8, 4));
            long tableOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(groupBytes.AsSpan(g * 8 + 4, 4)) << 2;
            if (count == 0) continue;
            if (count > 0x10000)
                throw new GameCubeFormatException($"Implausible partition count {count} in group {g}.");
            if (tableOffset < 0 || tableOffset + count * 8 > stream.Length)
                throw new GameCubeFormatException(
                    $"Partition group {g} table runs past the end of the image.");

            var entryBytes = new byte[count * 8];
            stream.Seek(tableOffset, SeekOrigin.Begin);
            ReadExact(stream, entryBytes, $"partition group {g}");

            for (int p = 0; p < count; p++)
            {
                long offset = (long)BinaryPrimitives.ReadUInt32BigEndian(entryBytes.AsSpan(p * 8, 4)) << 2;
                uint rawType = BinaryPrimitives.ReadUInt32BigEndian(entryBytes.AsSpan(p * 8 + 4, 4));
                // NOTE: we deliberately do NOT seek to `offset` — partition contents are encrypted.
                partitions.Add(new WiiPartition
                {
                    Type = rawType switch
                    {
                        0 => WiiPartitionType.Data,
                        1 => WiiPartitionType.Update,
                        2 => WiiPartitionType.Channel,
                        _ => WiiPartitionType.TitleSpecific,
                    },
                    RawType = rawType,
                    Offset = offset,
                });
            }
        }

        return partitions;
    }

    // ---- partition-aware layer: plaintext ticket + TMD metadata --------------
    //
    // A partition begins with its ticket, then a small partition header, then the
    // TMD, cert chain, H3 hash table, and finally the AES-ENCRYPTED data region.
    // The ticket and TMD are signed but NOT encrypted, so their descriptive fields
    // (title id, title version, content count, and the offsets/sizes below) are
    // plaintext and may be read. We NEVER read the ticket's encrypted title key
    // (offset 0x1BF), derive any key, or seek into the data region.
    //
    // Ticket (0x2A4 bytes from the partition start):
    //   0x1DC  8   title id                         (read — plaintext)
    // Partition header (immediately after the ticket):
    //   0x2A4  u32 TMD size
    //   0x2A8  u32 TMD offset       (<<2, from partition start)
    //   0x2AC  u32 cert chain size
    //   0x2B0  u32 cert chain offset(<<2)
    //   0x2B4  u32 H3 table offset  (<<2)
    //   0x2B8  u32 data offset      (<<2, from partition start) — DESCRIBED, never read
    //   0x2BC  u32 data size        (<<2)                        — DESCRIBED, never read
    // TMD (at partition start + TMD offset), signed blob:
    //   0x18C  8   title id
    //   0x1DC  u16 title version
    //   0x1DE  u16 number of contents

    private const int TicketSize = 0x2A4;
    private const int PartitionHeaderEnd = 0x2C0;   // end of the data-size field
    private const int TicketTitleIdOffset = 0x1DC;
    private const int TmdTitleVersionOffset = 0x1DC;
    private const int TmdContentCountOffset = 0x1DE;
    private const int TmdReadLength = 0x1E0;         // covers up to content count (0x1DE + 2)

    /// <summary>
    /// Read the plaintext ticket + TMD metadata for one partition. Seeks only within the
    /// ticket, partition header, and TMD regions — NEVER into the (encrypted) data region,
    /// and never reads the encrypted title key. Throws <see cref="GameCubeFormatException"/>
    /// if the partition is truncated or its offsets fall outside the image.
    /// </summary>
    public static WiiPartitionDetails ReadPartitionDetails(Stream image, WiiPartition partition)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(partition);
        if (!image.CanSeek)
            throw new GameCubeFormatException("Reading Wii partition details needs a seekable stream.");

        long baseOffset = partition.Offset;

        // Ticket + partition header in one read (title id, TMD/data offsets & sizes).
        var head = new byte[PartitionHeaderEnd];
        ReadRegion(image, baseOffset, head, "partition ticket/header");

        var titleIdBytes = head.AsSpan(TicketTitleIdOffset, 8);
        string titleId = System.Convert.ToHexString(titleIdBytes);
        string? gameId = TryGameId(titleIdBytes[^4..]);

        long tmdSize = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x2A4, 4));
        long tmdOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x2A8, 4)) << 2;
        long dataOffsetRel = (long)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x2B8, 4)) << 2;
        long dataSize = (long)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x2BC, 4)) << 2;

        // The TMD sits after the header but before the data region — read only that blob.
        long tmdAbs = baseOffset + tmdOffset;
        var tmd = new byte[TmdReadLength];
        ReadRegion(image, tmdAbs, tmd, "partition TMD");

        int titleVersion = BinaryPrimitives.ReadUInt16BigEndian(tmd.AsSpan(TmdTitleVersionOffset, 2));
        int contentCount = BinaryPrimitives.ReadUInt16BigEndian(tmd.AsSpan(TmdContentCountOffset, 2));

        return new WiiPartitionDetails
        {
            Type = partition.Type,
            Offset = baseOffset,
            TitleId = titleId,
            GameId = gameId,
            TitleVersion = titleVersion,
            ContentCount = contentCount,
            // Data region is described only; we never seek here.
            DataOffset = baseOffset + dataOffsetRel,
            DataSize = dataSize,
            TmdOffset = tmdAbs,
        };
    }

    /// <summary>
    /// Read the volume plus the plaintext details for every partition. Partitions whose
    /// details cannot be read (truncated/out-of-range) are still described structurally by
    /// <paramref name="volume"/>; this returns details only for those that parse.
    /// </summary>
    public static IReadOnlyList<WiiPartitionDetails> ReadAllDetails(Stream image, WiiVolume volume)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(volume);
        var list = new List<WiiPartitionDetails>(volume.Partitions.Count);
        foreach (var p in volume.Partitions)
            list.Add(ReadPartitionDetails(image, p));
        return list;
    }

    /// <summary>The last 4 title-id bytes as ASCII when all are printable (0x20–0x7E), else null.</summary>
    private static string? TryGameId(ReadOnlySpan<byte> four)
    {
        foreach (byte b in four)
            if (b < 0x20 || b > 0x7E) return null;
        return Encoding.ASCII.GetString(four);
    }

    /// <summary>Read <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>,
    /// with an explicit bounds check so an out-of-range partition throws our format
    /// exception rather than seeking past the end of the image.</summary>
    private static void ReadRegion(Stream stream, long offset, byte[] buffer, string what)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
            throw new GameCubeFormatException(
                $"Wii {what} at 0x{offset:X} runs past the end of the image (length {stream.Length}).");
        stream.Seek(offset, SeekOrigin.Begin);
        ReadExact(stream, buffer, what);
    }

    private static void ReadExact(Stream stream, byte[] buffer, string what)
    {
        try { stream.ReadExactly(buffer, 0, buffer.Length); }
        catch (EndOfStreamException)
        {
            throw new GameCubeFormatException($"Unexpected end of image while reading the {what}.");
        }
    }

    private static string Ascii(ReadOnlySpan<byte> field)
    {
        int len = field.IndexOf((byte)0);
        if (len < 0) len = field.Length;
        return Encoding.ASCII.GetString(field[..len]).TrimEnd('\0', ' ');
    }
}
