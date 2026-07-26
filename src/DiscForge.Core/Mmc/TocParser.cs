// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.Mmc;

/// <summary>One entry from the disc's table of contents.</summary>
public sealed record TocTrack
{
    public required int Number { get; init; }
    /// <summary>Q sub-channel mode (1 = current position).</summary>
    public required byte Adr { get; init; }
    /// <summary>CONTROL nibble: bit 2 (0x04) marks a data track.</summary>
    public required byte Control { get; init; }
    public required uint StartLba { get; init; }
    /// <summary>Derived: runs to the next track's start, or the lead-out.</summary>
    public required uint LengthSectors { get; init; }

    public bool IsData => (Control & 0x04) != 0;
    public bool IsAudio => !IsData;
    /// <summary>Audio recorded with pre-emphasis (CONTROL bit 0).</summary>
    public bool PreEmphasis => (Control & 0x01) != 0;
    /// <summary>Digital copy permitted (CONTROL bit 1).</summary>
    public bool CopyPermitted => (Control & 0x02) != 0;
    /// <summary>Four-channel audio (CONTROL bit 3) — rare.</summary>
    public bool FourChannel => (Control & 0x08) != 0;
}

/// <summary>A parsed table of contents.</summary>
public sealed record DiscToc
{
    public required int FirstTrack { get; init; }
    public required int LastTrack { get; init; }
    public required uint LeadOutLba { get; init; }
    public required IReadOnlyList<TocTrack> Tracks { get; init; }

    public bool HasAudio => Tracks.Any(t => t.IsAudio);
    public bool HasData => Tracks.Any(t => t.IsData);
    /// <summary>Audio and data on one disc — the layout that needs RAW to write back.</summary>
    public bool IsMixedMode => HasAudio && HasData;
}

/// <summary>
/// Parses MMC READ TOC/PMA/ATIP (0x43) format 0 responses. Pure and fully
/// testable — the transport lives in DiscForge.Devices.
///
/// The TOC does not carry track lengths: each track runs to the start of the
/// next, and the last runs to the lead-out. Deriving that is this parser's real
/// job (validated in docs/reference/toc_parse.py).
/// </summary>
public static class TocParser
{
    /// <summary>Track number reserved for the lead-out descriptor.</summary>
    public const int LeadOutTrackNumber = 0xAA;

    public static DiscToc Parse(ReadOnlySpan<byte> response)
    {
        if (response.Length < 4)
            throw new InvalidDataException("TOC response is too short to contain a header.");

        // Data Length counts everything after the length field itself.
        int dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[..2]);
        int total = dataLength + 2;
        if (total > response.Length)
            throw new InvalidDataException(
                $"TOC response truncated: header declares {total} bytes, got {response.Length}.");
        if (dataLength < 2)
            throw new InvalidDataException("TOC response declares no descriptors.");

        int firstTrack = response[2];
        int lastTrack = response[3];
        int count = (dataLength - 2) / 8;

        uint? leadOut = null;
        var raw = new List<(int Number, byte Adr, byte Control, uint Lba)>();

        for (int i = 0; i < count; i++)
        {
            int off = 4 + i * 8;
            if (off + 8 > response.Length) break;

            byte b1 = response[off + 1];
            byte adr = (byte)((b1 >> 4) & 0x0F);
            byte control = (byte)(b1 & 0x0F);
            int number = response[off + 2];
            uint lba = BinaryPrimitives.ReadUInt32BigEndian(response.Slice(off + 4, 4));

            if (number == LeadOutTrackNumber) leadOut = lba;
            else raw.Add((number, adr, control, lba));
        }

        if (leadOut is not { } leadOutLba)
            throw new InvalidDataException("TOC response has no lead-out descriptor.");

        raw.Sort((a, b) => a.Number.CompareTo(b.Number));

        var tracks = new List<TocTrack>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            uint end = i + 1 < raw.Count ? raw[i + 1].Lba : leadOutLba;
            if (end < raw[i].Lba)
                throw new InvalidDataException(
                    $"TOC track {raw[i].Number} ends ({end}) before it starts ({raw[i].Lba}).");

            tracks.Add(new TocTrack
            {
                Number = raw[i].Number,
                Adr = raw[i].Adr,
                Control = raw[i].Control,
                StartLba = raw[i].Lba,
                LengthSectors = end - raw[i].Lba,
            });
        }

        return new DiscToc
        {
            FirstTrack = firstTrack,
            LastTrack = lastTrack,
            LeadOutLba = leadOutLba,
            Tracks = tracks,
        };
    }
}
