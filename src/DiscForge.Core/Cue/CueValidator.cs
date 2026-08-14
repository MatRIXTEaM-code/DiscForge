// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Cue;

public enum CueIssueLevel { Info, Warning, Error }

/// <summary>One thing worth saying about a cuesheet.</summary>
public sealed record CueIssue(CueIssueLevel Level, int? Track, string Message)
{
    public override string ToString() =>
        (Track is not null ? $"Track {Track}: " : "") + Message;
}

public sealed record CueValidation
{
    public required IReadOnlyList<CueIssue> Issues { get; init; }
    /// <summary>Data files the sheet refers to, and whether each was found.</summary>
    public required IReadOnlyDictionary<string, long> FileSizes { get; init; }

    public bool HasErrors => Issues.Any(i => i.Level == CueIssueLevel.Error);
    public bool HasWarnings => Issues.Any(i => i.Level == CueIssueLevel.Warning);
    public bool Clean => !HasErrors && !HasWarnings;
}

/// <summary>
/// Checks a cuesheet against the data files it describes.
///
/// A cuesheet is a set of claims about a BIN: this track starts here, runs this
/// long, holds this kind of sector. Nothing in the text checks those claims, so
/// a sheet can be perfectly well-formed and still describe a disc that doesn't
/// match the bytes beside it — a truncated BIN, a track type that makes the
/// arithmetic wrong, an index past the end of the file. Burning from such a
/// sheet produces a coaster, and the failure arrives after the media is spent.
///
/// The checks here are the ones a text editor cannot make, because they need the
/// file: does the arithmetic reach the end of the BIN exactly, does every index
/// fall inside it, is the declared sector size consistent with the file's length.
/// </summary>
public static class CueValidator
{
    public static CueValidation Validate(CueSheet cue, string cueDirectory)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(cueDirectory);

        var issues = new List<CueIssue>();
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (cue.Tracks.Count == 0)
        {
            issues.Add(new CueIssue(CueIssueLevel.Error, null, "The sheet has no tracks."));
            return new CueValidation { Issues = issues, FileSizes = sizes };
        }

        // --- the files ------------------------------------------------------

        foreach (var name in cue.Tracks.Select(t => t.File).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.IsPathRooted(name) ? name : Path.Combine(cueDirectory, name);
            if (File.Exists(path))
            {
                sizes[name] = new FileInfo(path).Length;
            }
            else
            {
                sizes[name] = -1;
                issues.Add(new CueIssue(CueIssueLevel.Error, null,
                    $"'{name}' is not beside the sheet. A cuesheet describes a data file; " +
                    "without it there is nothing to burn or convert."));
            }
        }

        // --- track numbering ------------------------------------------------

        var numbers = cue.Tracks.Select(t => t.Number).ToList();
        if (numbers.Distinct().Count() != numbers.Count)
            issues.Add(new CueIssue(CueIssueLevel.Error, null,
                "Two tracks share a number. Every track must be numbered uniquely."));

        if (numbers.First() != 1)
            issues.Add(new CueIssue(CueIssueLevel.Warning, numbers.First(),
                $"Numbering starts at {numbers.First()} rather than 1. Legal, but most " +
                "software expects a disc to begin at track 1."));

        for (int i = 1; i < numbers.Count; i++)
            if (numbers[i] != numbers[i - 1] + 1)
                issues.Add(new CueIssue(CueIssueLevel.Warning, numbers[i],
                    $"Track numbers jump from {numbers[i - 1]} to {numbers[i]}. " +
                    "A gap in numbering is unusual and some players will not follow it."));

        // --- indexes --------------------------------------------------------

        foreach (var t in cue.Tracks)
        {
            var index1 = t.Indices.FirstOrDefault(x => x.Number == 1);
            if (index1 is null)
            {
                issues.Add(new CueIssue(CueIssueLevel.Error, t.Number,
                    "No INDEX 01. That index marks where the track's audio or data actually " +
                    "begins, and nothing can be laid out without it."));
                continue;
            }

            var index0 = t.Indices.FirstOrDefault(x => x.Number == 0);
            if (index0 is not null && index0.Time.ToSectors() > index1.Time.ToSectors())
                issues.Add(new CueIssue(CueIssueLevel.Error, t.Number,
                    $"INDEX 00 ({index0.Time}) is after INDEX 01 ({index1.Time}). The pregap " +
                    "cannot start after the track it precedes."));

            var ordered = t.Indices.OrderBy(x => x.Number).ToList();
            for (int i = 1; i < ordered.Count; i++)
                if (ordered[i].Time.ToSectors() <= ordered[i - 1].Time.ToSectors())
                    issues.Add(new CueIssue(CueIssueLevel.Error, t.Number,
                        $"INDEX {ordered[i].Number:D2} at {ordered[i].Time} is not after " +
                        $"INDEX {ordered[i - 1].Number:D2} at {ordered[i - 1].Time}. " +
                        "Indexes must ascend."));
        }

