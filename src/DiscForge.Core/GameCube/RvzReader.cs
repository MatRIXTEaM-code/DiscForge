// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.GameCube;

/// <summary>Which container flavour a file is — WIA or its RVZ superset.</summary>
public enum RvzFormat { Wia, Rvz }

/// <summary>The compression algorithm a WIA/RVZ container declares for its data groups.</summary>
public enum RvzCompression
{
    None = 0,
    Purge = 1,
    Bzip2 = 2,
    Lzma = 3,
    Lzma2 = 4,
    /// <summary>zstd — the RVZ addition over base WIA.</summary>
    Zstd = 5,
}

/// <summary>Identification and metadata for a WIA/RVZ container (no group decompression).</summary>
public sealed record RvzInfo
{
    public required RvzFormat Format { get; init; }
    public required uint Version { get; init; }
    public required RvzCompression Compression { get; init; }
    /// <summary>Compression level as stored (algorithm-specific; may be negative for zstd).</summary>
    public required int CompressionLevel { get; init; }
    /// <summary>The data-group ("chunk") size in bytes.</summary>
    public required uint ChunkSize { get; init; }
    /// <summary>The size of the original disc image the container reconstructs to.</summary>
    public required ulong IsoSize { get; init; }
    /// <summary>The disc's game id, taken from the unencrypted 0x80-byte disc header.</summary>
    public required string GameId { get; init; }
    /// <summary>The disc's internal game title, from the unencrypted disc header.</summary>
    public required string GameName { get; init; }

    /// <summary>True when the file was long enough to also read the disc-structure
    /// directory (partition/raw-data/group counts) beyond the identification header.</summary>
    public bool HasStructure { get; init; }
    /// <summary>Number of Wii partition entries (0 for a GameCube disc).</summary>
    public uint PartitionCount { get; init; }
    /// <summary>Number of unencrypted "raw data" regions (whole GameCube disc, or the
    /// Wii disc's non-partition metadata).</summary>
    public uint RawDataCount { get; init; }
    /// <summary>Number of compressed data groups ("chunks") the body is split into.</summary>
    public uint GroupCount { get; init; }
    /// <summary>Length of the codec's per-file parameter blob (LZMA/LZMA2 props); 0 for none/zstd.</summary>
    public int CompressorDataLength { get; init; }
}

/// <summary>
/// Parses the header of a WIA/RVZ container for IDENTIFICATION and METADATA only.
/// RVZ shares WIA's structure and adds zstd compression; both are BIG-ENDIAN.
///
/// wia_file_head (0x48 bytes):
///   0x00  4    magic  "WIA\x01" or "RVZ\x01"
///   0x04  4    version
///   0x08  4    version_compatible
///   0x0C  4    size of the disc-structure header
///   0x10  20   SHA-1 of the disc-structure header
///   0x24  8    original ISO/disc file size
///   0x2C  8    WIA/RVZ file size
///   0x34  20   SHA-1 of bytes 0x00..0x34
///
/// disc structure (immediately after, at 0x48):
///   0x00  4    disc type (0 none, 1 GameCube, 2 Wii)
///   0x04  4    compression type (0 none, 1 purge, 2 bzip2, 3 lzma, 4 lzma2, 5 zstd)
///   0x08  4    compression level
///   0x0C  4    chunk size
///   0x10  0x80 the first 0x80 bytes of the disc — UNENCRYPTED, so the game id/name
///              are readable here without decrypting anything.
///
/// Full RVZ -> ISO decompression is not yet wired: the zstd blocker is now solved (see
/// <see cref="DiscForge.Core.Compression.ZstdDecoder"/>, a clean-room decoder validated against
/// 120+ reference streams), so decompressing the data groups is available; what remains is the
/// container walk (raw-data + group tables, WIA exception lists) and — for Wii discs — the
/// hash-tree rebuild + AES re-encryption + LFSR junk regeneration, which fail silently when wrong
/// and need a reference RVZ+ISO pair to validate. <see cref="Decode"/> throws until that lands.
///
/// Clean-room from the public WIA/RVZ container description; validated by a hand-built
/// minimal header fixture.
/// </summary>
public static class RvzReader
{
    private const int FileHeadSize = 0x48;
    private const int DiscHeadOffset = FileHeadSize + 0x10; // 0x58
    private const int DiscHeadSize = 0x80;
    private const int MinBytes = DiscHeadOffset + DiscHeadSize; // 0xD8
    // The wia_disc directory (counts + table offsets) follows the 0x80-byte disc
    // header. It is stored UNCOMPRESSED (it names where the compressed tables live),
    // so these fields are readable without any codec. Offsets are within wia_disc.
    private const int DiscStructSize = 0xDC;
    private const int FullBytes = FileHeadSize + DiscStructSize; // 0x124

