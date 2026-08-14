// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.GameAudio;

/// <summary>
/// A parsed PSF-family file: the version/system, the CRC-32 of the (still
/// compressed, never decoded) program block, and the <c>[TAG]</c> key/value
/// metadata. Only structure and tags are read — the zlib program payload is
/// located but deliberately left compressed and un-executed.
/// </summary>
public sealed class PsfFile
{
    /// <summary>The version byte at offset 0x03 — identifies the emulated system.</summary>
    public required byte PsfVersion { get; init; }

    /// <summary>Human-readable system for <see cref="PsfVersion"/> (e.g. "PlayStation").</summary>
    public required string SystemName { get; init; }

    /// <summary>CRC-32 of the compressed program block (header field at 0x0C).</summary>
    public required uint ProgramCrc32 { get; init; }

    /// <summary>Compressed size of the program block (header field at 0x08).</summary>
    public required uint CompressedProgramSize { get; init; }

    /// <summary>
    /// True when a non-empty zlib program block is present. DiscForge does NOT
    /// decompress or execute it — it is metadata-only preservation.
    /// </summary>
    public bool HasProgram => CompressedProgramSize > 0;

    /// <summary>Case-insensitive <c>key=value</c> tags from the <c>[TAG]</c> block.</summary>
    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    public string? Title => Get("title");
    public string? Game => Get("game");
    public string? Artist => Get("artist");
    public string? Length => Get("length");

    private string? Get(string key) => Tags.TryGetValue(key, out var v) ? v : null;
}

/// <summary>
/// Reads PSF-family sound files (PSF/PSF2, SSF, DSF, USF, GSF, SNSF, QSF, …).
/// Layout: magic "PSF" + 1 version byte, then three little-endian u32s —
/// reserved_size (0x04), compressed program_size (0x08), program_crc32 (0x0C).
/// The reserved area and the zlib program follow; a "[TAG]" block of UTF-8
/// <c>key=value</c> lines may sit at 16 + reserved_size + program_size.
/// </summary>
public static class PsfReader
{
    private const int HeaderSize = 16;

    public static bool IsPsf(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Length >= 4 && data[0] == 'P' && data[1] == 'S' && data[2] == 'F'
               && SystemFor(data[3]) is not null;
    }

    public static bool IsPsf(Stream stream) => IsPsf(ReadHead(stream, 4));

    public static PsfFile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Read(ReadAll(stream));
    }

    public static PsfFile Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize)
            throw new GameAudioFormatException($"PSF file is only {data.Length} bytes — too short for a 16-byte header.");
        if (data[0] != 'P' || data[1] != 'S' || data[2] != 'F')
            throw new GameAudioFormatException("Not a PSF file — missing the \"PSF\" magic at offset 0.");

        byte version = data[3];
        string system = SystemFor(version) ?? $"unknown PSF system (version 0x{version:X2})";

        uint reservedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x04, 4));
        uint programSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x08, 4));
        uint programCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x0C, 4));

        // Tag area sits just past the reserved area and the compressed program.
        // Use a checked long so oversized (corrupt) sizes can't wrap the offset.
        long tagOffset = (long)HeaderSize + reservedSize + programSize;
        var tags = ParseTags(data, tagOffset);

        return new PsfFile
        {
            PsfVersion = version,
            SystemName = system,
            ProgramCrc32 = programCrc32,
            CompressedProgramSize = programSize,
            Tags = tags,
        };
    }

    /// <summary>Map a PSF version byte to the system it represents.</summary>
    public static string? SystemFor(byte version) => version switch
    {
        0x01 => "PlayStation",          // PSF1
        0x02 => "PlayStation 2",        // PSF2
        0x11 => "Sega Saturn",          // SSF
        0x12 => "Sega Dreamcast",       // DSF
        0x21 => "Nintendo 64",          // USF
        0x22 => "Game Boy Advance",     // GSF
        0x23 => "Super Nintendo",       // SNSF
        0x24 => "Capcom QSound",        // QSF
        0x41 => "Audio Portable Sound Format",  // APSF
        _ => null,
    };

    private static IReadOnlyDictionary<string, string> ParseTags(byte[] data, long tagOffset)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tagOffset < 0 || tagOffset + 5 > data.Length) return tags;

        // The block must open with the literal "[TAG]" marker.
        int at = (int)tagOffset;
        if (data[at] != '[' || data[at + 1] != 'T' || data[at + 2] != 'A' ||
            data[at + 3] != 'G' || data[at + 4] != ']')
            return tags;

        int start = at + 5;
        string body = Encoding.UTF8.GetString(data, start, data.Length - start);
        foreach (var rawLine in body.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            // Per the PSF tag convention whitespace around key and value is ignored.
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (key.Length == 0) continue;

            // A repeated key concatenates its values with a newline (multi-line comments).
            tags[key] = tags.TryGetValue(key, out var prev) ? prev + "\n" + value : value;
        }
        return tags;
    }

    private static byte[] ReadHead(Stream stream, int count)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buf = new byte[count];
        if (stream.CanSeek) stream.Seek(0, SeekOrigin.Begin);
        int n = stream.Read(buf, 0, count);
        return n == count ? buf : buf[..n];
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream ms) return ms.ToArray();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
