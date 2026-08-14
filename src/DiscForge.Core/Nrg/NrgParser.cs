// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.Nrg;

/// <summary>
/// Reads a Nero NRG image. The container puts the track data at the front of the
/// file and a chunk-based table of contents at the back, found through a footer
/// at the very end:
///
///   NRG v2 footer (12 bytes at EOF): "NER5" + 64-bit big-endian offset to the
///   first chunk. (v1 uses "NERO" + a 32-bit offset.)
///
///   Chunks (from that offset, until "END!"): a 4-byte ASCII tag, a 32-bit
///   big-endian length, then the payload. The two that matter:
///     CUEX — the cue table: 8-byte entries carrying each track's start LBA.
///     DAOX — Disc-At-Once info: the authoritative track table, with each track's
///            sector size, mode, and byte offsets into the file's data region.
///
/// This reads NRG v2 (NER5). Its structure follows the public format
/// description; it is validated by round-tripping DiscForge's own writer.
/// Validation against images produced by Nero itself is the next step, exactly
/// as the CDI reader still awaits a real DiscJuggler descriptor.
/// </summary>
public static class NrgParser
{
    private const int DaoxTrackEntrySize = 42;   // v2 track entry
    private const int DaoiTrackEntrySize = 30;   // v1 track entry
    private const int DaoxHeaderSize = 22;        // u32 size + 14 UPC + 4 fields

