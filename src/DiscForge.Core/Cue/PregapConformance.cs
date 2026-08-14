// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Cue;

/// <summary>Per-track pregap finding: the measured gap and whether it sits on a data/audio boundary.</summary>
public sealed record PregapTrackReport
{
    public required int Number { get; init; }
    public required string Type { get; init; }
    public required bool IsAudio { get; init; }
    public required long? Index00Sectors { get; init; }
    public required long Index01Sectors { get; init; }
    /// <summary>The pregap length in sectors: INDEX 01 − INDEX 00 when an INDEX 00 is present, else the PREGAP command, else 0.</summary>
    public required long GapSectors { get; init; }
    public required bool CrossesDataAudioBoundary { get; init; }
    /// <summary>A human-readable problem with this track's gap, or null when it conforms.</summary>
    public string? Issue { get; init; }
}

/// <summary>The outcome of auditing a cue's pregaps against Red Book / Redump conventions.</summary>
public sealed record PregapReport
{
    public required int TrackCount { get; init; }
    public required bool Conformant { get; init; }
    public required IReadOnlyList<PregapTrackReport> Tracks { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }

    public string Summary() => Conformant
        ? $"Pregaps conform: {TrackCount} track(s), the data/audio boundary carries the standard 2-second (150-sector) pregap."
        : $"{Issues.Count} pregap issue(s): {string.Join("; ", Issues)}.";
}

/// <summary>
/// pregap-check — audit a cue's track pregaps against the conventions a PlayStation (and any mixed-mode CD)
/// dump is expected to follow. Track 1 begins at 00:00:00; the first audio track after the data track carries
/// a two-second (150-sector) pregap; no INDEX 00 sits after its INDEX 01 (a negative gap); and track numbers
/// run 1..N without a break. It measures each track's gap from its own INDEX 00/01 (robust to single- or
/// per-track FILE layouts) and reports deviations. Read-only conformance analysis — it changes nothing and
/// touches no protection.
/// </summary>
public static class PregapConformance
{
    /// <summary>The customary pregap at a data/audio boundary: two seconds = 150 sectors.</summary>
    public const int StandardPregapSectors = 150;

    public static PregapReport Check(CueSheet cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var tracks = cue.Tracks.OrderBy(t => t.Number).ToList();
        var reports = new List<PregapTrackReport>();
        var issues = new List<string>();

        if (tracks.Count == 0)
        {
            return new PregapReport { TrackCount = 0, Conformant = false, Tracks = reports, Issues = new[] { "cue has no tracks" } };
        }

        // Track numbers should run 1..N without a gap.
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Number != i + 1)
            {
                issues.Add($"track numbering is not sequential (expected {i + 1}, found {tracks[i].Number})");
                break;
            }
        }

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            bool isAudio = t.Type == CueTrackType.Audio;
            string typeName = CueSheet.TypeToToken(t.Type).token;

            long? idx00 = Index00(t);
            long? idx01 = Index01(t);
            string? issue = null;

            if (idx01 is null)
            {
                issue = $"track {t.Number} has no INDEX 01";
                issues.Add(issue);
                reports.Add(new PregapTrackReport
                {
                    Number = t.Number, Type = typeName, IsAudio = isAudio,
                    Index00Sectors = idx00, Index01Sectors = 0, GapSectors = 0,
                    CrossesDataAudioBoundary = false, Issue = issue,
                });
                continue;
            }

            long gap = idx00 is not null ? idx01.Value - idx00.Value : (t.Pregap?.ToSectors() ?? 0);
            bool boundary = i > 0 && (tracks[i - 1].Type == CueTrackType.Audio) != isAudio;

            if (i == 0)
            {
                if (idx01.Value != 0)
                    issue = $"track 1 starts at LBA {idx01.Value}, not 00:00:00";
                else if (idx00 is not null && idx00.Value != 0)
                    issue = "track 1 carries an INDEX 00 before 00:00:00";
            }
            else if (gap < 0)
            {
                issue = $"track {t.Number} has a negative pregap (INDEX 00 sits after INDEX 01)";
            }
            else if (boundary && gap != StandardPregapSectors)
            {
                issue = gap == 0
                    ? $"track {t.Number} crosses the data/audio boundary but has no 2-second pregap"
                    : $"track {t.Number} boundary pregap is {gap} sectors, not the standard {StandardPregapSectors}";
            }

            if (issue is not null) issues.Add(issue);
            reports.Add(new PregapTrackReport
            {
                Number = t.Number, Type = typeName, IsAudio = isAudio,
                Index00Sectors = idx00, Index01Sectors = idx01.Value, GapSectors = gap,
                CrossesDataAudioBoundary = boundary, Issue = issue,
            });
        }

        return new PregapReport
        {
            TrackCount = tracks.Count,
            Conformant = issues.Count == 0,
            Tracks = reports,
            Issues = issues,
        };
    }

    private static long? Index01(CueTrack t) =>
        t.Indices.FirstOrDefault(i => i.Number == 1)?.Time.ToSectors();

    private static long? Index00(CueTrack t) =>
        t.Indices.FirstOrDefault(i => i.Number == 0)?.Time.ToSectors();
}
