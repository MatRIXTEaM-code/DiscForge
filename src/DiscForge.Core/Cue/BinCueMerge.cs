// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;

namespace DiscForge.Core.Cue;

/// <summary>
/// Merge a multi-track bin/cue set (one .bin per track — the shape Redump
/// distributes) into a single .bin with a rewritten .cue, and split it back
/// again. This is the job binmerge does, and it exists because many programs —
/// emulators, burners, older tools — only accept a single-file image, while the
/// canonical preservation set is one file per track.
///
/// The whole thing turns on one fact: in a one-file-per-track cue each track's
/// INDEX times are measured from the start of that track's own file, whereas in
/// a single-file cue they are absolute from the start of the disc. Merging is
/// therefore concatenating the track files and shifting every INDEX forward by
/// the number of sectors that precede its file; splitting is cutting the single
/// file at each track's first index and measuring the indices back from there.
///
/// Everything is raw 2352-byte sectors — that is what BINARY bin/cue always is,
/// data and audio alike — so a byte offset is exactly sector × 2352 and no
/// re-encoding of sector contents ever happens. The bytes are moved verbatim;
/// only the cue's arithmetic changes.
/// </summary>
public static class BinCueMerge
{
    /// <summary>Raw sector size for a BINARY bin/cue — data and audio alike.</summary>
    public const int RawSectorSize = 2352;

    public sealed record MergeResult(string CuePath, string BinPath, int Tracks, long Bytes);

    public sealed record SplitResult(string CuePath, IReadOnlyList<string> BinPaths, int Tracks);

    // ---- merge -------------------------------------------------------------

    /// <summary>
    /// Merge the bin files referenced by <paramref name="cuePath"/> into a single
    /// <paramref name="outBinPath"/>, writing a rewritten cue to
    /// <paramref name="outCuePath"/>. Files are concatenated in the order the
    /// tracks first reference them; INDEX times become absolute.
    /// </summary>
    public static MergeResult Merge(string cuePath, string outBinPath, string outCuePath,
                                    IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(cuePath);
        ArgumentNullException.ThrowIfNull(outBinPath);
        ArgumentNullException.ThrowIfNull(outCuePath);

        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        if (cue.Tracks.Count == 0)
            throw new InvalidDataException("The cue sheet has no tracks.");

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";

        // Distinct source files, in first-referenced order, with their sector
        // lengths. A single-file cue (already merged) is refused: there is
        // nothing to merge and the offsets would only be recomputed to
        // themselves.
        var order = new List<string>();
        var sectorsOf = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in cue.Tracks)
        {
            if (sectorsOf.ContainsKey(t.File)) continue;
            string full = Path.Combine(baseDir, t.File);
            if (!File.Exists(full))
                throw new FileNotFoundException($"Track file '{t.File}' referenced by the cue is missing.", full);
            long bytes = new FileInfo(full).Length;
            if (bytes % RawSectorSize != 0)
                throw new InvalidDataException(
                    $"'{t.File}' is {bytes:N0} bytes, not a whole number of {RawSectorSize}-byte " +
                    "sectors. Merge works on raw 2352-byte bin files; a 2048-byte ISO can't be merged.");
            order.Add(t.File);
            sectorsOf[t.File] = bytes / RawSectorSize;
        }

        if (order.Count < 2)
            throw new InvalidDataException(
                "The cue already references a single file — there is nothing to merge. " +
                "Use split to break a single-file image into per-track files.");

        // Sector offset at which each file's data begins in the merged bin.
        var fileStart = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long acc = 0;
        foreach (var f in order) { fileStart[f] = acc; acc += sectorsOf[f]; }
        long totalSectors = acc;

