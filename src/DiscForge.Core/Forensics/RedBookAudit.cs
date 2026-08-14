// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Cue;

namespace DiscForge.Core.Forensics;

/// <summary>One conformance finding against the Red Book (IEC 60908) CD structure.</summary>
public sealed record AuditFinding(LintSeverity Severity, string Where, string Message);

/// <summary>The result of auditing a disc's CD-layer structure.</summary>
public sealed record RedBookAuditReport
{
    public required IReadOnlyList<AuditFinding> Findings { get; init; }
    public int Errors => Findings.Count(f => f.Severity == LintSeverity.Error);
    public int Warnings => Findings.Count(f => f.Severity == LintSeverity.Warning);
    public bool Ok => Errors == 0;

    public string Summary() => Findings.Count == 0
        ? "Red Book: conformant — no structural issues."
        : $"Red Book: {Errors} error(s), {Warnings} warning(s).";
}

/// <summary>
/// redbook-audit — the physical-CD-layer sibling to image-lint. Where image-lint checks a filesystem
/// image against the ISO 9660 grammar, this holds a disc's TRACK structure up against the Red Book
/// (IEC 60908) rules for a Compact Disc and reports every deviation: the track count is 1–99 and the
/// numbers run sequentially from 1; every track carries an INDEX 01 (its true start); each track meets
/// the four-second (300-sector) minimum length and any pause/index-0 pregap is at least the two-second
/// (150-sector) minimum; the media catalogue number is 13 digits and every ISRC matches the
/// CC-XXX-YY-NNNNN grammar; and a data track (a mixed-mode or CD-Extra disc) sits where the standard
/// puts it — first track, or a later session — never wedged between audio tracks. It reads the structure
/// from a cue sheet (and, when given per-track sector counts, checks lengths too); it validates and
/// reports, and changes nothing on the disc.
/// </summary>
public static class RedBookAudit
{
    /// <summary>Red Book minimum track length: 4 seconds at 75 fps.</summary>
    public const int MinTrackSectors = 300;
    /// <summary>Red Book minimum pause/pregap: 2 seconds at 75 fps.</summary>
    public const int MinPregapSectors = 150;
    /// <summary>Highest legal track number on a CD.</summary>
    public const int MaxTracks = 99;

    /// <summary>Audit a disc's structure. <paramref name="trackSectors"/>, when supplied, gives the
    /// content length in sectors of each track in <paramref name="cue"/>'s order, enabling the
    /// minimum-track-length check; pass null to skip length checks.</summary>
    public static RedBookAuditReport Check(CueSheet cue, IReadOnlyList<int>? trackSectors = null)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var f = new List<AuditFinding>();
        var tracks = cue.Tracks;

        // ---- track count ----------------------------------------------------
        if (tracks.Count == 0)
        {
            f.Add(new(LintSeverity.Error, "disc", "no tracks — a CD must carry at least one."));
            return new RedBookAuditReport { Findings = f };
        }
        if (tracks.Count > MaxTracks)
            f.Add(new(LintSeverity.Error, "disc",
                $"{tracks.Count} tracks — a CD may hold at most {MaxTracks}."));

        if (trackSectors != null && trackSectors.Count != tracks.Count)
            f.Add(new(LintSeverity.Warning, "disc",
                $"{trackSectors.Count} track length(s) supplied for {tracks.Count} track(s) — lengths not checked."));
        bool haveLengths = trackSectors != null && trackSectors.Count == tracks.Count;

        // ---- per-track and sequencing --------------------------------------
        int expected = 1;
        bool sawAudio = false;
        int? dataAfterAudioAt = null;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            string where = $"track {t.Number}";

            // Sequential numbering from 1.
            if (t.Number != expected)
                f.Add(new(LintSeverity.Error, where,
                    $"out of sequence — expected track {expected}."));
            expected = t.Number + 1;

            if (t.Number is < 1 or > MaxTracks)
                f.Add(new(LintSeverity.Error, where, $"track number {t.Number} is outside 1–{MaxTracks}."));

            // INDEX 01 must exist — it marks the track's true start.
            if (!t.Indices.Any(ix => ix.Number == 1))
                f.Add(new(LintSeverity.Error, where, "no INDEX 01 — the track has no defined start."));

