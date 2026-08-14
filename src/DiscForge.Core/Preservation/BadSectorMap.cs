// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscForge.Core.Preservation;

/// <summary>A contiguous run of unreadable sectors (inclusive LBAs). One dropout is a run of length 1.</summary>
public sealed record BadSectorRun(long StartLba, long EndLba)
{
    public long Count => EndLba - StartLba + 1;
    public override string ToString() => Count == 1 ? $"{StartLba}" : $"{StartLba}-{EndLba} (×{Count})";
}

/// <summary>Where a source disc's unreadable sectors landed inside one output track file after a conversion.</summary>
public sealed record TrackBadSectors
{
    public required int Track { get; init; }
    public required string File { get; init; }
    /// <summary>Unreadable sectors as offsets WITHIN this track's file (0 = the file's first sector).</summary>
    public required IReadOnlyList<long> WithinFileLba { get; init; }
    /// <summary>Unreadable sectors that fell in this track's pregap — real holes, but not present in the
    /// content file (the pregap is not written), so they have no within-file offset.</summary>
    public int InPregap { get; init; }
}

/// <summary>
/// The map of sectors a drive could not read when a disc was captured — the one thing a checksum can never
/// tell you, because a zero-filled hole hashes just as happily as real data. A dump with unreadable sectors is
/// not complete, and this record is what carries that fact forward: from the capture, through a bin/cue
/// conversion (where each absolute LBA is re-expressed as an offset inside the track file it landed in), and
/// into the preservation master, so the master states the dump's true completeness instead of overstating it.
///
/// Absolute LBA is the canonical coordinate (what the drive reports). <see cref="RemapToTracks"/> produces the
/// per-file view for a split image. Boundary sectors — pregap/lead-out holes a drive's geometry causes rather
/// than disc damage — are tracked as a subset so genuine damage can be told apart from harmless padding holes.
/// This records what could not be read; it recovers nothing and defeats nothing.
/// </summary>
public sealed record BadSectorMap
{
    /// <summary>On-disk format tag of the sidecar.</summary>
    public string FormatVersion => "dbs/1";

    /// <summary>The image these LBAs refer to (the capture, before any conversion).</summary>
    public required string Image { get; init; }
    public required int TotalSectors { get; init; }

    /// <summary>Every unreadable sector, as an absolute disc LBA.</summary>
    public required IReadOnlyList<long> UnreadableLba { get; init; }

    /// <summary>The subset of <see cref="UnreadableLba"/> that sits against a track boundary — a drive
    /// geometry limit (pregap/run-out), not disc damage.</summary>
    public IReadOnlyList<long> BoundaryLba { get; init; } = Array.Empty<long>();

    public string? Note { get; init; }

    /// <summary>The per-file view, filled in by <see cref="RemapToTracks"/> after a conversion. Null on a raw map.</summary>
    public IReadOnlyList<TrackBadSectors>? ByTrack { get; init; }

    [JsonIgnore] public int Count => UnreadableLba.Count;
    [JsonIgnore] public int BoundaryCount => BoundaryLba.Count;

    /// <summary>Unreadable sectors that are NOT boundary holes — i.e. genuine damage.</summary>
    [JsonIgnore]
    public int DamageCount
    {
        get { var b = new HashSet<long>(BoundaryLba); return UnreadableLba.Count(l => !b.Contains(l)); }
    }

    [JsonIgnore] public bool DamagePresent => DamageCount > 0;
    [JsonIgnore] public bool Clean => UnreadableLba.Count == 0;

    /// <summary>Coalesce the unreadable LBAs into contiguous runs (sorted, de-duplicated).</summary>
    public IReadOnlyList<BadSectorRun> Runs()
    {
        var sorted = UnreadableLba.Distinct().OrderBy(x => x).ToList();
        var runs = new List<BadSectorRun>();
        if (sorted.Count == 0) return runs;
        long start = sorted[0], prev = sorted[0];
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == prev + 1) { prev = sorted[i]; continue; }
            runs.Add(new BadSectorRun(start, prev));
            start = prev = sorted[i];
        }
        runs.Add(new BadSectorRun(start, prev));
        return runs;
    }

    public string Summary()
    {
        if (Clean) return $"{Image}: no unreadable sectors — complete.";
        int runs = Runs().Count;
        string boundary = BoundaryCount > 0 ? $" ({BoundaryCount} at track boundaries)" : "";
        string verdict = DamagePresent ? "INCOMPLETE — genuine damage" : "holes only at track boundaries";
        return $"{Image}: {Count:N0} unreadable sector(s) in {runs:N0} run(s){boundary} — {verdict}.";
    }

    /// <summary>One track's absolute span for remapping: where its file begins on the disc and how it is laid out.</summary>
    public sealed record TrackSpan(int Track, string File, long StartLba, int PregapSectors, long LengthSectors);

    /// <summary>
    /// Re-express this (absolute-LBA) map against a split layout: each unreadable LBA becomes an offset inside
    /// the track file that holds it. A sector inside a track's pregap is counted (it is a real hole) but has no
    /// within-file offset, since the pregap is not written to the content file. The returned map keeps the same
    /// absolute truth and adds the per-file <see cref="ByTrack"/> view.
    /// </summary>
    public BadSectorMap RemapToTracks(IReadOnlyList<TrackSpan> tracks, string image)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var per = tracks.ToDictionary(t => t.Track, _ => new List<long>());
        var pregapHits = tracks.ToDictionary(t => t.Track, _ => 0);
        var boundary = new HashSet<long>(BoundaryLba);

        foreach (var lba in UnreadableLba)
        {
            var t = tracks.FirstOrDefault(x => lba >= x.StartLba && lba < x.StartLba + x.PregapSectors + x.LengthSectors);
            if (t is null) continue;   // outside every track (lead-in/out) — kept in the absolute list, not per-file
            long contentStart = t.StartLba + t.PregapSectors;
            if (lba < contentStart) pregapHits[t.Track]++;
            else per[t.Track].Add(lba - contentStart);
        }

        var byTrack = tracks
            .Where(t => per[t.Track].Count > 0 || pregapHits[t.Track] > 0)
            .Select(t => new TrackBadSectors
            {
                Track = t.Track, File = t.File,
                WithinFileLba = per[t.Track].OrderBy(x => x).ToList(),
                InPregap = pregapHits[t.Track],
            })
            .ToList();

        return this with { Image = image, ByTrack = byTrack };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));

    public static BadSectorMap Load(string path)
    {
        var map = JsonSerializer.Deserialize<BadSectorMap>(File.ReadAllText(path), JsonOpts)
                  ?? throw new InvalidDataException($"'{path}' is not a bad-sector map.");
        return map;
    }

    /// <summary>The conventional sidecar path for an image: <c>&lt;image&gt;.badsectors.json</c>.</summary>
    public static string SidecarPath(string imagePath) => imagePath + ".badsectors.json";
}
