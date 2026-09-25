// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.GameAudio;

/// <summary>The GD3 metadata block of a VGM file (English variants preferred).</summary>
public sealed class Gd3
{
    public required string TrackName { get; init; }
    public required string GameName { get; init; }
    public required string System { get; init; }
    public required string Author { get; init; }
    public required string Date { get; init; }
    public required string Notes { get; init; }

    public static readonly Gd3 Empty = new()
    {
        TrackName = "", GameName = "", System = "", Author = "", Date = "", Notes = "",
    };
}

/// <summary>
/// A parsed VGM file: version, sample count / duration, the list of sound chips
/// whose clock fields are non-zero, and the GD3 tag. Structure and metadata only
/// — the VGM command stream is never executed and no chip is emulated.
/// </summary>
public sealed class VgmFile
{
    /// <summary>Dotted version from the BCD field (e.g. "1.61").</summary>
    public required string Version { get; init; }

    /// <summary>Total sample count at 44100 Hz (header field at 0x18).</summary>
    public required uint TotalSamples { get; init; }

    /// <summary>Playback length in seconds (<see cref="TotalSamples"/> / 44100).</summary>
    public double DurationSeconds => TotalSamples / 44100.0;

    /// <summary>Sound chips present, by non-zero clock field.</summary>
    public required IReadOnlyList<string> Chips { get; init; }

    /// <summary>GD3 tag, or <see cref="Gd3.Empty"/> when none is present.</summary>
    public required Gd3 Tags { get; init; }
}

/// <summary>
/// Reads the header and GD3 tag of a VGM (Video Game Music) log. Magic "Vgm "
/// at 0x00; a BCD version at 0x08; total_samples at 0x18; gd3_offset at 0x14
/// (relative to 0x14). Chip clock fields sit at fixed header offsets — a
/// non-zero clock means the chip is used. The GD3 block is "Gd3 " + version +
/// length + eleven NUL-terminated UTF-16LE strings. No chip is emulated.
/// </summary>
public static class VgmReader
{
    // Well-known chip clock offsets (a subset of the full VGM header). A non-zero
    // u32 at the offset means the chip is present. Many more chips exist at higher
    // offsets in recent VGM revisions; DiscForge reports the common ones below.
    private static readonly (int Offset, string Name)[] ChipClocks =
    {
        (0x0C, "SN76489"),
        (0x10, "YM2413"),
        (0x2C, "YM2612"),
        (0x30, "YM2151"),
        (0x38, "SegaPCM"),
        (0x40, "RF5C68"),
        (0x44, "YM2203"),
        (0x48, "YM2608"),
        (0x4C, "YM2610/B"),
        (0x50, "YM3812"),
        (0x54, "YM3526"),
        (0x58, "Y8950"),
        (0x5C, "YMF262"),
        (0x60, "YMF278B"),
        (0x68, "YMZ280B"),
    };

    public static bool IsVgm(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return HasMagic(data);
    }

    public static bool IsVgm(Stream stream) => IsVgm(ReadHead(stream, 4));

    public static VgmFile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Read(ReadAll(stream));
    }

    public static VgmFile Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 0x40)
            throw new GameAudioFormatException($"VGM file is only {data.Length} bytes — too short for the 0x40 header.");
        if (!HasMagic(data))
            throw new GameAudioFormatException("Not a VGM file — missing the \"Vgm \" magic at offset 0.");

        uint versionBcd = ReadU32(data, 0x08);
        string version = FormatBcdVersion(versionBcd);
        uint totalSamples = ReadU32(data, 0x18);

        // The VGM data start bounds which header clock fields actually exist. It is
        // stored at 0x34 (relative to 0x34) from v1.50; older files start at 0x40.
        long dataStart = 0x40;
        if (versionBcd >= 0x150)
        {
            uint rel = ReadU32(data, 0x34);
            if (rel != 0) dataStart = 0x34 + rel;
        }
        dataStart = Math.Min(dataStart, data.Length);

        var chips = new List<string>();
        foreach (var (off, name) in ChipClocks)
            if (off + 4 <= dataStart && ReadU32(data, off) != 0)
                chips.Add(name);

        Gd3 tags = Gd3.Empty;
        uint gd3Rel = ReadU32(data, 0x14);
        if (gd3Rel != 0)
        {
            long gd3Off = 0x14 + gd3Rel;
            tags = ParseGd3(data, gd3Off);
        }

        return new VgmFile
        {
            Version = version,
            TotalSamples = totalSamples,
            Chips = chips,
            Tags = tags,
        };
    }

    // The BCD version packs the major byte and minor byte as hex-equivalent
    // digits: 0x00000161 -> "1.61", 0x00000150 -> "1.50".
    private static string FormatBcdVersion(uint bcd)
    {
        int major = (int)((bcd >> 8) & 0xFF);
        int minor = (int)(bcd & 0xFF);
        return $"{major:X}.{minor:X2}";
    }

    private static Gd3 ParseGd3(byte[] data, long offset)
    {
        if (offset < 0 || offset + 12 > data.Length) return Gd3.Empty;
        int at = (int)offset;
        if (data[at] != 'G' || data[at + 1] != 'd' || data[at + 2] != '3' || data[at + 3] != ' ')
            return Gd3.Empty;

        // "Gd3 " + version u32 + length u32, then the UTF-16LE string sequence.
        uint length = ReadU32(data, at + 8);
        int start = at + 12;
        long end = Math.Min((long)start + length, data.Length);

        var fields = new List<string>();
        int p = start;
        while (fields.Count < 11 && p < end)
        {
            int s = p;
            while (p + 1 < end && !(data[p] == 0 && data[p + 1] == 0)) p += 2;
            fields.Add(Encoding.Unicode.GetString(data, s, p - s));
            p += 2;   // step over the UTF-16 NUL terminator
        }

        string Field(int i) => i < fields.Count ? fields[i] : "";

        // Order: track EN/JP, game EN/JP, system EN/JP, author EN/JP, date, vgm-by, notes.
        return new Gd3
        {
            TrackName = Field(0),
            GameName = Field(2),
            System = Field(4),
            Author = Field(6),
            Date = Field(8),
            Notes = Field(10),
        };
    }

    private static bool HasMagic(byte[] data) =>
        data.Length >= 4 && data[0] == 'V' && data[1] == 'g' && data[2] == 'm' && data[3] == ' ';

    private static uint ReadU32(byte[] data, int at)
    {
        if (at < 0 || at + 4 > data.Length) return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4));
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
