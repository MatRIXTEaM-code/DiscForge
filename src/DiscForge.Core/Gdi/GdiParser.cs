// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Gdi;

/// <summary>Whether a GD-ROM track carries data or Red Book audio.</summary>
public enum GdiTrackType { Audio, Data }

/// <summary>One track line from a .gdi index.</summary>
public sealed record GdiTrack
{
    public required int Number { get; init; }
    /// <summary>Absolute start LBA on the disc. The high-density area begins at
    /// <see cref="GdiParser.HighDensityStart"/>.</summary>
    public required long StartLba { get; init; }
    public required GdiTrackType Type { get; init; }
    /// <summary>Bytes per sector as the index declares — 2352 for raw tracks,
    /// occasionally 2048 for cooked data.</summary>
    public required int SectorSize { get; init; }
    /// <summary>The track's binary file, named relative to the .gdi.</summary>
    public required string FileName { get; init; }
    /// <summary>Byte offset into the file where the track's data begins (normally 0).</summary>
    public required long Offset { get; init; }

    public bool IsData => Type == GdiTrackType.Data;
    public bool IsHighDensity => StartLba >= GdiParser.HighDensityStart;
}

/// <summary>A parsed GD-ROM image described by a .gdi index.</summary>
public sealed record GdiDisc
{
    public required IReadOnlyList<GdiTrack> Tracks { get; init; }

    public IEnumerable<GdiTrack> DataTracks => Tracks.Where(t => t.IsData);
    public IEnumerable<GdiTrack> AudioTracks => Tracks.Where(t => t.Type == GdiTrackType.Audio);

    /// <summary>The track that holds the bootable game filesystem: the first data
    /// track in the high-density area (LBA ≥ 45000). This is the track a PPF patch
    /// or a filesystem browse targets. Null if the index has no high-density data
    /// track (an unusual or truncated dump).</summary>
    public GdiTrack? BootDataTrack =>
        DataTracks.Where(t => t.IsHighDensity).OrderBy(t => t.StartLba).FirstOrDefault();
}

public sealed class GdiFormatException(string message) : Exception(message);

/// <summary>
/// Reads the Dreamcast .gdi (GD-ROM) index — the text table of contents that
/// pairs with a set of raw track files and describes a GD-ROM image. A GD-ROM
/// has two areas: a low-density region any drive can read (tracks 1–2: the
/// "this is a Dreamcast disc" warning and a short audio track) and a
/// high-density region starting at LBA 45000 that holds the game and which only
/// the console's own drive can read. The .gdi records where each track sits, its
/// type, and the file its bytes live in.
///
/// The format is deliberately plain: a track count on the first line, then one
/// line per track of
///
///   number   startLBA   type(0=audio,4=data)   sectorSize   filename   offset
///
/// with the filename optionally quoted when it contains spaces. This parses that
/// index and models the tracks; <see cref="GdiValidator"/> then checks the index
/// against the files beside it. It is pure text and arithmetic — no drive, no
/// decryption (a GD-ROM carries none), just an honest description of an image
/// DiscForge can then patch or, in time, browse and convert.
/// </summary>
public static class GdiParser
{
    /// <summary>The disc LBA at which a GD-ROM's high-density (game) area begins.</summary>
    public const long HighDensityStart = 45000;

    // GD-ROM track "type" codes in a .gdi line.
    private const int TypeAudio = 0;
    private const int TypeData = 4;

    /// <summary>Parse a .gdi index from its text. Does not touch the track files.</summary>
    public static GdiDisc Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // A wrong file dropped on the GDI view (a binary disc image, say) would
        // otherwise be split into enormous "lines" and echoed back in full; refuse
        // it cleanly instead. A NUL in the first few KB is a reliable "not text" tell.
        int probe = Math.Min(text.Length, 4096);
        if (text.AsSpan(0, probe).IndexOf('\0') >= 0)
            throw new GdiFormatException("This file is not a text .gdi index — it looks like binary data.");

        var lines = text.Replace("\r\n", "\n").Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .ToList();
        if (lines.Count == 0)
            throw new GdiFormatException("The .gdi index is empty.");

        if (!int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            throw new GdiFormatException(
                $"The first line of a .gdi is the track count; '{Short(lines[0])}' is not a number. " +
                "This does not look like a .gdi index.");
        if (count < 1)
            throw new GdiFormatException($"The track count is {count}; a GD-ROM has at least one track.");

        var tracks = new List<GdiTrack>();
        for (int i = 1; i < lines.Count; i++)
            tracks.Add(ParseTrackLine(lines[i]));

        if (tracks.Count != count)
            throw new GdiFormatException(
                $"The index declares {count} track(s) but lists {tracks.Count}.");

        return new GdiDisc { Tracks = tracks };
    }

    /// <summary>Parse a .gdi index from a file on disk.</summary>
    public static GdiDisc ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    private static GdiTrack ParseTrackLine(string line)
    {
        var fields = Tokenize(line);
        if (fields.Count != 6)
            throw new GdiFormatException(
                $"A track line needs 6 fields (number, LBA, type, sector size, file, offset); " +
                $"'{line}' has {fields.Count}.");

        int number = ParseInt(fields[0], "track number", line);
        long lba = ParseLong(fields[1], "start LBA", line);
        int typeCode = ParseInt(fields[2], "type", line);
        int sectorSize = ParseInt(fields[3], "sector size", line);
        string file = fields[4];
        long offset = ParseLong(fields[5], "offset", line);

        GdiTrackType type = typeCode switch
        {
            TypeData => GdiTrackType.Data,
            TypeAudio => GdiTrackType.Audio,
            _ => throw new GdiFormatException(
                $"Track type {typeCode} in '{line}' is neither audio (0) nor data (4)."),
        };

        return new GdiTrack
        {
            Number = number,
            StartLba = lba,
            Type = type,
            SectorSize = sectorSize,
            FileName = file,
            Offset = offset,
        };
    }

    /// <summary>Split a track line into fields, honouring a quoted filename that
    /// may contain spaces.</summary>
    private static List<string> Tokenize(string line)
    {
        var fields = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;

            if (line[i] == '"')
            {
                int end = line.IndexOf('"', i + 1);
                if (end < 0)
                    throw new GdiFormatException($"Unclosed quote in '{line}'.");
                fields.Add(line[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                fields.Add(line[start..i]);
            }
        }
        return fields;
    }

    private static int ParseInt(string s, string what, string line) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : throw new GdiFormatException($"The {what} '{Short(s)}' in '{Short(line)}' is not a number.");

    private static long ParseLong(string s, string what, string line) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
            ? v
            : throw new GdiFormatException($"The {what} '{Short(s)}' in '{Short(line)}' is not a number.");

    // Bound and sanitise a snippet for an error message, so a wrong (e.g. binary)
    // file can never dump megabytes of content into the log.
    private static string Short(string s)
    {
        const int max = 48;
        var clean = new string(s.Where(c => !char.IsControl(c)).Take(max).ToArray());
        return s.Length > max ? clean + "…" : clean;
    }
}
