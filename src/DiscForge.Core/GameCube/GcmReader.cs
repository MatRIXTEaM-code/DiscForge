// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.GameCube;

/// <summary>One node in a GameCube disc's filesystem — a file or a directory.</summary>
public sealed record GcmEntry
{
    /// <summary>The node's own name (no path), e.g. "opening.bnr".</summary>
    public required string Name { get; init; }
    /// <summary>Full path from the disc root with '/' separators, e.g. "/audio/music.adp".</summary>
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    /// <summary>File length in bytes (0 for directories).</summary>
    public required long Size { get; init; }
    /// <summary>Absolute byte offset of the file's data within the image (0 for directories).</summary>
    public required long Offset { get; init; }
}

/// <summary>
/// A GameCube disc image (.gcm / .iso), read from its unencrypted on-disc structures.
/// GameCube GCM data is entirely unencrypted, so reading its boot header and walking
/// its FST filesystem is clean-room-safe and touches no copy protection.
/// </summary>
public sealed record GameCubeDisc
{
    /// <summary>Four-character game code from the boot header (offset 0x00).</summary>
    public required string GameCode { get; init; }
    /// <summary>Two-character maker code (offset 0x04).</summary>
    public required string MakerCode { get; init; }
    /// <summary>The internal game title (offset 0x20, NUL-terminated).</summary>
    public required string GameName { get; init; }
    /// <summary>Disc number (offset 0x06), 0 for the first disc.</summary>
    public required int DiscId { get; init; }
    /// <summary>Disc version (offset 0x07).</summary>
    public required int Version { get; init; }
    /// <summary>Offset of the DOL main executable (boot header 0x420).</summary>
    public long DolOffset { get; init; }
    /// <summary>Every file and directory in the disc filesystem, in FST order.</summary>
    public required IReadOnlyList<GcmEntry> Entries { get; init; }
}

/// <summary>
/// Reads GameCube disc images. Everything on a GameCube disc is BIG-ENDIAN.
///
/// Boot header (boot.bin, first 0x440 bytes):
///   0x00  4   game code
///   0x04  2   maker code
///   0x06  1   disc id (disc number)
///   0x07  1   version
///   0x1C  4   magic word 0xC2339F3D (validates a GameCube disc)
///   0x20  ..  game name (NUL-terminated, up to 0x3E0 bytes)
///   0x420 4   DOL (main executable) offset
///   0x424 4   FST offset
///   0x428 4   FST size
///
/// FST: an array of 12-byte entries followed by a string table.
///   byte 0     flag: 0 = file, 1 = directory
///   bytes 1..3 24-bit offset of this entry's name in the string table
///   bytes 4..7 file: data offset; directory: parent entry index
///   bytes 8..11 file: length; directory: index one past its last child ("next index")
///   Entry 0 is the root; its "length" field is the total number of entries.
///
/// Clean-room from the public GameCube disc-layout description; validated by a
/// synthetic-image round trip (build FST + files -> read tree -> extract bytes).
/// </summary>
public static class GcmReader
{
    /// <summary>The magic word at 0x1C that every GameCube disc carries.</summary>
    public const uint Magic = 0xC2339F3D;

    private const int HeaderSize = 0x440;
    private const int FstEntrySize = 12;