            // Indices: numbers 0–99, no duplicates, non-decreasing times.
            var seen = new HashSet<int>();
            CueIndex? prev = null;
            foreach (var ix in t.Indices)
            {
                if (ix.Number is < 0 or > MaxTracks)
                    f.Add(new(LintSeverity.Error, where, $"index number {ix.Number} is outside 0–{MaxTracks}."));
                if (!seen.Add(ix.Number))
                    f.Add(new(LintSeverity.Error, where, $"duplicate INDEX {ix.Number:D2}."));
                if (prev != null && ix.Time.ToSectors() < prev.Time.ToSectors())
                    f.Add(new(LintSeverity.Error, where,
                        $"INDEX {ix.Number:D2} at {ix.Time} precedes INDEX {prev.Number:D2} at {prev.Time}."));
                prev = ix;
            }

            // Pregap: an INDEX 00 or PREGAP that is shorter than the 2-second minimum.
            var idx0 = t.Indices.FirstOrDefault(ix => ix.Number == 0);
            var idx1 = t.Indices.FirstOrDefault(ix => ix.Number == 1);
            if (idx0 != null && idx1 != null)
            {
                long pause = idx1.Time.ToSectors() - idx0.Time.ToSectors();
                if (pause < MinPregapSectors)
                    f.Add(new(LintSeverity.Warning, where,
                        $"index-0 pause is {pause} sector(s) — Red Book minimum is {MinPregapSectors} (2 s)."));
            }
            if (t.Pregap is { } pg && pg.ToSectors() is var pgs && pgs < MinPregapSectors && pgs != 0)
                f.Add(new(LintSeverity.Warning, where,
                    $"declared pregap is {pgs} sector(s) — Red Book minimum is {MinPregapSectors} (2 s)."));

            // Minimum track length.
            if (haveLengths)
            {
                int len = trackSectors![i];
                if (len < MinTrackSectors)
                    f.Add(new(LintSeverity.Error, where,
                        $"length {len} sector(s) is below the {MinTrackSectors}-sector (4 s) Red Book minimum."));
            }

            // Data/audio ordering.
            bool isData = t.Type != CueTrackType.Audio;
            if (isData)
            {
                if (sawAudio && dataAfterAudioAt == null && t.Session == tracks[0].Session)
                    dataAfterAudioAt = t.Number;
            }
            else sawAudio = true;

            // ISRC grammar.
            if (t.Isrc != null && !IsValidIsrc(t.Isrc))
                f.Add(new(LintSeverity.Warning, where,
                    $"ISRC \"{t.Isrc}\" does not match the CC-XXX-YY-NNNNN (12-char) grammar."));
        }

        // A data track wedged between audio tracks within one session is not a legal
        // mixed-mode (data-first) or CD-Extra (data in a later session) layout.
        if (dataAfterAudioAt != null)
            f.Add(new(LintSeverity.Warning, $"track {dataAfterAudioAt}",
                "a data track follows audio within the same session — not a standard mixed-mode or CD-Extra layout."));

        // First track should begin with the standard 2-second pregap.
        var first = tracks[0];
        if (first.Type != CueTrackType.Audio)
        {
            // data track 1: fine (mixed mode / CD-ROM).
        }
        else if (first.Pregap == null && !first.Indices.Any(ix => ix.Number == 0)
                 && first.Indices.FirstOrDefault(ix => ix.Number == 1) is { } fi
                 && fi.Time.ToSectors() < MinPregapSectors)
        {
            f.Add(new(LintSeverity.Info, "track 1",
                "no explicit 2-second lead-in pregap before track 1 (burners generate it automatically)."));
        }

        // ---- disc-level MCN -------------------------------------------------
        if (cue.Catalog != null && !IsValidMcn(cue.Catalog))
            f.Add(new(LintSeverity.Warning, "disc",
                $"media catalogue number \"{cue.Catalog}\" is not 13 digits."));

        return new RedBookAuditReport { Findings = f };
    }

    public static string Render(RedBookAuditReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var x in r.Findings)
            sb.AppendLine($"  [{x.Severity}] {x.Where}: {x.Message}");
        return sb.ToString().TrimEnd();
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>MCN/EAN: exactly 13 decimal digits.</summary>
    public static bool IsValidMcn(string mcn)
        => mcn.Length == 13 && mcn.All(char.IsDigit);

    /// <summary>ISRC: 12 chars — 2 country letters, 3 alphanumeric registrant,
    /// 2-digit year, 5-digit designation (CC-XXX-YY-NNNNN, punctuation stripped).</summary>
    public static bool IsValidIsrc(string isrc)
    {
        if (isrc.Length != 12) return false;
        for (int i = 0; i < 2; i++) if (!char.IsAsciiLetter(isrc[i])) return false;
        for (int i = 2; i < 5; i++) if (!char.IsAsciiLetterOrDigit(isrc[i])) return false;
        for (int i = 5; i < 12; i++) if (!char.IsAsciiDigit(isrc[i])) return false;
        return true;
    }
}
