// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Saves;

public sealed class SaturnSaveFormatException(string message) : Exception(message);

/// <summary>One save in a Sega Saturn backup memory (directory metadata).</summary>
public sealed record SaturnSave
{
    /// <summary>Save file name (up to 11 ASCII characters).</summary>
    public required string Name { get; init; }
    /// <summary>Save comment (up to 10 ASCII characters).</summary>
    public required string Comment { get; init; }
    /// <summary>Language of the save ("Japanese", "English", …).</summary>
    public required string Language { get; init; }
    /// <summary>Size of the save data in bytes (from the directory entry).</summary>
    public required long DataSize { get; init; }
}

/// <summary>The directory of a Sega Saturn backup memory (internal 32 KB or a cartridge).</summary>
public sealed record SaturnBackup
{
    public required IReadOnlyList<SaturnSave> Saves { get; init; }
}

/// <summary>
/// Reads a Sega Saturn backup-memory image — the internal 32 KB battery-backed RAM or
/// a larger backup cartridge (512 KB / 1 MB). It enumerates the save directory (names,
/// comments, language, data sizes). Everything is BIG-ENDIAN.
///
/// Clean-room, from the public Saturn backup-memory description:
///   The memory opens with the signature string "BackUpRam Format" repeated across the
///   first block. Storage is divided into 0x40-byte blocks; each save's first block is a
///   directory entry marked by an 0x80 "occupied" tag. An entry: 0x00 tag (0x80),
///   0x04 name (11), 0x0F language (1), 0x10 comment (10), 0x1A date (u32), 0x1E data
///   size (u32), 0x22 a block-number list (u16 each, 0x0000-terminated). This reader
///   enumerates the directory reliably; because the block-linking is fiddly, full data
///   extraction via the block list is intentionally omitted (best-effort) rather than
///   risk returning wrong bytes.
/// </summary>
public static class SaturnSaveReader
{
    public const string Signature = "BackUpRam Format";
    public const int BlockSize = 0x40;
    private const byte OccupiedTag = 0x80;

    private static readonly string[] Languages =
        { "Japanese", "English", "French", "German", "Spanish", "Italian" };

    public static bool IsSaturnBackup(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return HasSignature(data);
    }

    public static SaturnBackup Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < BlockSize)
            throw new SaturnSaveFormatException($"A Saturn backup is at least {BlockSize} bytes; got {data.Length}.");
        if (!HasSignature(data))
            throw new SaturnSaveFormatException("Missing the \"BackUpRam Format\" signature — not a Saturn backup.");

        var saves = new List<SaturnSave>();
        // Scan every 0x40-aligned block after the signature block for an occupied entry.
        for (int at = BlockSize; at + BlockSize <= data.Length; at += BlockSize)
        {
            if (data[at] != OccupiedTag) continue;

            string name = Ascii(data, at + 0x04, 11);
            if (name.Length == 0) continue;   // an occupied entry always names its save

            string comment = Ascii(data, at + 0x10, 10);
            byte lang = data[at + 0x0F];
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 0x1E));

            saves.Add(new SaturnSave
            {
                Name = name,
                Comment = comment,
                Language = lang < Languages.Length ? Languages[lang] : $"Unknown({lang})",
                DataSize = size,
            });
        }
        return new SaturnBackup { Saves = saves };
    }

    private static bool HasSignature(byte[] data)
    {
        var sig = Encoding.ASCII.GetBytes(Signature);
        if (data.Length < sig.Length) return false;
        for (int i = 0; i < sig.Length; i++) if (data[i] != sig[i]) return false;
        return true;
    }

    private static string Ascii(byte[] d, int at, int len)
    {
        if (at + len > d.Length) len = Math.Max(0, d.Length - at);
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte b = d[at + i];
            if (b == 0) break;
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : ' ');
        }
        return sb.ToString().TrimEnd();
    }
}