        // Concatenate the raw bytes in file order.
        long written = 0;
        long totalBytes = totalSectors * RawSectorSize;
        var buffer = new byte[1 << 20];
        using (var outBin = File.Create(outBinPath))
        {
            foreach (var f in order)
            {
                using var src = File.OpenRead(Path.Combine(baseDir, f));
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    outBin.Write(buffer, 0, n);
                    written += n;
                    progress?.Report(totalBytes == 0 ? 0 : written / (double)totalBytes);
                }
            }
        }

        string mergedName = Path.GetFileName(outBinPath);
        var merged = RewriteForMerge(cue, fileStart, mergedName);
        File.WriteAllText(outCuePath, merged.Write());

        return new MergeResult(outCuePath, outBinPath, cue.Tracks.Count, totalBytes);
    }

    /// <summary>
    /// Pure cue arithmetic for a merge: shift every INDEX of every track forward
    /// by the sector offset of the track's file, and point all tracks at a single
    /// merged file. Exposed for testing without touching the disk.
    /// </summary>
    public static CueSheet RewriteForMerge(CueSheet cue,
                                           IReadOnlyDictionary<string, long> fileStartSectors,
                                           string mergedFileName)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(fileStartSectors);

        var tracks = new List<CueTrack>(cue.Tracks.Count);
        foreach (var t in cue.Tracks)
        {
            long start = fileStartSectors.TryGetValue(t.File, out var s) ? s : 0;
            var indices = t.Indices
                .Select(i => new CueIndex(i.Number, Msf.FromSectors(i.Time.ToSectors() + start)))
                .ToList();
            tracks.Add(t with { File = mergedFileName, Indices = indices });
        }
        return cue with { Tracks = tracks };
    }

    // ---- split -------------------------------------------------------------

    /// <summary>
    /// Split the single bin referenced by <paramref name="cuePath"/> into one file
    /// per track, written into <paramref name="outDir"/> named
    /// "<paramref name="baseName"/> (Track N).bin", with a rewritten multi-file
    /// cue at <paramref name="outCuePath"/>.
    /// </summary>
    public static SplitResult Split(string cuePath, string outDir, string baseName,
                                    string outCuePath, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(cuePath);
        ArgumentNullException.ThrowIfNull(outDir);
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentNullException.ThrowIfNull(outCuePath);

        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        if (cue.Tracks.Count == 0)
            throw new InvalidDataException("The cue sheet has no tracks.");

        var files = cue.Tracks.Select(t => t.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count != 1)
            throw new InvalidDataException(
                $"Split expects a single-file image, but the cue references {files.Count} files. " +
                "It is already split.");

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        string binPath = Path.Combine(baseDir, files[0]);
        if (!File.Exists(binPath))
            throw new FileNotFoundException($"Bin file '{files[0]}' is missing.", binPath);

        long totalBytes = new FileInfo(binPath).Length;
        if (totalBytes % RawSectorSize != 0)
            throw new InvalidDataException(
                $"'{files[0]}' is {totalBytes:N0} bytes, not a whole number of {RawSectorSize}-byte sectors.");
        long totalSectors = totalBytes / RawSectorSize;

        // Each track starts at its lowest-numbered index (INDEX 00 pregap if
        // present, else INDEX 01) and runs to the next track's start.
        var starts = cue.Tracks.Select(TrackStartSector).ToList();
        Directory.CreateDirectory(outDir);

        var binPaths = new List<string>(cue.Tracks.Count);
        var buffer = new byte[1 << 20];
        long doneBytes = 0;

        using (var src = File.OpenRead(binPath))
        {
            for (int i = 0; i < cue.Tracks.Count; i++)
            {
                long startSec = starts[i];
                long endSec = i + 1 < cue.Tracks.Count ? starts[i + 1] : totalSectors;
                if (endSec < startSec)
                    throw new InvalidDataException(
                        $"Track {cue.Tracks[i].Number} starts after the next track — the cue indices are out of order.");

                string outName = $"{baseName} (Track {cue.Tracks[i].Number}).bin";
                string outPath = Path.Combine(outDir, outName);
                binPaths.Add(outName);

                long remaining = (endSec - startSec) * RawSectorSize;
                src.Seek(startSec * RawSectorSize, SeekOrigin.Begin);
                using var dst = File.Create(outPath);
                while (remaining > 0)
                {
                    int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (n <= 0)
                        throw new EndOfStreamException("The bin file is shorter than the cue describes.");
                    dst.Write(buffer, 0, n);
                    remaining -= n;
                    doneBytes += n;
                    progress?.Report(totalBytes == 0 ? 0 : doneBytes / (double)totalBytes);
                }
            }
        }

        var split = RewriteForSplit(cue, starts, binPaths);
        File.WriteAllText(outCuePath, split.Write());

        return new SplitResult(outCuePath, binPaths, cue.Tracks.Count);
    }

    /// <summary>
    /// Pure cue arithmetic for a split: give each track its own file and measure
    /// its indices back from the track's own start sector. Exposed for testing.
    /// </summary>
    public static CueSheet RewriteForSplit(CueSheet cue, IReadOnlyList<long> trackStartSectors,
                                           IReadOnlyList<string> perTrackFiles)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(trackStartSectors);
        ArgumentNullException.ThrowIfNull(perTrackFiles);
        if (trackStartSectors.Count != cue.Tracks.Count || perTrackFiles.Count != cue.Tracks.Count)
            throw new ArgumentException("Per-track arrays must match the track count.");

        var tracks = new List<CueTrack>(cue.Tracks.Count);
        for (int i = 0; i < cue.Tracks.Count; i++)
        {
            var t = cue.Tracks[i];
            long start = trackStartSectors[i];
            var indices = t.Indices
                .Select(idx => new CueIndex(idx.Number, Msf.FromSectors(idx.Time.ToSectors() - start)))
                .ToList();
            tracks.Add(t with { File = perTrackFiles[i], Indices = indices });
        }
        return cue with { Tracks = tracks };
    }

    /// <summary>The sector a track begins at: its lowest-numbered INDEX (00 pregap
    /// if present, else 01). A track with no indices is treated as starting at 0,
    /// which a validator would already have rejected.</summary>
    public static long TrackStartSector(CueTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Indices.Count == 0) return 0;
        return track.Indices.Min(i => i.Time.ToSectors());
    }
}
