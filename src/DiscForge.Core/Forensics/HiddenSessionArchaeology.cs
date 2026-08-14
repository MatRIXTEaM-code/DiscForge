// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>A track placed on the disc: which session it belongs to, whether it is data,
/// its mode, and where it physically sits.</summary>
public readonly record struct SessionTrack(int Number, int Session, bool IsData, int? Mode, long StartLba, long LengthSectors)
{
    public long EndLba => StartLba + LengthSectors - 1;
}

/// <summary>What a track carries, before LBAs are assigned — the input to <see cref="HiddenSessionArchaeology.Place"/>.</summary>
public readonly record struct SessionTrackInput(int Number, int Session, bool IsData, int? Mode, long LengthSectors);

public enum SessionKind : byte { Empty = 0, Audio = 1, Data = 2, Mixed = 3 }

/// <summary>One session on the disc, summarised.</summary>
public sealed record DiscSession
{
    public required int Number { get; init; }
    public required IReadOnlyList<int> Tracks { get; init; }
    public required SessionKind Kind { get; init; }
    public required long StartLba { get; init; }
    public required long EndLba { get; init; }
    public required int? DataMode { get; init; }
    public required long DataSectors { get; init; }
    /// <summary>A data session a naive audio rip / single-session read would skip.</summary>
    public required bool Hidden { get; init; }

    public long Sectors => EndLba - StartLba + 1;
    public bool CarriesData => Kind is SessionKind.Data or SessionKind.Mixed;
}

/// <summary>The session map of a disc, with the hidden data sessions called out.</summary>
public sealed record SessionArchaeologyReport
{
    public required IReadOnlyList<DiscSession> Sessions { get; init; }
    public required IReadOnlyList<string> Findings { get; init; }
    /// <summary>Gap (lead-out + lead-in) between each session and the next, in sectors.</summary>
    public required IReadOnlyList<long> InterSessionGaps { get; init; }

    public bool Multisession => Sessions.Count > 1;
    public bool HasHiddenData => Sessions.Any(s => s.Hidden);

    public string Summary()
    {
        if (Sessions.Count == 0) return "No tracks — nothing to map.";
        int hidden = Sessions.Count(s => s.Hidden);
        if (!Multisession)
            return "Single-session disc — no hidden sessions.";
        return HasHiddenData
            ? $"Multisession disc: {Sessions.Count} sessions, {hidden} hidden data session(s) a naive rip would drop."
            : $"Multisession disc: {Sessions.Count} sessions; no hidden data sessions.";
    }
}

/// <summary>
/// Hidden-session archaeology — surface the data sessions a normal player or a plain audio rip
/// never sees. The classic case is the "Enhanced CD / CD Extra" layout: session 1 is ordinary
/// audio tracks, and a second session tucked behind them carries a data track (bonus content, a
/// PC installer, videos) that an audio ripper stops short of and a single-session read never
/// reaches. This maps every session, classifies each (audio / data / mixed), measures the
/// lead-out/lead-in gaps between them, and flags every data session that isn't the first as
/// hidden — the thing to extract and preserve rather than discard. Detection and mapping only;
/// it reads the layout and reports, and defeats nothing.
/// </summary>
public static class HiddenSessionArchaeology
{
    /// <summary>The standard multisession gap: session lead-out (6750) + next lead-in (4500).
    /// A real disc's inter-session gap is close to this; it is what a single-session read skips.</summary>
    public const long StandardMultisessionGap = 11250;

