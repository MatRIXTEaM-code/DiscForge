// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.Audio;

/// <summary>
/// Parses the AccurateRip binary database record (the <c>dBAR-*.bin</c> blob the
/// AccurateRip service returns for a disc) into the <see cref="AccurateRip.DbEntry"/>
/// list that <see cref="AccurateRip.Verify"/> consumes. This turns AccurateRip
/// from "compute checksums to compare by hand" into an automatic pass/fail.
///
/// The blob is a sequence of <i>chunks</i>, one per pressing/submission the
/// database holds for that disc. Each chunk is:
/// <code>
///   u8   trackCount
///   u32  discId1        (AccurateRip id 1)
///   u32  discId2        (AccurateRip id 2)
///   u32  cddbId         (FreeDB/CDDB id)
///   trackCount × {
///       u8   confidence
///       u32  crc         (the AccurateRip checksum for that track)
///       u32  crc450      (offset-detection crc; retained, not required to match)
///   }
/// </code>
/// Multiple chunks are simply concatenated. All integers are little-endian.
///
/// Only the fetch of this blob needs the network (a plain HTTP GET the caller
/// performs); parsing and verifying are offline, so this is fully unit-testable.
/// AccurateRip data enables verification only — nothing here is protected or
/// circumvents anything.
/// </summary>
public static class AccurateRipDatabase
{
    /// <summary>One pressing/submission from the database, with its disc IDs.</summary>
    public sealed record Chunk
    {
        public required int TrackCount { get; init; }
        public required uint DiscId1 { get; init; }
        public required uint DiscId2 { get; init; }
        public required uint CddbId { get; init; }
        /// <summary>Per-track (confidence, crc). Index 0 = track 1.</summary>
        public required IReadOnlyList<(int Confidence, uint Crc)> Tracks { get; init; }
    }

    /// <summary>Parse every chunk from a dBAR blob. Returns an empty list for an
    /// empty blob; throws on a truncated/garbled chunk.</summary>
    public static IReadOnlyList<Chunk> Parse(ReadOnlySpan<byte> blob)
    {
        var chunks = new List<Chunk>();
        int pos = 0;

        while (pos < blob.Length)
        {
            // A chunk header is 13 bytes; if fewer remain, we're at trailing pad.
            if (pos + 13 > blob.Length) break;

            int trackCount = blob[pos];
            if (trackCount is <= 0 or > 99)
                throw new AccurateRipFormatException(
                    $"Implausible track count {trackCount} at offset {pos} — blob is not a dBAR record.");

            uint id1 = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(pos + 1, 4));
            uint id2 = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(pos + 5, 4));
            uint cddb = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(pos + 9, 4));
            pos += 13;

            int need = trackCount * 9;   // 9 bytes per track entry
            if (pos + need > blob.Length)
                throw new AccurateRipFormatException(
                    "dBAR record ends inside a track table — the blob is truncated.");

            var tracks = new List<(int, uint)>(trackCount);
            for (int t = 0; t < trackCount; t++)
            {
                int confidence = blob[pos];
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(pos + 1, 4));
                // bytes pos+5..pos+8 are crc450 (offset-detection); not needed here.
                pos += 9;
                tracks.Add((confidence, crc));
            }

            chunks.Add(new Chunk
            {
                TrackCount = trackCount, DiscId1 = id1, DiscId2 = id2, CddbId = cddb,
                Tracks = tracks,
            });
        }

        return chunks;
    }

    /// <summary>
    /// Convert parsed chunks into the <see cref="AccurateRip.DbEntry"/> list that
    /// <see cref="AccurateRip.Verify"/> expects — one entry per chunk, keeping
    /// each track's best (highest-confidence) CRC. Optionally filter to chunks
    /// whose disc IDs match the disc you computed, so a stray record for another
    /// pressing doesn't produce a false match.
    /// </summary>
    public static IReadOnlyList<AccurateRip.DbEntry> ToEntries(
        IReadOnlyList<Chunk> chunks,
        (uint Id1, uint Id2, uint CddbId)? filterDiscIds = null)
    {
        var entries = new List<AccurateRip.DbEntry>();
        foreach (var c in chunks)
        {
            if (filterDiscIds is { } f &&
                (c.DiscId1 != f.Id1 || c.DiscId2 != f.Id2 || c.CddbId != f.CddbId))
                continue;

            // A chunk stores one CRC per track already at the chunk's own
            // confidence; represent it as a DbEntry per track-confidence set.
            // We use the maximum per-track confidence as the entry confidence so
            // Verify reports the strongest available.
            int confidence = c.Tracks.Count == 0 ? 0 : c.Tracks.Max(t => t.Confidence);
            entries.Add(new AccurateRip.DbEntry
            {
                Confidence = confidence,
                TrackChecksums = c.Tracks.Select(t => t.Crc).ToArray(),
            });
        }
        return entries;
    }

    /// <summary>
    /// The canonical AccurateRip lookup URL for a disc, given its IDs and track
    /// count. The caller performs the HTTP GET (network is machine-side) and
    /// passes the returned bytes to <see cref="Parse"/>.
    /// </summary>
    public static string LookupUrl(int trackCount, uint id1, uint id2, uint cddbId)
    {
        // AccurateRip shards by the low nibbles of id1, as its service expects.
        char a = "0123456789abcdef"[(int)(id1 & 0xF)];
        char b = "0123456789abcdef"[(int)((id1 >> 4) & 0xF)];
        char c = "0123456789abcdef"[(int)((id1 >> 8) & 0xF)];
        return $"http://www.accuraterip.com/accuraterip/{a}/{b}/{c}/" +
               $"dBAR-{trackCount:000}-{id1:x8}-{id2:x8}-{cddbId:x8}.bin";
    }
}

public sealed class AccurateRipFormatException(string message) : Exception(message);
