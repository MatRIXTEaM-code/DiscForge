// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;
using DiscForge.Core.Cdi;
using DiscForge.Core.Cue;

namespace DiscForge.Core.Gdi;

/// <summary>
/// The Dreamcast boot header ("IP.BIN" metadata) — the first bytes of a GD-ROM's
/// bootable data track, identifying the disc. Reading it is purely descriptive:
/// it names the disc, it does not unlock or decrypt anything (a GD-ROM has no
/// encryption). It answers the practical questions — what region is this, what
/// is its product number, what is it called — that a person backing up or
/// patching a Dreamcast game wants confirmed.
///
/// The meta area is a table of fixed-width ASCII fields at the very start of the
/// track's user data:
///
///   0x00  16  Hardware ID    "SEGA SEGAKATANA "  (the signature)
///   0x10  16  Maker ID       "SEGA ENTERPRISES"
///   0x20  16  Device info    disc number / total, e.g. "GD-ROM1/1"
///   0x30   8  Area symbols   region letters: J (Japan), U (USA), E (Europe)
///   0x38   8  Peripherals    controller/VMU/etc. support bitfield (hex text)
///   0x40  10  Product number e.g. "T-8101N"
///   0x4A   6  Version        e.g. "V1.001"
///   0x50  16  Release date   "YYYYMMDD"
///   0x60  16  Boot file      the bootstrap, e.g. "1ST_READ.BIN"
///   0x70  16  Maker / company
///   0x80 128  Software title the game's name
/// </summary>
public sealed record IpBinHeader
{
    public required string HardwareId { get; init; }
    public required string MakerId { get; init; }
    public required string DeviceInfo { get; init; }
    /// <summary>Regions the disc declares — Japan, USA, Europe — from the area
    /// symbol letters.</summary>
    public required IReadOnlyList<string> Regions { get; init; }
    public required string Peripherals { get; init; }
    public required string ProductNumber { get; init; }
    public required string Version { get; init; }
    public required string ReleaseDate { get; init; }
    public required string BootFile { get; init; }
    public required string Maker { get; init; }
    public required string Title { get; init; }

    /// <summary>This disc's position in a multi-disc set, from the device info ("GD-ROM<b>n</b>/<b>m</b>"),
    /// or null if the field wasn't in that form.</summary>
    public int? DiscNumber { get; init; }
    public int? DiscTotal { get; init; }

    /// <summary>The 16-bit checksum stored in the device-info field (its leading 4 hex digits), or null
    /// when the field carries no checksum — as on many homebrew or rebuilt headers.</summary>
    public int? StoredCrc { get; init; }
    /// <summary>The checksum recomputed from the product number + version with the Katana boot algorithm.
    /// A retail header stores this same value in its device info.</summary>
    public required int ComputedCrc { get; init; }

    /// <summary>The hardware and maker IDs are the exact Sega signatures ("SEGA SEGAKATANA" /
    /// "SEGA ENTERPRISES"). A rebuilt or damaged header often gets these subtly wrong.</summary>
    public bool HardwareIdValid => string.Equals(HardwareId, IpBin.Signature, StringComparison.Ordinal);
    public bool MakerIdValid => string.Equals(MakerId, IpBin.MakerSignature, StringComparison.Ordinal);
    /// <summary>The stored device-info checksum matches the one recomputed from the product fields — the
    /// header's own integrity check passes. Null CRC (no checksum stored) is reported as not-valid here;
    /// <see cref="CrcPresent"/> distinguishes "wrong" from "absent".</summary>
    public bool CrcValid => StoredCrc is { } s && s == ComputedCrc;
    public bool CrcPresent => StoredCrc.HasValue;

    /// <summary>A one-line integrity read of the boot header, for a preservation record: whether the
    /// Sega signatures and the device-info checksum agree with the header's own contents. Descriptive —
    /// it verifies the header against itself, it decrypts and unlocks nothing.</summary>
    public string Integrity()
    {
        var notes = new List<string>();
        if (!HardwareIdValid) notes.Add($"hardware ID is \"{HardwareId}\", not \"{IpBin.Signature}\"");
        if (!MakerIdValid) notes.Add($"maker ID is \"{MakerId}\", not \"{IpBin.MakerSignature}\"");
        if (!CrcPresent) notes.Add($"device info stores no checksum (computed {ComputedCrc:X4}) — homebrew or rebuilt header");
        else if (!CrcValid) notes.Add($"device-info CRC mismatch: stored {StoredCrc:X4}, computed {ComputedCrc:X4} — header edited or corrupt");
        if (notes.Count == 0) return $"boot header intact — signatures and CRC {ComputedCrc:X4} check out.";
        return string.Join("; ", notes) + ".";
    }

