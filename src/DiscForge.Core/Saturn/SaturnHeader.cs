// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Cdi;
using DiscForge.Core.Cue;

namespace DiscForge.Core.Saturn;

/// <summary>
/// The Sega Saturn disc header — the fixed-width ASCII table at the very start of a
/// Saturn CD's data track, identifying the disc. Reading it is purely descriptive: it
/// names the disc (product number, version, region, title), it does not touch the
/// Saturn security ring or any protection. It answers the practical questions a person
/// backing up or cataloguing a Saturn game wants confirmed.
///
/// The header (0x100 bytes at the start of the data track's user data):
///
///   0x00  16  Hardware ID     "SEGA SEGASATURN " (the signature)
///   0x10  16  Maker ID        "SEGA ENTERPRISES", or "SEGA TP T-xxx" for licensees
///   0x20  10  Product number  e.g. "T-1810G", "GS-9109"
///   0x2A   6  Version         e.g. "V1.000"
///   0x30   8  Release date    "YYYYMMDD"
///   0x38   8  Device info     e.g. "CD-1/1"
///   0x40  10  Area symbols    region letters (J U E T B K A L)
///   0x4A   6  (spaces / reserved)
///   0x50  16  Peripherals     letters for the input devices the game supports
///   0x60 112  Game title
///
/// Clean-room from the public Saturn disc-header description; validated by round trip
/// (writer-shaped fixture -> reader) and by the signature check.
/// </summary>
public sealed record SaturnHeader
{
    public required string HardwareId { get; init; }
    public required string MakerId { get; init; }
    public required string ProductNumber { get; init; }
    public required string Version { get; init; }
    public required string ReleaseDate { get; init; }
    public required string DeviceInfo { get; init; }
    /// <summary>Regions the disc declares (Japan, USA, Europe, …), from the area symbols.</summary>
    public required IReadOnlyList<string> Regions { get; init; }
    /// <summary>The raw area-symbol letters, e.g. "JUE".</summary>
    public required string AreaSymbols { get; init; }
    /// <summary>The raw peripheral letters, e.g. "J" or "JAM".</summary>
    public required string Peripherals { get; init; }
    public required string Title { get; init; }

    /// <summary>The peripheral letters decoded to capabilities (Control Pad, Mouse, …).</summary>
    public IReadOnlyList<string> SupportedPeripherals => SaturnDisc.DecodePeripherals(Peripherals);
}

public sealed class SaturnFormatException(string message) : Exception(message);

/// <summary>Reads <see cref="SaturnHeader"/> from Saturn disc images in the containers
/// DiscForge understands (raw bin, bin/cue, DiscJuggler CDI, cooked ISO).</summary>
public static class SaturnDisc
{
    /// <summary>The hardware signature every Saturn disc begins with.</summary>
    public const string Signature = "SEGA SEGASATURN";

    /// <summary>Parse a Saturn header from the first 0x100 bytes of a data track's cooked
    /// user data. Throws if the signature is absent.</summary>
    public static SaturnHeader Parse(ReadOnlySpan<byte> meta)
    {
        if (meta.Length < 0x100)
            throw new SaturnFormatException($"Need at least 256 bytes of Saturn header; got {meta.Length}.");

        string hardware = Ascii(meta.Slice(0x00, 16));
        if (!hardware.StartsWith(Signature, StringComparison.Ordinal))
            throw new SaturnFormatException(
                $"Not a Saturn disc header: expected \"{Signature}\", found \"{hardware}\".");

        string areas = Ascii(meta.Slice(0x40, 10));
        var regions = new List<string>();
        foreach (char c in areas)
        {
            string? name = c switch
            {
                'J' => "Japan",
                'U' => "USA",
                'E' => "Europe",
                'T' => "Asia (NTSC)",
                'B' => "Brazil",
                'K' => "Korea",
                'A' => "Asia (PAL)",
                'L' => "Latin America",
                _ => null,
            };
            if (name is not null && !regions.Contains(name)) regions.Add(name);
        }

        return new SaturnHeader
        {
            HardwareId = hardware,
            MakerId = Ascii(meta.Slice(0x10, 16)),
            ProductNumber = Ascii(meta.Slice(0x20, 10)),
            Version = Ascii(meta.Slice(0x2A, 6)),
            ReleaseDate = Ascii(meta.Slice(0x30, 8)),
            DeviceInfo = Ascii(meta.Slice(0x38, 8)),
            Regions = regions,
            AreaSymbols = areas,
            Peripherals = Ascii(meta.Slice(0x50, 16)),
            Title = Ascii(meta.Slice(0x60, 112)),
        };
    }

    /// <summary>True if these bytes begin with the Saturn signature.</summary>
    public static bool IsHeader(ReadOnlySpan<byte> meta) =>
        meta.Length >= 16 && Ascii(meta[..16]).StartsWith(Signature, StringComparison.Ordinal);

