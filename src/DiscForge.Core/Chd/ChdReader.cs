// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Chd;

public sealed class ChdFormatException(string message) : Exception(message);

/// <summary>One CD track described in a CHD's metadata.</summary>
public sealed record ChdTrack
{
    public required int Number { get; init; }
    /// <summary>e.g. MODE1, MODE2_RAW, AUDIO.</summary>
    public required string Type { get; init; }
    public required string SubType { get; init; }
    public required int Frames { get; init; }
    public int Pregap { get; init; }
    public int Postgap { get; init; }
}

/// <summary>What a CHD file declares about itself.</summary>
public sealed record ChdInfo
{
    public required int Version { get; init; }
    /// <summary>The four compression codecs (FourCC), e.g. "cdzl", "cdlz", "cdfl",
    /// or "none".</summary>
    public required IReadOnlyList<string> Compressors { get; init; }
    public required long LogicalBytes { get; init; }
    public required int HunkBytes { get; init; }
    public required int UnitBytes { get; init; }
    public required IReadOnlyList<ChdTrack> Tracks { get; init; }

    public bool IsCd => Tracks.Count > 0;
    public string Summary =>
        $"CHD v{Version}, codecs [{string.Join(", ", Compressors.Where(c => c != "none"))}], " +
        $"{LogicalBytes:N0} bytes" + (IsCd ? $", {Tracks.Count} CD track(s)" : "");
}

/// <summary>
/// Reads the structure of a CHD ("Compressed Hunks of Data") disc image — the
/// compressed format the emulation ecosystem (MAME, RetroArch, RomM libraries)
/// uses for CD/DVD/GD-ROM images. This identifies a CHD and reads its CD track
/// layout from the header and metadata, so DiscForge can inspect a CHD and report
/// what disc it holds.
///
/// Clean-room, from the public CHD v5 description:
///   Header (124 bytes, big-endian):
///     0x00  8  "MComprHD"
///     0x08  4  header length
///     0x0C  4  version (5)
///     0x10 16  four compressor FourCC codes
///     0x20  8  logical (uncompressed) byte size
///     0x28  8  hunk-map offset
///     0x30  8  first-metadata offset
///     0x38  4  bytes per hunk
///     0x3C  4  bytes per unit (2448 for CD = 2352 + 96 subcode)
///     0x40 60  raw / combined / parent SHA-1
///   Metadata is a linked list from the metadata offset: each entry is a 4-byte
///   tag, a byte of flags + 24-bit length, an 8-byte next-offset, then the payload.
///   CD track descriptors ("CHT2"/"CHTR") are ASCII "TRACK:n TYPE:.. SUBTYPE:..
///   FRAMES:n PREGAP:n .. POSTGAP:n".
///
/// Scope: this identifies a CHD and reads its CD track layout. Decompression to a
/// raw bin/cue lives in <see cref="ChdExtractor"/>, which handles all three CD
/// codecs (cdzl/zlib, cdlz/LZMA, cdfl/FLAC), auto-detects the codec per hunk, and
/// verifies its output against the SHA-1 the CHD stores of itself. Nothing here is
/// protection-related.
/// </summary>
public static class ChdReader
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MComprHD");
    private const int V5HeaderLen = 124;

    public static bool IsChd(byte[] data) =>
        data.Length >= 16 && data.AsSpan(0, 8).SequenceEqual(Magic);

    public static ChdInfo Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsChd(data))
            throw new ChdFormatException("Missing the \"MComprHD\" signature — not a CHD file.");

        int version = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x0C));
        if (version != 5)
            throw new ChdFormatException(
                $"CHD v{version} is not supported yet — only v5 (the current format) is read. " +
                "Re-create the CHD with a current chdman, or convert it to BIN/CUE first.");
        if (data.Length < V5HeaderLen)
            throw new ChdFormatException("Truncated CHD v5 header.");

        var compressors = new List<string>(4);
        for (int i = 0; i < 4; i++)
            compressors.Add(FourCc(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x10 + i * 4))));

        long logicalBytes = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(0x20));
        long metaOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(0x30));
        int hunkBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x38));
        int unitBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x3C));

        var tracks = ReadTracks(data, metaOffset);

        return new ChdInfo
        {
            Version = version,
            Compressors = compressors,
            LogicalBytes = logicalBytes,
            HunkBytes = hunkBytes,
            UnitBytes = unitBytes,
            Tracks = tracks,
        };
    }

    // Walk the metadata linked list and parse CD track descriptors.
    private static List<ChdTrack> ReadTracks(byte[] data, long metaOffset)
    {
        var tracks = new List<ChdTrack>();
        long at = metaOffset;
        int guard = 0;
        while (at > 0 && at + 16 <= data.LongLength && ++guard < 10_000)
        {
            string tag = FourCc(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)at)));
            uint flagsLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)at + 4));
            int length = (int)(flagsLen & 0x00FF_FFFF);
            long next = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)at + 8));
            int dataAt = (int)at + 16;
            if (dataAt + length > data.Length) break;

            if (tag is "CHT2" or "CHTR")
            {
                string text = Encoding.ASCII.GetString(data, dataAt, length).TrimEnd('\0');
                var track = ParseTrack(text);
                if (track is not null) tracks.Add(track);
            }

            at = next;
        }
        tracks.Sort((a, b) => a.Number.CompareTo(b.Number));
        return tracks;
    }

    private static ChdTrack? ParseTrack(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int c = token.IndexOf(':');
            if (c > 0) fields[token[..c]] = token[(c + 1)..];
        }
        if (!fields.TryGetValue("TRACK", out var num) || !int.TryParse(num, out int number)) return null;

        return new ChdTrack
        {
            Number = number,
            Type = fields.GetValueOrDefault("TYPE", "?"),
            SubType = fields.GetValueOrDefault("SUBTYPE", "NONE"),
            Frames = int.TryParse(fields.GetValueOrDefault("FRAMES"), out int f) ? f : 0,
            Pregap = int.TryParse(fields.GetValueOrDefault("PREGAP"), out int pre) ? pre : 0,
            Postgap = int.TryParse(fields.GetValueOrDefault("POSTGAP"), out int post) ? post : 0,
        };
    }

    // A big-endian FourCC as text; all-zero reads as "none".
    private static string FourCc(uint v)
    {
        if (v == 0) return "none";
        Span<char> c = stackalloc char[4]
        {
            (char)((v >> 24) & 0xFF), (char)((v >> 16) & 0xFF), (char)((v >> 8) & 0xFF), (char)(v & 0xFF),
        };
        for (int i = 0; i < 4; i++) if (c[i] < 0x20 || c[i] > 0x7E) c[i] = '?';
        return new string(c);
    }
}