    /// <summary>The area symbols as a compact string, e.g. "JUE" or "E".</summary>
    public string RegionCode => string.Concat(Regions.Select(r => r[0]));

    /// <summary>The peripherals bitfield decoded into human-readable capabilities,
    /// e.g. "Standard controller", "Memory card (VMU)", "VGA box".</summary>
    public IReadOnlyList<string> SupportedPeripherals => IpBin.DecodePeripherals(Peripherals);
}

public sealed class IpBinFormatException(string message) : Exception(message);

public static class IpBin
{
    /// <summary>The hardware signature every Dreamcast disc begins with.</summary>
    public const string Signature = "SEGA SEGAKATANA";

    /// <summary>The maker signature a retail Dreamcast header carries at 0x10.</summary>
    public const string MakerSignature = "SEGA ENTERPRISES";

    /// <summary>
    /// The Katana boot checksum — the 16-bit value a Dreamcast header stores in the first four hex
    /// digits of its device-info field, computed over the 16 bytes of product number + version (0x40).
    /// This is the header's own integrity check; recomputing it and comparing tells you whether the boot
    /// metadata is intact or was edited/corrupted. Algorithm per the public Katana bootstrap spec: a
    /// CRC-16 with polynomial 0x1021 (4129), initial value 0xFFFF, MSB-first, no final XOR.
    /// </summary>
    public static int BootCrc(ReadOnlySpan<byte> productAndVersion)
    {
        int n = 0xffff;
        for (int i = 0; i < productAndVersion.Length; i++)
        {
            n ^= productAndVersion[i] << 8;
            for (int c = 0; c < 8; c++)
                n = (n & 0x8000) != 0 ? (n << 1) ^ 4129 : n << 1;
        }
        return n & 0xffff;
    }

    /// <summary>Parse a boot header from the first bytes of a data track's cooked
    /// user data (at least 0x100 bytes). Throws if the signature is absent.</summary>
    public static IpBinHeader Parse(ReadOnlySpan<byte> meta)
    {
        if (meta.Length < 0x100)
            throw new IpBinFormatException(
                $"Need at least 256 bytes of boot header; got {meta.Length}.");

        string hardware = Ascii(meta.Slice(0x00, 16));
        if (!hardware.StartsWith(Signature, StringComparison.Ordinal))
            throw new IpBinFormatException(
                $"Not a Dreamcast boot header: expected \"{Signature}\", found \"{hardware}\". " +
                "This track is not a bootable GD-ROM data track.");

        var areas = meta.Slice(0x30, 8);
        var regions = new List<string>();
        // The area symbols sit at fixed positions: J, U, E — a letter present
        // means that region, a space means absent.
        if (areas.IndexOf((byte)'J') >= 0) regions.Add("Japan");
        if (areas.IndexOf((byte)'U') >= 0) regions.Add("USA");
        if (areas.IndexOf((byte)'E') >= 0) regions.Add("Europe");

        string deviceInfo = Ascii(meta.Slice(0x20, 16));
        var (storedCrc, discNo, discTotal) = ParseDeviceInfo(deviceInfo);

        return new IpBinHeader
        {
            HardwareId = hardware,
            MakerId = Ascii(meta.Slice(0x10, 16)),
            DeviceInfo = deviceInfo,
            Regions = regions,
            Peripherals = Ascii(meta.Slice(0x38, 8)),
            ProductNumber = Ascii(meta.Slice(0x40, 10)),
            Version = Ascii(meta.Slice(0x4A, 6)),
            ReleaseDate = Ascii(meta.Slice(0x50, 16)),
            BootFile = Ascii(meta.Slice(0x60, 16)),
            Maker = Ascii(meta.Slice(0x70, 16)),
            Title = Ascii(meta.Slice(0x80, 128)),
            DiscNumber = discNo,
            DiscTotal = discTotal,
            StoredCrc = storedCrc,
            // The header's own checksum covers the 16 bytes of product number (0x40) + version (0x4A).
            ComputedCrc = BootCrc(meta.Slice(0x40, 16)),
        };
    }

