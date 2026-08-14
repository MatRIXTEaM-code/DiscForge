// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Floppy;

/// <summary>One captured revolution's flux metadata within an SCP track.</summary>
public sealed record ScpRevolution(uint IndexDurationTicks, uint FluxCount, uint DataOffset);

/// <summary>One track of an SCP flux capture (metadata + revolution table).</summary>
public sealed record ScpTrack(int TrackNumber, long HeaderOffset, IReadOnlyList<ScpRevolution> Revolutions);

/// <summary>The SCP file header.</summary>
public sealed record ScpHeader
{
    public required int VersionMajor { get; init; }
    public required int VersionMinor { get; init; }
    public required int DiskType { get; init; }
    public required int Revolutions { get; init; }
    public required int StartTrack { get; init; }
    public required int EndTrack { get; init; }
    public required byte Flags { get; init; }
    public required int BitCellWidth { get; init; }     // 0 = 16-bit
    public required int Heads { get; init; }            // 0 both, 1 side0, 2 side1
    /// <summary>Nanoseconds per flux tick (25 ns base × (resolution+1)).</summary>
    public required int TickNs { get; init; }

    public bool Indexed => (Flags & 0x01) != 0;
    public bool Is96Tpi => (Flags & 0x02) != 0;
    public bool Is360Rpm => (Flags & 0x04) != 0;
    public bool Extended => (Flags & 0x40) != 0;
    public bool HasFooter => (Flags & 0x20) != 0;
}

/// <summary>A parsed SuperCard Pro (SCP) flux image.</summary>
public sealed record ScpImage
{
    public required ScpHeader Header { get; init; }
    public required IReadOnlyList<ScpTrack> Tracks { get; init; }
    public required bool ChecksumValid { get; init; }

    /// <summary>Approximate disc RPM inferred from the first revolution's index duration.</summary>
    public double? Rpm
    {
        get
        {
            var rev = Tracks.SelectMany(t => t.Revolutions).FirstOrDefault(r => r.IndexDurationTicks > 0);
            if (rev is null) return null;
            double revSeconds = rev.IndexDurationTicks * (double)Header.TickNs / 1_000_000_000.0;
            return revSeconds > 0 ? 60.0 / revSeconds : null;
        }
    }
}

/// <summary>
/// Reads a SuperCard Pro (SCP) flux image — the raw magnetic-flux timing capture the
/// preservation community uses for floppies. This parses the header, the 168-entry track
/// offset table and each present track's revolution metadata, validates the file checksum,
/// and can decode a track's flux transitions to nanosecond intervals (honouring the 0x0000
/// overflow convention). Clean-room, from the public SCP image specification. Pure flux
/// preservation — no protection concerns.
/// </summary>
public static class ScpReader
{
    public const int MaxTracks = 168;

    public static bool IsScp(ReadOnlySpan<byte> head)
        => head.Length >= 3 && head[0] == 'S' && head[1] == 'C' && head[2] == 'P';

    public static ScpImage Read(Stream s)
    {
        s.Position = 0;
        var all = new byte[s.Length];
        s.ReadExactly(all, 0, all.Length);
        return Parse(all);
    }

    public static ScpImage Parse(byte[] all)
    {
        if (all.Length < 0x10 || !IsScp(all))
            throw new InvalidDataException("Not an SCP image (missing 'SCP' signature).");

        var header = new ScpHeader
        {
            VersionMajor = all[3] >> 4,
            VersionMinor = all[3] & 0x0F,
            DiskType = all[4],
            Revolutions = all[5],
            StartTrack = all[6],
            EndTrack = all[7],
            Flags = all[8],
            BitCellWidth = all[9],
            Heads = all[10],
            TickNs = 25 * (all[11] + 1),
        };

        uint storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(0x0C, 4));
        uint sum = 0;
        for (int i = 0x10; i < all.Length; i++) sum = unchecked(sum + all[i]);
        bool checksumValid = sum == storedChecksum;

        int tableStart = header.Extended ? 0x80 : 0x10;
        var tracks = new List<ScpTrack>();
        for (int t = 0; t < MaxTracks; t++)
        {
            int entry = tableStart + t * 4;
            if (entry + 4 > all.Length) break;
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(entry, 4));
            if (off == 0 || off + 4 > all.Length) continue;              // absent track

            // Track Data Header: "TRK" + track number, then one 3-longword block per revolution.
            if (all[off] != 'T' || all[off + 1] != 'R' || all[off + 2] != 'K') continue;
            int trackNo = all[off + 3];
            var revs = new List<ScpRevolution>();
            for (int r = 0; r < header.Revolutions; r++)
            {
                long rp = off + 4 + (long)r * 12;
                if (rp + 12 > all.Length) break;
                revs.Add(new ScpRevolution(
                    BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((int)rp, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((int)rp + 4, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((int)rp + 8, 4))));
            }
            tracks.Add(new ScpTrack(trackNo, off, revs));
        }

        return new ScpImage { Header = header, Tracks = tracks, ChecksumValid = checksumValid };
    }

    /// <summary>
    /// Decode a track revolution's flux transitions to nanosecond intervals. Flux values are
    /// 16-bit big-endian tick counts; a 0x0000 word is an overflow that adds 65536 ticks to
    /// the next non-zero value (no interval is emitted for the overflow word itself).
    /// </summary>
    public static long[] ReadFluxNs(byte[] all, ScpImage img, ScpTrack track, int revolution)
    {
        if (revolution < 0 || revolution >= track.Revolutions.Count)
            throw new ArgumentOutOfRangeException(nameof(revolution));
        var rev = track.Revolutions[revolution];
        long dataStart = track.HeaderOffset + rev.DataOffset;
        var intervals = new List<long>((int)rev.FluxCount);
        long acc = 0;
        long p = dataStart;
        for (uint i = 0; i < rev.FluxCount; i++)
        {
            if (p + 2 > all.Length) break;
            int v = (all[p] << 8) | all[p + 1];               // big-endian
            p += 2;
            if (v == 0) { acc += 0x10000; continue; }         // overflow
            intervals.Add((acc + v) * (long)img.Header.TickNs);
            acc = 0;
        }
        return intervals.ToArray();
    }
}