    public static SessionArchaeologyReport Analyze(IReadOnlyList<SessionTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        if (tracks.Count == 0)
            return new SessionArchaeologyReport
            {
                Sessions = Array.Empty<DiscSession>(),
                Findings = Array.Empty<string>(),
                InterSessionGaps = Array.Empty<long>(),
            };

        var bySession = tracks
            .GroupBy(t => t.Session)
            .OrderBy(g => g.Key)
            .ToList();

        int firstSession = bySession[0].Key;
        var sessions = new List<DiscSession>();

        foreach (var g in bySession)
        {
            var list = g.OrderBy(t => t.StartLba).ToList();
            bool hasData = list.Any(t => t.IsData);
            bool hasAudio = list.Any(t => !t.IsData);
            var kind = (hasData, hasAudio) switch
            {
                (true, true) => SessionKind.Mixed,
                (true, false) => SessionKind.Data,
                (false, true) => SessionKind.Audio,
                _ => SessionKind.Empty,
            };
            int? dataMode = list.Where(t => t.IsData).Select(t => t.Mode).FirstOrDefault(m => m is not null);
            long dataSectors = list.Where(t => t.IsData).Sum(t => t.LengthSectors);

            // A data session that is not the earliest session is what a naive rip skips.
            bool hidden = hasData && g.Key > firstSession;

            sessions.Add(new DiscSession
            {
                Number = g.Key,
                Tracks = list.Select(t => t.Number).ToList(),
                Kind = kind,
                StartLba = list.Min(t => t.StartLba),
                EndLba = list.Max(t => t.EndLba),
                DataMode = dataMode,
                DataSectors = dataSectors,
                Hidden = hidden,
            });
        }

        var gaps = new List<long>();
        for (int i = 1; i < sessions.Count; i++)
            gaps.Add(sessions[i].StartLba - (sessions[i - 1].EndLba + 1));

        var findings = new List<string>();
        bool firstIsAudio = sessions[0].Kind == SessionKind.Audio;
        foreach (var s in sessions.Where(s => s.Hidden))
        {
            string mode = s.DataMode is int m ? $"Mode {m}" : "data";
            bool cdExtra = firstIsAudio && sessions[0].Number == firstSession;
            string layout = cdExtra
                ? "This is the classic Enhanced CD / CD Extra layout — an audio ripper stops at session 1 and never sees it."
                : "A single-session read stops before it.";
            findings.Add(
                $"Session {s.Number} carries a {mode} data area (LBA {s.StartLba:N0}–{s.EndLba:N0}, {s.DataSectors:N0} sectors) " +
                $"behind session {firstSession}. {layout} Extract session {s.Number} explicitly and preserve it.");
        }

        int dataSessionCount = sessions.Count(s => s.CarriesData);
        if (dataSessionCount > 1)
            findings.Add($"{dataSessionCount} separate data sessions are present — preserve each; tools that mount only " +
                         "the first (or only the last) will silently drop the others.");

        return new SessionArchaeologyReport
        {
            Sessions = sessions,
            Findings = findings,
            InterSessionGaps = gaps,
        };
    }

    /// <summary>Lay a session-tagged track list onto a disc: tracks placed back-to-back within a
    /// session, with <paramref name="interSessionGap"/> sectors of lead-out/lead-in inserted at each
    /// session boundary. Tracks must be supplied in disc order.</summary>
    public static IReadOnlyList<SessionTrack> Place(IReadOnlyList<SessionTrackInput> tracks,
                                                    long interSessionGap = StandardMultisessionGap)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (interSessionGap < 0) throw new ArgumentOutOfRangeException(nameof(interSessionGap));

        var placed = new List<SessionTrack>(tracks.Count);
        long lba = 0;
        int? prevSession = null;
        foreach (var t in tracks)
        {
            if (t.LengthSectors < 0) throw new ArgumentOutOfRangeException(nameof(tracks), "Track length cannot be negative.");
            if (prevSession is int ps && t.Session != ps) lba += interSessionGap;
            placed.Add(new SessionTrack(t.Number, t.Session, t.IsData, t.Mode, lba, t.LengthSectors));
            lba += t.LengthSectors;
            prevSession = t.Session;
        }
        return placed;
    }

    public static string Render(SessionArchaeologyReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var s in r.Sessions)
        {
            string tracks = string.Join(",", s.Tracks);
            string mode = s.DataMode is int m ? $" Mode {m}" : "";
            string hidden = s.Hidden ? "  ← HIDDEN" : "";
            sb.AppendLine($"  Session {s.Number}: {s.Kind}{mode} · tracks {tracks} · LBA {s.StartLba:N0}–{s.EndLba:N0} " +
                          $"({s.Sectors:N0} sectors){hidden}");
        }
        for (int i = 0; i < r.InterSessionGaps.Count; i++)
            sb.AppendLine($"  gap after session {r.Sessions[i].Number}: {r.InterSessionGaps[i]:N0} sectors (lead-out + lead-in)");
        foreach (var f in r.Findings)
            sb.AppendLine($"  ! {f}");
        return sb.ToString().TrimEnd();
    }
}