    /// <summary>Pull the stored checksum and the disc x/y out of the device-info field, which a retail
    /// header formats as "<b>CRC</b> GD-ROM<b>n</b>/<b>m</b>" (e.g. "8B40 GD-ROM2/3"). Either part may
    /// be absent on a rebuilt header, so each is returned as null when it isn't there.</summary>
    internal static (int? Crc, int? DiscNumber, int? DiscTotal) ParseDeviceInfo(string deviceInfo)
    {
        int? crc = null, num = null, total = null;
        string s = deviceInfo.Trim();

        // A leading 4-hex-digit token, before the first space, is the checksum.
        int sp = s.IndexOf(' ');
        string head = sp >= 0 ? s[..sp] : s;
        if (head.Length == 4 &&
            int.TryParse(head, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int c))
            crc = c;

        int gd = s.IndexOf("GD-ROM", StringComparison.OrdinalIgnoreCase);
        if (gd >= 0)
        {
            string rest = s[(gd + 6)..];
            int slash = rest.IndexOf('/');
            if (slash > 0)
            {
                if (int.TryParse(rest[..slash].Trim(), out int n)) num = n;
                string after = rest[(slash + 1)..].Trim();
                // Stop at the first non-digit (trailing spaces/padding).
                int end = 0;
                while (end < after.Length && char.IsDigit(after[end])) end++;
                if (end > 0 && int.TryParse(after[..end], out int t)) total = t;
            }
        }
        return (crc, num, total);
    }

    /// <summary>True if these bytes begin with the Dreamcast signature.</summary>
    public static bool IsBootHeader(ReadOnlySpan<byte> meta) =>
        meta.Length >= 16 && Ascii(meta[..16]).StartsWith(Signature, StringComparison.Ordinal);

    // The peripherals bitfield (8 ASCII hex digits at 0x38): bit -> capability.
    // Ordered most-significant first so a read lists the controller before the
    // finer analog/expansion bits. From the public IP.BIN documentation.
    private static readonly (int Bit, string Name)[] PeripheralBits =
    {
        (24, "Standard controller (Start + A + B + directions)"),
        (23, "C button"),
        (22, "D button"),
        (21, "X button"),
        (20, "Y button"),
        (19, "Z button"),
        (18, "Expanded direction buttons"),
        (17, "Analog R trigger"),
        (16, "Analog L trigger"),
        (15, "Analog horizontal controller"),
        (14, "Analog vertical controller"),
        (13, "Expanded analog horizontal"),
        (12, "Expanded analog vertical"),
        (11, "Light gun"),
        (10, "Keyboard"),
        (9, "Mouse"),
        (8, "Memory card (VMU)"),
        (7, "Microphone"),
        (6, "Vibration pack"),
        (5, "Other expansions"),
        (1, "VGA box"),
        (0, "Windows CE"),
    };

