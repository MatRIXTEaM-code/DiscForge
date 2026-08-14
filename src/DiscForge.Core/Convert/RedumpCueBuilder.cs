// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Convert;

/// <summary>What happened to one track when a cue was made Redump-conformant.</summary>
public sealed record RedumpTrackReport
{
    public required int Track { get; init; }
    public required CueTrackType Type { get; init; }
    public required int PregapSectors { get; init; }
    /// <summary>The pregap the subchannel measured, before any --snap adjustment.</summary>
    public required int MeasuredPregapSectors { get; init; }
    public required bool Snapped { get; init; }
    public required long NewLengthSectors { get; init; }
    public string? Note { get; init; }
}

public sealed record RedumpCueResult
{
    public required string CueText { get; init; }
    public required IReadOnlyList<string> BinFilenames { get; init; }
    public required IReadOnlyList<RedumpTrackReport> Tracks { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public string Summary()
    {
        int gaps = Tracks.Count(t => t.PregapSectors > 0);
        int snapped = Tracks.Count(t => t.Snapped);
        string s = $"{Tracks.Count} track(s), {gaps} with a pregap";
        if (snapped > 0) s += $", {snapped} snapped to the 2-second convention";
        return s + ".";
    }
}

/// <summary>
/// Rebuilds a split bin/cue set so its track boundaries match what the disc's subchannel actually says —
/// the layout Redump uses. A CD-audio pregap is the gap that precedes a track; Redump's split convention puts
/// those bytes at the HEAD of the following track's file and marks them with INDEX 00 00:00:00 / INDEX 01
/// mm:ss:ff. A capture that instead cut each track at its INDEX 01 folds every gap into the tail of the
/// preceding file and writes a flat "INDEX 01 00:00:00" cue — playable, but not Redump's boundaries, so it will
/// not match a Redump checksum.
///
/// This re-cut is byte-preserving: the concatenated program area is identical before and after; only the
/// per-file split points move (from each track's INDEX 01 to its INDEX 00). No sector is invented or dropped —
/// which is exactly why it is a preservation operation and not a re-encode. It reads an already-captured
/// subchannel sidecar and defeats no protection.
///
/// Scope: the split-file Redump convention here is for all-2352 discs (PlayStation and other Red Book CDs).
/// A set with a cooked (2048/2336) data track is rejected rather than mis-cut.
/// </summary>
public static class RedumpCueBuilder
{
    /// <summary>A pregap this many sectors either side of 150 is treated as "the 2-second convention, off by a
    /// dropped/extra Q frame" and is eligible for --snap.</summary>
    private const int ConventionPregap = 150;
    private const int SnapTolerance = 2;