        // --- the arithmetic against the file --------------------------------

        foreach (var group in cue.Tracks.GroupBy(t => t.File, StringComparer.OrdinalIgnoreCase))
        {
            if (!sizes.TryGetValue(group.Key, out long size) || size < 0) continue;

            var inFile = group.OrderBy(t => t.Number).ToList();
            int sectorSize = CueSheet.TypeToToken(inFile[0].Type).sectorSize;

            // Every track in one file should agree about sector size, or the
            // offsets can't all be right.
            foreach (var t in inFile)
            {
                int s = CueSheet.TypeToToken(t.Type).sectorSize;
                if (s != sectorSize)
                    issues.Add(new CueIssue(CueIssueLevel.Error, t.Number,
                        $"This track is {s}-byte sectors but track {inFile[0].Number} in the " +
                        $"same file is {sectorSize}. One file cannot hold both — every offset " +
                        "after the change would be wrong."));
            }

            if (size % sectorSize != 0)
                issues.Add(new CueIssue(CueIssueLevel.Warning, null,
                    $"'{group.Key}' is {size:N0} bytes, which is not a whole number of " +
                    $"{sectorSize}-byte sectors ({size / (double)sectorSize:N2}). The file may " +
                    "be truncated, or the track type may be wrong."));

            long fileSectors = size / sectorSize;

            // Every index must land inside the file.
            foreach (var t in inFile)
                foreach (var idx in t.Indices)
                    if (idx.Time.ToSectors() >= fileSectors)
                        issues.Add(new CueIssue(CueIssueLevel.Error, t.Number,
                            $"INDEX {idx.Number:D2} is at {idx.Time} (sector " +
                            $"{idx.Time.ToSectors():N0}) but '{group.Key}' holds only " +
                            $"{fileSectors:N0} sectors. The sheet describes more disc than the " +
                            "file contains."));

            // The last track should reach the end of the file, or something is
            // missing at one end or the other.
            var last = inFile[^1];
            var lastIndex = last.Indices.FirstOrDefault(x => x.Number == 1);
            if (lastIndex is not null)
            {
                long remaining = fileSectors - lastIndex.Time.ToSectors();
                if (remaining <= 0)
                    issues.Add(new CueIssue(CueIssueLevel.Error, last.Number,
                        "This track starts at or past the end of the file, so it has no content."));
                else if (remaining < 150)
                    issues.Add(new CueIssue(CueIssueLevel.Warning, last.Number,
                        $"Only {remaining} sector(s) — {remaining / 75.0:N1}s — remain after this " +
                        "track's start. That is very short for a final track; the file may be " +
                        "truncated."));
            }
        }

        // --- track-type sanity ----------------------------------------------

        foreach (var t in cue.Tracks)
        {
            if (t.Type == CueTrackType.Audio && t.Flags.HasFlag(CueFlags.Dcp))
                issues.Add(new CueIssue(CueIssueLevel.Info, t.Number,
                    "Flagged DCP (digital copy permitted)."));

            if (t.Type != CueTrackType.Audio &&
                (t.Flags.HasFlag(CueFlags.PreEmphasis) || t.Flags.HasFlag(CueFlags.FourChannel)))
                issues.Add(new CueIssue(CueIssueLevel.Warning, t.Number,
                    "Pre-emphasis and four-channel are audio flags; they mean nothing on a " +
                    "data track and some tools reject them."));

            if (t.Type == CueTrackType.Mode1_2048)
                issues.Add(new CueIssue(CueIssueLevel.Info, t.Number,
                    "MODE1/2048 is cooked data: sync, header and ECC are not in the file and " +
                    "must be regenerated when burning. MODE1/2352 preserves them as read."));

            if (t.Isrc is not null && t.Isrc.Length != 12)
                issues.Add(new CueIssue(CueIssueLevel.Warning, t.Number,
                    $"ISRC '{t.Isrc}' is {t.Isrc.Length} characters; the standard is 12."));
        }

        if (cue.Catalog is not null && cue.Catalog.Length != 13)
            issues.Add(new CueIssue(CueIssueLevel.Warning, null,
                $"CATALOG '{cue.Catalog}' is {cue.Catalog.Length} digits; an MCN is 13."));

        // --- pregap on track 1 ----------------------------------------------

        var first = cue.Tracks.OrderBy(t => t.Number).First();
        bool hasPregap = first.Pregap is not null ||
                         first.Indices.Any(x => x.Number == 0);
        if (!hasPregap && first.Type == CueTrackType.Audio)
            issues.Add(new CueIssue(CueIssueLevel.Info, first.Number,
                "No pregap declared on track 1. The Red Book requires two seconds before the " +
                "first track; burners normally add it, but declaring it makes the intent plain."));

        return new CueValidation { Issues = issues, FileSizes = sizes };
    }
}