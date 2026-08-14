// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

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
    /// <summary>Derived: runs to the next track's start in the SAME session, or that
    /// session's lead-out.</summary>
    public required uint LengthSectors { get; init; }
    /// <summary>Which session this track belongs to (1 for a single-session disc).</summary>
    public int SessionNumber { get; init; } = 1;

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

    /// <summary>Number of sessions on the disc (1 unless read from the Full TOC).</summary>
    public int SessionCount => Tracks.Count == 0 ? 1 : Tracks.Select(t => t.SessionNumber).Distinct().Count();
    /// <summary>A CD Extra / multi-session disc — the last track of each earlier
    /// session must stop at that session's lead-out, not run into the inter-session gap.</summary>
    public bool IsMultiSession => SessionCount > 1;
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

    /// <summary>POINT value marking a session's lead-out in the Full TOC.</summary>
    private const int PointLeadOut = 0xA2;

    /// <summary>
    /// Parse an MMC READ TOC/PMA/ATIP <b>Full TOC</b> (format 0010b) response. Unlike
    /// the plain TOC, this carries a session number on every entry and a lead-out
    /// address <i>per session</i> (POINT 0xA2), which is exactly what a multi-session
    /// disc (CD Extra / mixed-mode audio+data) needs: the last track of each earlier
    /// session is capped at that session's lead-out instead of running into the
    /// unreadable inter-session gap. Addresses arrive as MSF and are converted to LBA.
    /// </summary>
    public static DiscToc ParseFullToc(ReadOnlySpan<byte> response)
    {
        if (response.Length < 4)
            throw new InvalidDataException("Full TOC response is too short to contain a header.");

        int dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[..2]);
        int total = dataLength + 2;
        if (total > response.Length)
            throw new InvalidDataException($"Full TOC response truncated: header declares {total} bytes, got {response.Length}.");
        int count = (dataLength - 2) / 11;

        var raw = new List<(int Session, byte Adr, byte Control, int Number, uint Lba)>();
        var leadOuts = new Dictionary<int, uint>();

        for (int i = 0; i < count; i++)
        {
            int off = 4 + i * 11;
            if (off + 11 > response.Length) break;

            int session = response[off];
            byte b1 = response[off + 1];
            byte adr = (byte)((b1 >> 4) & 0x0F);
            byte control = (byte)(b1 & 0x0F);
            int point = response[off + 3];
            uint lba = MsfToLba(response[off + 8], response[off + 9], response[off + 10]);

            if (point is >= 1 and <= 99) raw.Add((session, adr, control, point, lba));
            else if (point == PointLeadOut) leadOuts[session] = lba;
            // POINT 0xA0/0xA1 (first/last track of session) are not needed here.
        }

        if (raw.Count == 0) throw new InvalidDataException("Full TOC response has no track descriptors.");
        if (leadOuts.Count == 0) throw new InvalidDataException("Full TOC response has no lead-out descriptor.");

        raw.Sort((a, b) => a.Number.CompareTo(b.Number));
        uint finalLeadOut = leadOuts.Values.Max();

        var tracks = new List<TocTrack>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var cur = raw[i];
            bool lastOfSession = i + 1 >= raw.Count || raw[i + 1].Session != cur.Session;
            uint end;
            if (lastOfSession)
                end = leadOuts.TryGetValue(cur.Session, out uint lo) ? lo
                    : (i + 1 < raw.Count ? raw[i + 1].Lba : finalLeadOut);
            else
                end = raw[i + 1].Lba;

            if (end < cur.Lba) end = cur.Lba;   // defensive: never negative length

            tracks.Add(new TocTrack
            {
                Number = cur.Number,
                Adr = cur.Adr,
                Control = cur.Control,
                StartLba = cur.Lba,
                LengthSectors = end - cur.Lba,
                SessionNumber = cur.Session,
            });
        }

        return new DiscToc
        {
            FirstTrack = raw[0].Number,
            LastTrack = raw[^1].Number,
            LeadOutLba = finalLeadOut,
            Tracks = tracks,
        };
    }

    /// <summary>MSF (minute/second/frame) → LBA. 75 frames/second; LBA 0 is at 00:02:00.</summary>
    private static uint MsfToLba(int min, int sec, int frame)
    {
        int lba = (min * 60 + sec) * 75 + frame - 150;
        return lba < 0 ? 0u : (uint)lba;
    }
}