    /// <summary>
    /// Re-cut <paramref name="cuePath"/>'s bins at the subchannel's INDEX 00 boundaries and write a
    /// Redump-conformant cue + bins under <paramref name="outBaseName"/> in <paramref name="outDir"/>.
    /// </summary>
    /// <param name="snapPregap">When true, a measured pregap within two sectors of 150 is snapped to exactly
    /// 150 (the 2-second convention) and the cut point moved to match. Off by default: the measured value is
    /// authoritative and a deviation is reported rather than silently corrected.</param>
    public static RedumpCueResult Build(string cuePath, ReadOnlySpan<byte> subchannel, string outDir,
                                        string outBaseName, bool snapPregap = false,
                                        RawSubcodeForm? subForm = null)
    {
        ArgumentNullException.ThrowIfNull(cuePath);
        var cueDir = Path.GetDirectoryName(Path.GetFullPath(cuePath))!;
        var sheet = CueSheet.Parse(File.ReadAllText(cuePath));
        if (sheet.Tracks.Count == 0)
            throw new InvalidOperationException("The cue sheet has no tracks.");

        // This convention is defined for 2352-byte sectors (audio + raw data). A cooked data track has no
        // room for the sync/header a real pregap boundary needs, so refuse rather than mis-cut.
        const int SectorSize = 2352;
        foreach (var t in sheet.Tracks)
        {
            var (_, size) = CueSheet.TypeToToken(t.Type);
            if (size != SectorSize)
                throw new InvalidOperationException(
                    $"track {t.Number:D2} is {t.Type} ({size}-byte sectors). The Redump split convention is " +
                    "defined for 2352-byte discs (PlayStation / Red Book); re-image the data track as raw 2352 first.");
        }

        // The bins, in cue order, form one contiguous 2352-byte program area (LBA 0..N-1).
        var binOrder = new List<(string file, long sectors)>();
        string? last = null;
        long totalSectors = 0;
        foreach (var t in sheet.Tracks)
        {
            if (string.Equals(t.File, last, StringComparison.Ordinal)) continue;  // one FILE per track expected
            last = t.File;
            var path = Path.Combine(cueDir, t.File);
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new FileNotFoundException($"{t.File}: referenced by the cue but not found.", path);
            if (fi.Length % SectorSize != 0)
                throw new InvalidDataException($"{t.File}: length {fi.Length} is not a multiple of {SectorSize}.");
            long sec = fi.Length / SectorSize;
            binOrder.Add((path, sec));
            totalSectors += sec;
        }
        if (binOrder.Count != sheet.Tracks.Count)
            throw new InvalidOperationException(
                "This tool expects one FILE per track (a split bin/cue). A single-file image is already " +
                "unambiguous — its INDEX points are absolute — and needs no re-cut.");

        var map = SubchannelIndexMapper.Parse(subchannel, subForm);
        var warnings = new List<string>();
        if (map.Tracks.Count != sheet.Tracks.Count)
            warnings.Add($"the subchannel yielded {map.Tracks.Count} track(s) but the cue has " +
                         $"{sheet.Tracks.Count}; using the cue's track list and the subchannel's boundaries where they line up.");

        // Index the subchannel boundaries by track number.
        var byTrack = map.Tracks.ToDictionary(t => t.Track);

        // Boundary (absolute LBA) where each track's file should START under the Redump convention:
        //   its INDEX 00 (pregap) if it has one, else its INDEX 01 (body).
        long BoundaryOf(int trackNo, long fallback)
        {
            if (!byTrack.TryGetValue(trackNo, out var it)) return fallback;
            return it.Index00Lba ?? it.Index01Lba;
        }

        Directory.CreateDirectory(outDir);
        int n = sheet.Tracks.Count;

        // Pass 1 — decide every track's start boundary (with any --snap adjustment) and its body (INDEX 01).
        // The cut between two tracks is a SINGLE shared value: track i's end is track i+1's start. Computing all
        // starts first (rather than deriving each track's end independently) is what keeps the re-cut an exact
        // partition of the program area — no sector duplicated or dropped when a boundary is snapped.
        var starts = new long[n];
        var bodies = new long[n];
        var measuredGap = new int[n];
        var snapped = new bool[n];
        var notes = new string?[n];

        for (int i = 0; i < n; i++)
        {
            int trackNo = sheet.Tracks[i].Number;
            long rawStart = i == 0 ? 0 : BoundaryOf(trackNo, cumulativeStart(i));
            byTrack.TryGetValue(trackNo, out var it);
            long body = it?.Index01Lba ?? rawStart;
            int measured = (int)Math.Max(0, body - rawStart);
            measuredGap[i] = measured;
            bodies[i] = body;
            starts[i] = rawStart;

            if (i != 0 && measured != ConventionPregap &&
                Math.Abs(measured - ConventionPregap) <= SnapTolerance)
            {
                if (snapPregap)
                {
                    starts[i] = body - ConventionPregap;   // body (INDEX 01) fixed; move the cut to make gap = 150
                    snapped[i] = true;
                    notes[i] = $"measured {measured} → snapped to {ConventionPregap} (2-second convention)";
                }
                else
                {
                    notes[i] = $"measured {measured}, {(measured < ConventionPregap ? "short of" : "over")} " +
                               $"the {ConventionPregap}-sector (2-second) convention — likely a dropped/extra Q frame; " +
                               "re-capture or pass --snap-pregap to normalise";
                }
            }
        }
        starts[0] = 0;

        // A snapped start must not cross into the previous track's body (that would corrupt the earlier track).
        for (int i = 1; i < n; i++)
            if (starts[i] < starts[i - 1])
                throw new InvalidDataException(
                    $"track {sheet.Tracks[i].Number:D2}: its pregap boundary falls before the previous track's " +
                    "start — the subchannel and the bins disagree, so no cue was written.");

        // Pass 2 — cut each track [start, nextStart) and build the cue.
        var reports = new List<RedumpTrackReport>();
        var newTracks = new List<CueTrack>();
        var binNames = new List<string>();

        for (int i = 0; i < n; i++)
        {
            var ct = sheet.Tracks[i];
            int trackNo = ct.Number;
            long start = starts[i];
            long end = i == n - 1 ? totalSectors : starts[i + 1];
            int pregap = (int)Math.Max(0, bodies[i] - start);

            if (start < 0 || end < start || end > totalSectors)
                throw new InvalidDataException(
                    $"track {trackNo:D2}: computed a nonsensical range [{start}..{end}] against {totalSectors} " +
                    "total sectors — the subchannel and the bins disagree, so no cue was written.");

            string binName = $"{outBaseName}_track{trackNo:D2}.bin";
            binNames.Add(binName);
            using (var outBin = File.Create(Path.Combine(outDir, binName)))
                CopyRange(binOrder, start, end, SectorSize, outBin);

            var indices = pregap > 0
                ? new List<CueIndex> { new(0, Msf.FromSectors(0)), new(1, Msf.FromSectors(pregap)) }
                : new List<CueIndex> { new(1, Msf.FromSectors(0)) };

            newTracks.Add(new CueTrack
            {
                Number = trackNo, Type = ct.Type, File = binName,
                Flags = ct.Flags, Isrc = ct.Isrc, Indices = indices,
            });
            reports.Add(new RedumpTrackReport
            {
                Track = trackNo, Type = ct.Type, PregapSectors = pregap,
                MeasuredPregapSectors = measuredGap[i], Snapped = snapped[i],
                NewLengthSectors = end - start, Note = notes[i],
            });
        }

        var outSheet = new CueSheet { Tracks = newTracks, Catalog = sheet.Catalog };
        string cueText = outSheet.Write();
        File.WriteAllText(Path.Combine(outDir, $"{outBaseName}.cue"), cueText);

        return new RedumpCueResult
        {
            CueText = cueText, BinFilenames = binNames, Tracks = reports, Warnings = warnings,
        };

        // Running start of track i under the ORIGINAL split (INDEX-01 boundaries), used only as a fallback
        // when the subchannel has no frame for a track number.
        long cumulativeStart(int trackIndex)
        {
            long acc = 0;
            for (int k = 0; k < trackIndex && k < binOrder.Count; k++) acc += binOrder[k].sectors;
            return acc;
        }
    }

    /// <summary>Stream sectors [startSector, endSector) of the concatenated bins to <paramref name="dst"/>.</summary>
    private static void CopyRange(List<(string file, long sectors)> bins, long startSector, long endSector,
                                  int sectorSize, Stream dst)
    {
        long absSector = 0;
        var buffer = new byte[1 << 16];
        foreach (var (file, sectors) in bins)
        {
            long binStart = absSector;
            long binEnd = absSector + sectors;   // exclusive
            absSector = binEnd;

            long from = Math.Max(startSector, binStart);
            long to = Math.Min(endSector, binEnd);
            if (to <= from) continue;

            long byteFrom = (from - binStart) * sectorSize;
            long byteCount = (to - from) * sectorSize;
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                                           1 << 16, FileOptions.SequentialScan);
            src.Seek(byteFrom, SeekOrigin.Begin);
            while (byteCount > 0)
            {
                int want = (int)Math.Min(byteCount, buffer.Length);
                int n = src.Read(buffer, 0, want);
                if (n <= 0) throw new EndOfStreamException($"{Path.GetFileName(file)}: short read while re-cutting.");
                dst.Write(buffer, 0, n);
                byteCount -= n;
            }
        }
    }
}
