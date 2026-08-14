// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.GameAudio;

/// <summary>
/// The header metadata of an NSF (NES Sound Format) file. Structure only — the
/// embedded 6502 code and APU register writes are never executed or emulated.
/// </summary>
public sealed class NsfFile
{
    public required int Version { get; init; }
    public required int TotalSongs { get; init; }

    /// <summary>1-based index of the song that plays first.</summary>
    public required int StartingSong { get; init; }

    public required string SongName { get; init; }
    public required string Artist { get; init; }
    public required string Copyright { get; init; }

    /// <summary>True when the tune targets PAL timing (bit 0 of the region flag).</summary>
    public required bool IsPal { get; init; }

    /// <summary>Expansion audio chips enabled by the flag byte at 0x7B.</summary>
    public required IReadOnlyList<string> ExpansionChips { get; init; }
}

/// <summary>
/// Reads the 128-byte header of an NSF file. Magic "NESM" + 0x1A at 0x00,
/// version at 0x05, song count at 0x06, starting song at 0x07, load/init/play
/// addresses (0x08/0x0A/0x0C), three 32-byte Latin-1 strings (name/artist/
/// copyright), a region flag at 0x7A and an expansion-chip flag at 0x7B. The
/// NSFe container ("NSFE" magic) is a different chunked format and is only
/// detected, not parsed. No audio is synthesised.
/// </summary>
public static class NsfReader
{
    private const int HeaderSize = 0x80;

    // Expansion-audio bits in the flag byte at 0x7B.
    private static readonly (int Bit, string Name)[] ExpansionBits =
    {
        (0, "VRC6"),
        (1, "VRC7"),
        (2, "FDS"),
        (3, "MMC5"),
        (4, "Namco 163"),
        (5, "Sunsoft 5B"),
    };

    public static bool IsNsf(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return HasMagic(data);
    }

    public static bool IsNsf(Stream stream) => IsNsf(ReadHead(stream, 5));

    /// <summary>True for the NSFe chunked container ("NSFE" magic) — a distinct format.</summary>
    public static bool IsNsfe(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Length >= 4 && data[0] == 'N' && data[1] == 'S' && data[2] == 'F' && data[3] == 'E';
    }

    public static NsfFile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Read(ReadAll(stream));
    }

    public static NsfFile Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (IsNsfe(data))
            throw new GameAudioFormatException(
                "This is an NSFe file — a chunked container distinct from classic NSF; DiscForge does not parse NSFe.");
        if (data.Length < HeaderSize)
            throw new GameAudioFormatException($"NSF file is only {data.Length} bytes — too short for the 128-byte header.");
        if (!HasMagic(data))
            throw new GameAudioFormatException("Not an NSF file — missing the \"NESM\\x1A\" magic at offset 0.");

        byte regionFlag = data[0x7A];
        byte expansion = data[0x7B];

        var chips = new List<string>();
        foreach (var (bit, name) in ExpansionBits)
            if ((expansion & (1 << bit)) != 0)
                chips.Add(name);

        return new NsfFile
        {
            Version = data[0x05],
            TotalSongs = data[0x06],
            StartingSong = data[0x07],
            SongName = ReadString(data, 0x0E, 32),
            Artist = ReadString(data, 0x2E, 32),
            Copyright = ReadString(data, 0x4E, 32),
            IsPal = (regionFlag & 0x01) != 0,
            ExpansionChips = chips,
        };
    }

    private static bool HasMagic(byte[] data) =>
        data.Length >= 5 && data[0] == 'N' && data[1] == 'E' && data[2] == 'S' && data[3] == 'M' && data[4] == 0x1A;

    // The three header strings are fixed 32-byte, NUL-padded, Latin-1 fields.
    private static string ReadString(byte[] data, int at, int maxLen)
    {
        if (at >= data.Length) return "";
        int end = Math.Min(at + maxLen, data.Length);
        int len = 0;
        while (at + len < end && data[at + len] != 0) len++;
        return Encoding.Latin1.GetString(data, at, len).TrimEnd();
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