    /// <summary>True if the stream begins with a valid GameCube boot header (magic at 0x1C).
    /// Never throws — a short or unreadable stream simply returns false. Leaves position at 0.</summary>
    public static bool IsGcm(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length < 0x20) return false;
        try
        {
            stream.Seek(0x1C, SeekOrigin.Begin);
            Span<byte> m = stackalloc byte[4];
            stream.ReadExactly(m);
            stream.Seek(0, SeekOrigin.Begin);
            return BinaryPrimitives.ReadUInt32BigEndian(m) == Magic;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Parse a GameCube disc image: the boot header and the full FST tree.</summary>
    public static GameCubeDisc Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new GameCubeFormatException("Reading a GameCube image needs a seekable stream.");
        if (stream.Length < HeaderSize)
            throw new GameCubeFormatException(
                $"Too small for a GameCube disc header: need {HeaderSize} bytes, have {stream.Length}.");

        var header = new byte[HeaderSize];
        stream.Seek(0, SeekOrigin.Begin);
        ReadExact(stream, header, "boot header");

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x1C, 4));
        if (magic != Magic)
            throw new GameCubeFormatException(
                $"Not a GameCube disc: magic at 0x1C was 0x{magic:X8}, expected 0x{Magic:X8}.");

        string gameCode = Ascii(header.AsSpan(0x00, 4));
        string makerCode = Ascii(header.AsSpan(0x04, 2));
        int discId = header[0x06];
        int version = header[0x07];
        string gameName = Ascii(header.AsSpan(0x20, 0x3E0));

        long dolOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x420, 4));
        long fstOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x424, 4));
        long fstSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x428, 4));

        var entries = ReadFst(stream, fstOffset, fstSize);

        return new GameCubeDisc
        {
            GameCode = gameCode,
            MakerCode = makerCode,
            GameName = gameName,
            DiscId = discId,
            Version = version,
            DolOffset = dolOffset,
            Entries = entries,
        };
    }

    /// <summary>Write a file entry's exact bytes to <paramref name="output"/>.</summary>
    public static void ExtractFile(Stream stream, GcmEntry entry, Stream output)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        if (entry.IsDirectory)
            throw new GameCubeFormatException($"'{entry.Path}' is a directory, not a file.");
        if (entry.Offset < 0 || entry.Offset + entry.Size > stream.Length)
            throw new GameCubeFormatException(
                $"File '{entry.Path}' runs past the end of the image (offset {entry.Offset}, size {entry.Size}).");

        stream.Seek(entry.Offset, SeekOrigin.Begin);
        long remaining = entry.Size;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int got = stream.Read(buffer, 0, want);
            if (got <= 0)
                throw new GameCubeFormatException($"Unexpected end of image while extracting '{entry.Path}'.");
            output.Write(buffer, 0, got);
            remaining -= got;
        }
    }

    private static List<GcmEntry> ReadFst(Stream stream, long fstOffset, long fstSize)
    {
        if (fstOffset < 0 || fstSize < FstEntrySize || fstOffset + fstSize > stream.Length)
            throw new GameCubeFormatException(
                $"FST location is invalid (offset {fstOffset}, size {fstSize}, image {stream.Length}).");
        if (fstSize > int.MaxValue)
            throw new GameCubeFormatException("FST is implausibly large.");

        var fst = new byte[(int)fstSize];
        stream.Seek(fstOffset, SeekOrigin.Begin);
        ReadExact(stream, fst, "FST");

        // Entry 0 is the root; its "length" field holds the total entry count.
        long count = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(8, 4));
        if (count < 1 || count * FstEntrySize > fstSize)
            throw new GameCubeFormatException(
                $"FST entry count {count} does not fit in {fstSize} bytes.");

        int entryCount = (int)count;
        int stringTableStart = entryCount * FstEntrySize;

        var entries = new List<GcmEntry>(entryCount - 1);
        // Directory scope stack: (path prefix, index one past this dir's last child).
        var dirStack = new Stack<(string Path, long End)>();
        dirStack.Push(("", count)); // root spans the whole table

        for (int i = 1; i < entryCount; i++)
        {
            while (dirStack.Count > 1 && dirStack.Peek().End <= i)
                dirStack.Pop();

            int at = i * FstEntrySize;
            byte flag = fst[at];
            int nameOffset = (fst[at + 1] << 16) | (fst[at + 2] << 8) | fst[at + 3];
            uint field2 = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(at + 4, 4));
            uint field3 = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(at + 8, 4));

            string name = ReadName(fst, stringTableStart, nameOffset);
            string parentPath = dirStack.Peek().Path;
            string fullPath = parentPath + "/" + name;

            bool isDir = flag == 1;
            if (isDir)
            {
                entries.Add(new GcmEntry
                {
                    Name = name, Path = fullPath, IsDirectory = true, Size = 0, Offset = 0,
                });
                // field3 is the "next index": one past this directory's last child.
                dirStack.Push((fullPath, field3));
            }
            else
            {
                entries.Add(new GcmEntry
                {
                    Name = name, Path = fullPath, IsDirectory = false,
                    Size = field3, Offset = field2,
                });
            }
        }

        return entries;
    }

    private static string ReadName(byte[] fst, int stringTableStart, int nameOffset)
    {
        int start = stringTableStart + nameOffset;
        if (start < stringTableStart || start >= fst.Length)
            throw new GameCubeFormatException($"FST name offset {nameOffset} points outside the string table.");
        int end = start;
        while (end < fst.Length && fst[end] != 0) end++;
        return Encoding.ASCII.GetString(fst, start, end - start);
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