    // Saturn peripheral letters, from the public disc-header documentation.
    private static readonly Dictionary<char, string> PeripheralLetters = new()
    {
        ['J'] = "Control Pad",
        ['A'] = "Analog controller",
        ['M'] = "Mouse",
        ['K'] = "Keyboard",
        ['S'] = "Steering controller",
        ['T'] = "Multitap",
        ['G'] = "Light gun",
        ['F'] = "Floppy drive",
        ['R'] = "ROM cartridge",
        ['P'] = "MPEG card",
    };

    /// <summary>Decode the peripheral letters into human-readable device names; unknown
    /// letters are listed verbatim so nothing is silently dropped.</summary>
    public static IReadOnlyList<string> DecodePeripherals(string? letters)
    {
        if (string.IsNullOrWhiteSpace(letters)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (char c in letters.Trim())
        {
            if (char.IsWhiteSpace(c)) continue;
            string name = PeripheralLetters.TryGetValue(c, out var n) ? n : $"Unknown ('{c}')";
            if (!list.Contains(name)) list.Add(name);
        }
        return list;
    }

    /// <summary>Identify a Saturn disc from any image DiscForge understands, dispatching
    /// on the extension: a bin/cue (.cue), a DiscJuggler image (.cdi), or a raw data
    /// track / ISO (.bin/.iso and anything else). Returns the header, or null when the
    /// image carries no Saturn data track.</summary>
    public static SaturnHeader? Identify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Image not found.", path);

        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cue" => ReadFromBinCue(path),
            ".cdi" => ReadFromCdi(path),
            _ => ReadFromRaw(path),
        };
    }

    private static SaturnHeader? ReadFromBinCue(string cuePath)
    {
        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        foreach (var t in cue.Tracks)
        {
            if (t.Type == CueTrackType.Audio) continue;
            var (sectorSize, userOffset) = SectorGeometry(t.Type);
            string binPath = Path.Combine(baseDir, t.File);
            if (!File.Exists(binPath)) continue;

            long startSector = DataStartSector(t);
            long at = startSector * sectorSize + userOffset;
            using var fs = File.OpenRead(binPath);
            var buffer = new byte[0x100];
            if (at + 0x100 > fs.Length) continue;
            fs.Seek(at, SeekOrigin.Begin);
            if (!ReadFull(fs, buffer)) continue;
            if (IsHeader(buffer)) return Parse(buffer);
        }
        return null;
    }

    private static SaturnHeader? ReadFromCdi(string path)
    {
        using var fs = File.OpenRead(path);
        var image = CdiParser.Parse(fs);
        foreach (var track in image.AllTracks)
        {
            if (track.Mode == CdiTrackMode.Audio) continue;
            int sectorBytes = (int)track.SectorSize;
            int userOffset = (track.Mode, sectorBytes) switch
            {
                (CdiTrackMode.Mode1, 2352) => 16,
                (CdiTrackMode.Mode2, 2352) => 24,
                (CdiTrackMode.Mode2, 2336) => 8,
                _ => 0,
            };
            long at = track.FileOffset + (long)track.PregapSectors * sectorBytes + userOffset;
            var buffer = new byte[0x100];
            if (at + 0x100 > fs.Length) continue;
            fs.Seek(at, SeekOrigin.Begin);
            if (!ReadFull(fs, buffer)) continue;
            if (IsHeader(buffer)) return Parse(buffer);
        }
        return null;
    }

    private static SaturnHeader? ReadFromRaw(string path)
    {
        using var fs = File.OpenRead(path);
        // Cooked 2048/ISO (offset 0), raw Mode 1 2352 (offset 16), raw Mode 2 2352 (offset 24).
        foreach (int off in new[] { 0, 16, 24 })
        {
            if (off + 0x100 > fs.Length) continue;
            var buffer = new byte[0x100];
            fs.Seek(off, SeekOrigin.Begin);
            if (!ReadFull(fs, buffer)) continue;
            if (IsHeader(buffer)) return Parse(buffer);
        }
        return null;
    }

    private static (int SectorSize, int UserOffset) SectorGeometry(CueTrackType type) => type switch
    {
        CueTrackType.Mode1_2048 => (2048, 0),
        CueTrackType.Mode1_2352 => (2352, 16),
        CueTrackType.Mode2_2336 => (2336, 8),
        CueTrackType.Mode2_2352 => (2352, 24),
        _ => (2352, 16),
    };

    private static long DataStartSector(CueTrack t)
    {
        if (t.Indices.Count == 0) return 0;
        var i1 = t.Indices.FirstOrDefault(i => i.Number == 1);
        return (i1 ?? t.Indices.OrderBy(i => i.Time.ToSectors()).First()).Time.ToSectors();
    }

    private static bool ReadFull(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static string Ascii(ReadOnlySpan<byte> field) =>
        Encoding.ASCII.GetString(field).TrimEnd('\0', ' ');
}