    /// <summary>True if the stream carries an NRG v2 (or v1) footer.</summary>
    public static bool IsNrg(Stream image)
    {
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            if (image.Length < 12) return false;
            var tail = ReadAt(image, image.Length - 12, 12);
            if (NrgFormat.Match(tail, 0, NrgFormat.FooterV2)) return true;
            var tail8 = ReadAt(image, image.Length - 8, 8);
            return NrgFormat.Match(tail8, 0, NrgFormat.FooterV1);
        }
        catch { return false; }
    }

    public static NrgImage Parse(Stream image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanSeek)
            throw new ArgumentException("Reading NRG requires a seekable stream.", nameof(image));
        if (image.Length < 12)
            throw new NrgFormatException("Too short to be an NRG image.");

        bool v2;
        long chunkOffset;
        var tail = ReadAt(image, image.Length - 12, 12);
        if (NrgFormat.Match(tail, 0, NrgFormat.FooterV2))
        {
            v2 = true;
            chunkOffset = (long)NrgFormat.ReadU64Be(tail.AsSpan(4, 8));
        }
        else
        {
            var tail8 = ReadAt(image, image.Length - 8, 8);
            if (!NrgFormat.Match(tail8, 0, NrgFormat.FooterV1))
                throw new NrgFormatException(
                    "No Nero footer at the end of the file — the last bytes are neither \"NER5\" nor " +
                    "\"NERO\". This is not an NRG image.");
            v2 = false;
            chunkOffset = NrgFormat.ReadU32Be(tail8.AsSpan(4, 4));
        }

        // Read the chunk region. v2 carries CUEX/DAOX, v1 carries CUES/DAOI.
        byte[]? cue = null, dao = null;
        bool cueV2 = v2, daoV2 = v2;
        long p = chunkOffset;
        while (p + 8 <= image.Length)
        {
            var header = ReadAt(image, p, 8);
            uint size = NrgFormat.ReadU32Be(header.AsSpan(4, 4));
            if (NrgFormat.Match(header, 0, NrgFormat.TagEnd)) break;

            var payload = ReadAt(image, p + 8, (int)size);
            if (NrgFormat.Match(header, 0, NrgFormat.TagCuex)) { cue = payload; cueV2 = true; }
            else if (NrgFormat.Match(header, 0, NrgFormat.TagCues)) { cue = payload; cueV2 = false; }
            else if (NrgFormat.Match(header, 0, NrgFormat.TagDaox)) { dao = payload; daoV2 = true; }
            else if (NrgFormat.Match(header, 0, NrgFormat.TagDaoi)) { dao = payload; daoV2 = false; }

            p += 8 + size;
        }

        if (dao is null)
            throw new NrgFormatException("The NRG has no DAOX/DAOI chunk, so it has no track table.");

        var tracks = ParseDao(dao, daoV2, cue, cueV2);
        return new NrgImage { IsV2 = v2, Tracks = tracks };
    }

    private static IReadOnlyList<NrgTrack> ParseDao(byte[] dao, bool v2, byte[]? cue, bool cueV2)
    {
        if (dao.Length < DaoxHeaderSize)
            throw new NrgFormatException("The DAO chunk is too short to hold its header.");

        int firstTrack = dao[20];
        int lastTrack = dao[21];
        int count = lastTrack - firstTrack + 1;
        if (count < 0 || count > 99)
            throw new NrgFormatException($"The DAO chunk declares {count} tracks (first {firstTrack}, last {lastTrack}).");

        int entrySize = v2 ? DaoxTrackEntrySize : DaoiTrackEntrySize;
        var lbaByTrack = ParseCueLbas(cue, cueV2);

        var tracks = new List<NrgTrack>();
        int at = DaoxHeaderSize;
        for (int i = 0; i < count; i++)
        {
            if (at + entrySize > dao.Length)
                throw new NrgFormatException("The DAO chunk ends inside a track entry.");

            var e = dao.AsSpan(at, entrySize);
            int sectorSize = BinaryPrimitives.ReadUInt16BigEndian(e.Slice(12, 2));
            byte modeCode = e[14];
            long index1Offset, endOffset;
            if (v2)
            {
                index1Offset = (long)NrgFormat.ReadU64Be(e.Slice(26, 8));
                endOffset = (long)NrgFormat.ReadU64Be(e.Slice(34, 8));
            }
            else
            {
                index1Offset = NrgFormat.ReadU32Be(e.Slice(22, 4));
                endOffset = NrgFormat.ReadU32Be(e.Slice(26, 4));
            }

            var (mode, canonicalSize) = NrgFormat.FromModeCode(modeCode);
            if (sectorSize <= 0) sectorSize = canonicalSize;

            long dataBytes = endOffset - index1Offset;
            if (dataBytes < 0 || dataBytes % sectorSize != 0)
                throw new NrgFormatException(
                    $"Track {firstTrack + i}: data span {dataBytes} is not a whole number of " +
                    $"{sectorSize}-byte sectors.");

            int number = firstTrack + i;
            tracks.Add(new NrgTrack
            {
                Number = number,
                Mode = mode,
                SectorSize = sectorSize,
                StartLba = lbaByTrack.TryGetValue(number, out long lba) ? lba : 0,
                LengthSectors = (uint)(dataBytes / sectorSize),
                DataOffset = index1Offset,
            });
            at += entrySize;
        }
        return tracks;
    }

    private static Dictionary<int, long> ParseCueLbas(byte[]? cue, bool v2)
    {
        var map = new Dictionary<int, long>();
        if (cue is null) return map;

        for (int at = 0; at + 8 <= cue.Length; at += 8)
        {
            int index = cue[at + 2];
            if (index != 1) continue;

            if (v2)
            {
                // CUEX: [ctrl][track binary][index][0][i32 BE lba].
                int track = cue[at + 1];
                if (track is <= 0 or >= 0xAA) continue;
                map[track] = NrgFormat.ReadI32Be(cue.AsSpan(at + 4, 4));
            }
            else
            {
                // CUES: [ctrl][track BCD][index][0][0][min][sec][frame] (BCD MSF).
                int track = FromBcd(cue[at + 1]);
                if (track is <= 0 or >= 0xAA) continue;
                int abs = FromBcd(cue[at + 5]) * 75 * 60 + FromBcd(cue[at + 6]) * 75 + FromBcd(cue[at + 7]);
                map[track] = abs - 150;
            }
        }
        return map;
    }

    private static int FromBcd(byte b) => (b >> 4) * 10 + (b & 0x0F);

    private static byte[] ReadAt(Stream s, long offset, int length)
    {
        if (length < 0) length = 0;
        if (offset < 0 || offset + length > s.Length)
            throw new NrgFormatException(
                $"The NRG is truncated: a structure at {offset:N0}+{length} lies past the end " +
                $"({s.Length:N0} bytes).");
        var buf = new byte[length];
        s.Seek(offset, SeekOrigin.Begin);
        s.ReadExactly(buf, 0, length);
        return buf;
    }
}