    /// <summary>True if the stream starts with the "WIA\x01" or "RVZ\x01" magic. Never throws.</summary>
    public static bool IsRvzOrWia(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length < 4) return false;
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            Span<byte> m = stackalloc byte[4];
            stream.ReadExactly(m);
            stream.Seek(0, SeekOrigin.Begin);
            return DetectFormat(m) is not null;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Read the container header and surface its identification/metadata.</summary>
    public static RvzInfo ReadInfo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new GameCubeFormatException("Reading a WIA/RVZ header needs a seekable stream.");
        if (stream.Length < MinBytes)
            throw new GameCubeFormatException(
                $"Too small for a WIA/RVZ header: need {MinBytes} bytes, have {stream.Length}.");

        // Read the identification header, plus the disc-structure directory when the
        // file is long enough for it (older/minimal fixtures may stop at MinBytes).
        int want = (int)Math.Min(stream.Length, FullBytes);
        var buffer = new byte[want];
        stream.Seek(0, SeekOrigin.Begin);
        try { stream.ReadExactly(buffer, 0, buffer.Length); }
        catch (EndOfStreamException)
        {
            throw new GameCubeFormatException("Unexpected end of file while reading the WIA/RVZ header.");
        }

        RvzFormat? format = DetectFormat(buffer.AsSpan(0, 4));
        if (format is null)
            throw new GameCubeFormatException("Not a WIA/RVZ file: missing the \"WIA\\x01\"/\"RVZ\\x01\" magic.");

        uint version = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0x04, 4));
        ulong isoSize = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(0x24, 8));

        uint compRaw = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(FileHeadSize + 0x04, 4));
        int compLevel = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(FileHeadSize + 0x08, 4));
        uint chunkSize = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(FileHeadSize + 0x0C, 4));

        var discHead = buffer.AsSpan(DiscHeadOffset, DiscHeadSize);
        string gameId = Ascii(discHead.Slice(0x00, 6));
        string gameName = Ascii(discHead.Slice(0x20, DiscHeadSize - 0x20));

        // The wia_disc directory, relative to the disc struct at 0x48:
        //   0x90 u32 partition count   0xB4 u32 raw-data count
        //   0xC4 u32 group count       0xD4 u8  compressor-data length
        bool hasStruct = buffer.Length >= FullBytes;
        uint partitions = 0, rawData = 0, groups = 0;
        int comprLen = 0;
        if (hasStruct)
        {
            int ds = FileHeadSize;
            partitions = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(ds + 0x90, 4));
            rawData = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(ds + 0xB4, 4));
            groups = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(ds + 0xC4, 4));
            comprLen = buffer[ds + 0xD4];
        }

        return new RvzInfo
        {
            Format = format.Value,
            Version = version,
            Compression = compRaw <= (uint)RvzCompression.Zstd
                ? (RvzCompression)compRaw
                : throw new GameCubeFormatException($"Unknown WIA/RVZ compression type {compRaw}."),
            CompressionLevel = compLevel,
            ChunkSize = chunkSize,
            IsoSize = isoSize,
            GameId = gameId,
            GameName = gameName,
            HasStructure = hasStruct,
            PartitionCount = partitions,
            RawDataCount = rawData,
            GroupCount = groups,
            CompressorDataLength = comprLen,
        };
    }

    /// <summary>
    /// Not yet wired: the zstd group codec is now available (<see cref="DiscForge.Core.Compression.ZstdDecoder"/>),
    /// but reconstructing the ISO still needs the container walk (raw-data/group tables + WIA
    /// exception lists) and, for Wii discs, hash-tree rebuild + AES re-encryption. Those fail
    /// silently when wrong, so they must be validated against a real RVZ+ISO pair before shipping.
    /// Always throws until then.
    /// </summary>
    public static void Decode(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        // Delegates to the GameCube reconstructor (zstd/none groups). Wii, other codecs and the
        // bit-exact junk (Nintendo LFG) throw with a clear message — see RvzDecoder's docs.
        _ = RvzDecoder.Decode(ms.ToArray(), output);
    }

    private static RvzFormat? DetectFormat(ReadOnlySpan<byte> magic)
    {
        if (magic.Length < 4 || magic[3] != 0x01) return null;
        if (magic[0] == (byte)'W' && magic[1] == (byte)'I' && magic[2] == (byte)'A') return RvzFormat.Wia;
        if (magic[0] == (byte)'R' && magic[1] == (byte)'V' && magic[2] == (byte)'Z') return RvzFormat.Rvz;
        return null;
    }

    private static string Ascii(ReadOnlySpan<byte> field)
    {
        int len = field.IndexOf((byte)0);
        if (len < 0) len = field.Length;
        return Encoding.ASCII.GetString(field[..len]).TrimEnd('\0', ' ');
    }
}