    /// <summary>
    /// Decode the peripherals field (its 8 hex digits) into the list of
    /// capabilities the disc declares. An unparseable or empty field yields an
    /// empty list rather than throwing — identification should still show the rest.
    /// </summary>
    public static IReadOnlyList<string> DecodePeripherals(string? hexField)
    {
        if (string.IsNullOrWhiteSpace(hexField)) return Array.Empty<string>();
        string hex = hexField.Trim();
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits))
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var (bit, name) in PeripheralBits)
            if ((bits & (1u << bit)) != 0) list.Add(name);
        return list;
    }

    /// <summary>
    /// Read the boot header from a BIN/CUE image (the shape a Dreamcast MIL-CD /
    /// CD-ROM rip takes). Each data track's first sector is checked for the
    /// signature and the first match is parsed; audio tracks are skipped. Returns
    /// null when no track carries a Dreamcast boot header.
    /// </summary>
    public static IpBinHeader? ReadFromBinCue(string cuePath)
    {
        ArgumentNullException.ThrowIfNull(cuePath);
        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";

        foreach (var t in cue.Tracks)
        {
            if (t.Type == CueTrackType.Audio) continue;
            var (sectorSize, userOffset) = SectorGeometry(t.Type);

            string binPath = Path.Combine(baseDir, t.File);
            if (!File.Exists(binPath)) continue;

            // The track's first data sector: its INDEX 01 (the audio/data start),
            // measured in its file (relative for a per-track bin, absolute for a
            // single-file image — both give the right byte offset within t.File).
            long startSector = DataStartSector(t);
            long at = startSector * sectorSize + userOffset;

            using var fs = File.OpenRead(binPath);
            if (at + 0x100 > fs.Length) continue;
            var buffer = new byte[0x100];
            fs.Seek(at, SeekOrigin.Begin);
            if (!ReadFull(fs, buffer)) continue;
            if (IsBootHeader(buffer)) return Parse(buffer);
        }
        return null;
    }

    // Stored sector size and the offset of user data within it, per CUE track type.
    private static (int SectorSize, int UserOffset) SectorGeometry(CueTrackType type) => type switch
    {
        CueTrackType.Mode1_2048 => (2048, 0),
        CueTrackType.Mode1_2352 => (2352, 16),         // sync(12) + header(4)
        CueTrackType.Mode2_2336 => (2336, 8),          // subheader(8)
        CueTrackType.Mode2_2352 => (2352, 24),         // sync(12) + header(4) + subheader(8)
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

    /// <summary>
    /// Read the boot header from a GD-ROM image's bootable data track. The track
    /// file's first sector is cooked — for a raw 2352-byte Mode 1 sector the
    /// user data starts 16 bytes in (sync + header); a 2048-byte track is already
    /// cooked. The meta table lives in that first sector.
    /// </summary>
    public static IpBinHeader ReadFromTrack(string trackPath, GdiTrack track)
    {
        ArgumentNullException.ThrowIfNull(trackPath);
        ArgumentNullException.ThrowIfNull(track);

        using var fs = File.OpenRead(trackPath);
        return ReadFromTrack(fs, track);
    }

    /// <summary>Read the boot header from an open track stream.</summary>
    public static IpBinHeader ReadFromTrack(Stream trackStream, GdiTrack track)
    {
        ArgumentNullException.ThrowIfNull(trackStream);
        ArgumentNullException.ThrowIfNull(track);

        // The first sector holds the meta table. Cooked data begins after the
        // 16-byte sync+header of a raw Mode 1 sector; a 2048 track has none.
        int userDataOffset = track.SectorSize >= 2352 ? 16 : 0;
        long at = track.Offset + userDataOffset;

        var buffer = new byte[0x100];
        trackStream.Seek(at, SeekOrigin.Begin);
        int read = 0;
        while (read < buffer.Length)
        {
            int n = trackStream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }
        if (read < buffer.Length)
            throw new IpBinFormatException(
                "The track is too short to hold a boot header — it may be truncated.");

        return Parse(buffer);
    }

    /// <summary>Read the boot header from a GD-ROM image's boot data track, given
    /// the .gdi and the directory its track files live in. Null if the image has
    /// no high-density data track.</summary>
    public static IpBinHeader? ReadFromDisc(GdiDisc disc, string gdiDirectory)
    {
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(gdiDirectory);

        var boot = disc.BootDataTrack;
        if (boot is null) return null;

        string path = Path.IsPathRooted(boot.FileName)
            ? boot.FileName
            : Path.Combine(gdiDirectory, boot.FileName);
        if (!File.Exists(path)) return null;

        return ReadFromTrack(path, boot);
    }

    /// <summary>
    /// Identify a Dreamcast disc from any image DiscForge understands, dispatching on
    /// the file extension: a GD-ROM index (.gdi), a MIL-CD bin/cue (.cue), a
    /// DiscJuggler image (.cdi), or a raw data track / ISO (.bin/.iso and anything
    /// else). Returns the boot header, or null when the image carries no bootable
    /// Dreamcast data track ("SEGA SEGAKATANA"). This is the one place the four
    /// container cases live, shared by the CLI and the GUI.
    /// </summary>
    public static IpBinHeader? Identify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Image not found.", path);

        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".gdi" => ReadFromDisc(GdiParser.ParseFile(path),
                                   Path.GetDirectoryName(Path.GetFullPath(path)) ?? "."),
            ".cue" => ReadFromBinCue(path),
            ".cdi" => ReadFromCdi(path),
            _ => ReadFromRaw(path),
        };
    }

    private static IpBinHeader? ReadFromCdi(string path)
    {
        using var fs = File.OpenRead(path);
        var image = CdiParser.Parse(fs);
        var track = image.AllTracks.FirstOrDefault(t => t.Mode != CdiTrackMode.Audio);
        if (track is null) return null;

        int sectorBytes = (int)track.SectorSize;
        int userOffset = (track.Mode, sectorBytes) switch
        {
            (CdiTrackMode.Mode1, 2352) => 16,
            (CdiTrackMode.Mode2, 2352) => 24,
            (CdiTrackMode.Mode2, 2336) => 8,
            _ => 0,   // 2048 cooked
        };
        long at = track.FileOffset + (long)track.PregapSectors * sectorBytes + userOffset;
        var buffer = new byte[0x100];
        fs.Seek(at, SeekOrigin.Begin);
        if (fs.Read(buffer, 0, buffer.Length) < buffer.Length) return null;
        return IsBootHeader(buffer) ? Parse(buffer) : null;
    }

    private static IpBinHeader? ReadFromRaw(string path)
    {
        using var fs = File.OpenRead(path);
        // Try a cooked 2048 sector (offset 0) and a raw Mode 1 2352 sector (offset 16).
        foreach (int off in new[] { 0, 16 })
        {
            if (off + 0x100 > fs.Length) continue;
            var buffer = new byte[0x100];
            fs.Seek(off, SeekOrigin.Begin);
            if (fs.Read(buffer, 0, buffer.Length) < buffer.Length) continue;
            if (IsBootHeader(buffer)) return Parse(buffer);
        }
        return null;
    }

    private static string Ascii(ReadOnlySpan<byte> field) =>
        Encoding.ASCII.GetString(field).TrimEnd('\0', ' ');
}
