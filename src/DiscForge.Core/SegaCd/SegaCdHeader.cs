// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Cue;

namespace DiscForge.Core.SegaCd;

/// <summary>
/// The header of a Sega CD / Mega-CD disc, read from the start of its data track. The first sector opens
/// with the boot signature ("SEGADISCSYSTEM" / "SEGABOOTDISC"); at offset 0x100 sits the standard Sega
/// hardware header shared with the Mega Drive / Genesis cartridge format — console name, copyright and
/// build date, the domestic and international titles, the product code and checksum, the supported input
/// devices, and the region field.
///
///   0x000  16  boot signature ("SEGADISCSYSTEM  ")
///   0x100  16  console name    ("SEGA MEGA DRIVE " / "SEGA GENESIS    ")
///   0x110  16  copyright + date("(C)SEGA 1993.MAR")
///   0x120  48  domestic title
///   0x150  48  international title
///   0x180  14  product code / version
///   0x18E   2  checksum (big-endian)
///   0x190  16  I/O device support
///   0x1F0  16  region field ("JUE" old style, or a hex bitfield)
///
/// Clean-room from the public Sega disc-header description; identification only — it reads and reports.
/// </summary>
public sealed record SegaCdHeader
{
    public required string SystemId { get; init; }
    public required string ConsoleName { get; init; }
    public required string Copyright { get; init; }
    public required string DomesticTitle { get; init; }
    public required string InternationalTitle { get; init; }
    public required string ProductCode { get; init; }
    public required ushort Checksum { get; init; }
    public required string IoSupport { get; init; }
    /// <summary>The raw region field, preserved verbatim so nothing is lost to interpretation.</summary>
    public required string RegionField { get; init; }
    /// <summary>Decoded regions (Japan / USA / Europe) from the region field.</summary>
    public required IReadOnlyList<string> Regions { get; init; }

    /// <summary>The best available human title (international, else domestic).</summary>
    public string Title => InternationalTitle.Length > 0 ? InternationalTitle : DomesticTitle;
}

public sealed class SegaCdFormatException(string message) : Exception(message);

/// <summary>Reader for the Sega CD / Mega-CD boot header.</summary>
public static class SegaCdDisc
{
    public const string SignatureDisc = "SEGADISCSYSTEM";
    public const string SignatureBoot = "SEGABOOTDISC";

    /// <summary>The Sega hardware header begins here; the parse needs at least 0x200 bytes.</summary>
    public const int HeaderBytes = 0x200;

    /// <summary>True if these bytes open with a Sega CD boot signature.</summary>
    public static bool IsBootSector(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 16) return false;
        string id = Ascii(sector[..16]);
        return id.StartsWith(SignatureDisc, StringComparison.Ordinal)
            || id.StartsWith(SignatureBoot, StringComparison.Ordinal);
    }

    /// <summary>Parse the header from the first <see cref="HeaderBytes"/> bytes of a data track's cooked
    /// user data (2048-byte sectors).</summary>
    public static SegaCdHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderBytes)
            throw new SegaCdFormatException($"Need at least {HeaderBytes} bytes to parse a Sega CD header.");
        if (!IsBootSector(data))
            throw new SegaCdFormatException("Not a Sega CD boot sector (missing the SEGADISCSYSTEM signature).");

        string region = Ascii(data.Slice(0x1F0, 16));
        return new SegaCdHeader
        {
            SystemId = Ascii(data[..16]),
            ConsoleName = Ascii(data.Slice(0x100, 16)),
            Copyright = Ascii(data.Slice(0x110, 16)),
            DomesticTitle = Ascii(data.Slice(0x120, 48)),
            InternationalTitle = Ascii(data.Slice(0x150, 48)),
            ProductCode = Ascii(data.Slice(0x180, 14)),
            Checksum = (ushort)((data[0x18E] << 8) | data[0x18F]),
            IoSupport = Ascii(data.Slice(0x190, 16)),
            RegionField = region,
            Regions = DecodeRegion(region),
        };
    }

    /// <summary>Decode the region field. The classic style spells regions with the letters J/U/E; the
    /// later style packs them into a single hex digit (bit 0 = Japan, bit 2 = North America, bit 3 =
    /// Europe). The raw field is always preserved on the header for anything this can't classify.</summary>
    public static IReadOnlyList<string> DecodeRegion(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return Array.Empty<string>();
        string f = field.Trim();

        // Letter style: the field is only J/U/E characters.
        if (f.All(c => c is 'J' or 'U' or 'E'))
        {
            var list = new List<string>();
            if (f.Contains('J')) list.Add("Japan");
            if (f.Contains('U')) list.Add("USA");
            if (f.Contains('E')) list.Add("Europe");
            return list;
        }

        // Hex-bitfield style: a single hex digit.
        if (f.Length == 1 && Uri.IsHexDigit(f[0]))
        {
            int bits = System.Convert.ToInt32(f, 16);
            var list = new List<string>();
            if ((bits & 0x1) != 0) list.Add("Japan");
            if ((bits & 0x4) != 0) list.Add("USA");
            if ((bits & 0x8) != 0) list.Add("Europe");
            return list.Count > 0 ? list : new List<string> { $"region 0x{bits:X}" };
        }

        return Array.Empty<string>();
    }

    /// <summary>Identify a Sega CD disc from a bin/cue or a raw data track / ISO, or null if none.</summary>
    public static SegaCdHeader? Identify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Image not found.", path);
        return Path.GetExtension(path).ToLowerInvariant() == ".cue"
            ? ReadFromBinCue(path)
            : ReadFromRaw(path);
    }

    // ---- image reading ------------------------------------------------------

    private static SegaCdHeader? ReadFromBinCue(string cuePath)
    {
        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        foreach (var t in cue.Tracks)
        {
            if (t.Type == CueTrackType.Audio) continue;
            var (sectorSize, userOffset) = SectorGeometry(t.Type);
            string binPath = Path.Combine(baseDir, t.File);
            if (!File.Exists(binPath)) continue;

            long at = DataStartSector(t) * sectorSize + userOffset;
            using var fs = File.OpenRead(binPath);
            var buffer = new byte[HeaderBytes];
            if (at + HeaderBytes > fs.Length) continue;
            fs.Seek(at, SeekOrigin.Begin);
            if (!ReadFull(fs, buffer)) continue;
            if (IsBootSector(buffer)) return Parse(buffer);
        }
        return null;
    }

    private static SegaCdHeader? ReadFromRaw(string path)
    {
        using var fs = File.OpenRead(path);
        foreach (int off in new[] { 0, 16, 24 })   // cooked/ISO, raw Mode 1, raw Mode 2
        {
            if (off + HeaderBytes > fs.Length) continue;
            var buffer = new byte[HeaderBytes];
            fs.Seek(off, SeekOrigin.Begin);
            if (!ReadFull(fs, buffer)) continue;
            if (IsBootSector(buffer)) return Parse(buffer);
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
