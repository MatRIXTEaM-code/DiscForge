// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Util;

namespace DiscForge.Core.Cdi;

public sealed record TrackDiff(int TrackNumber, string Field, string ValueA, string ValueB);

public sealed record CompareReport
{
    public required bool Equal { get; init; }
    /// <summary>Top-level structural differences (version, counts).</summary>
    public required IReadOnlyList<string> StructuralDifferences { get; init; }
    /// <summary>Per-track metadata differences.</summary>
    public required IReadOnlyList<TrackDiff> TrackDifferences { get; init; }
    /// <summary>Track numbers whose stored content CRC-32 differs.</summary>
    public required IReadOnlyList<int> ContentMismatchTracks { get; init; }
}

/// <summary>
/// Compares two CDI images: structure (version, sessions, per-track metadata)
/// and, optionally, content (per-track stored CRC-32). Useful for confirming a
/// copy, a deterministic rebuild, or that a burn source matches. Pure over the
/// parsed models plus the two streams.
/// </summary>
public static class CdiComparer
{
    public static CompareReport Compare(Stream a, CdiImage ia, Stream b, CdiImage ib,
                                        bool compareContent = true)
    {
        var structural = new List<string>();
        var trackDiffs = new List<TrackDiff>();
        var contentMismatch = new List<int>();

        if (ia.Version != ib.Version)
            structural.Add($"version: {ia.Version} vs {ib.Version}");
        if (ia.Sessions.Count != ib.Sessions.Count)
            structural.Add($"sessions: {ia.Sessions.Count} vs {ib.Sessions.Count}");
        if (ia.TrackCount != ib.TrackCount)
            structural.Add($"tracks: {ia.TrackCount} vs {ib.TrackCount}");

        var at = ia.AllTracks.ToList();
        var bt = ib.AllTracks.ToList();
        int common = Math.Min(at.Count, bt.Count);

        for (int i = 0; i < common; i++)
        {
            var x = at[i];
            var y = bt[i];
            int n = x.Number;

            void Cmp(string field, object va, object vb)
            {
                if (!Equals(va, vb))
                    trackDiffs.Add(new TrackDiff(n, field, va.ToString() ?? "", vb.ToString() ?? ""));
            }

            Cmp("mode", x.Mode, y.Mode);
            Cmp("sectorSize", (int)x.SectorSize, (int)y.SectorSize);
            Cmp("pregap", x.PregapSectors, y.PregapSectors);
            Cmp("length", x.LengthSectors, y.LengthSectors);
            Cmp("startLba", x.StartLba, y.StartLba);
            Cmp("session", x.SessionIndex, y.SessionIndex);

            if (compareContent)
            {
                // Only worth CRC-comparing when the stored sizes match; a size
                // difference is already a structural diff and CRC would be moot.
                if (x.StoredByteLength == y.StoredByteLength)
                {
                    if (StoredCrc(a, x) != StoredCrc(b, y))
                        contentMismatch.Add(n);
                }
                else
                {
                    trackDiffs.Add(new TrackDiff(n, "storedBytes",
                        x.StoredByteLength.ToString(), y.StoredByteLength.ToString()));
                }
            }
        }

        bool equal = structural.Count == 0 && trackDiffs.Count == 0 && contentMismatch.Count == 0;
        return new CompareReport
        {
            Equal = equal,
            StructuralDifferences = structural,
            TrackDifferences = trackDiffs,
            ContentMismatchTracks = contentMismatch,
        };
    }

    private static uint StoredCrc(Stream cdi, CdiTrack track)
    {
        cdi.Seek(track.FileOffset, SeekOrigin.Begin);
        var crc = new Crc32();
        long remaining = track.StoredByteLength;
        var buf = new byte[64 * 1024];
        while (remaining > 0)
        {
            int n = (int)Math.Min(remaining, buf.Length);
            cdi.ReadExactly(buf, 0, n);
            crc.Update(buf.AsSpan(0, n));
            remaining -= n;
        }
        return crc.Value;
    }
}
