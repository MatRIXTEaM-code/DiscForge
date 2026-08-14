// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Cue;

/// <summary>One track line of a CU2 sheet, normalised to a plain absolute LBA (the +150 lead-in offset
/// CU2 bakes in has been removed), so it can be compared directly against a cue's INDEX positions.</summary>
public sealed record Cu2Track(int Number, long StartLba, long? PregapLba);

/// <summary>A parsed CU2 sheet, normalised to absolute LBAs.</summary>
public sealed record Cu2Sheet
{
    public required int NTracks { get; init; }
    public required long SizeLba { get; init; }
    public required long Data1Lba { get; init; }
    public required IReadOnlyList<Cu2Track> Tracks { get; init; }
    public required long TrkEndLba { get; init; }
}

/// <summary>The outcome of cross-checking a CU2 against the cue it should mirror.</summary>
public sealed record Cu2VerifyResult
{
    public required bool Match { get; init; }
    public required IReadOnlyList<string> Differences { get; init; }
    public string Summary() => Match
        ? "CU2 matches the cue's track map."
        : $"{Differences.Count} difference(s): {string.Join("; ", Differences)}.";
}

/// <summary>
/// cu2 — read, write and verify the Cybdyn CU2 track-map sidecar used by PSIO / xStation optical-drive
/// emulators. CU2 exists because cue sheets have too many dialects; it restates a disc's geometry as
/// absolute LBAs (with the +150-sector lead-in offset baked in). That makes it an excellent dialect-free
/// second account of the track layout to cross-check a cue against — and lets DiscForge emit one for the
/// PSIO/xStation ecosystem. Generation from a cue + its data file, parsing, and a cue↔CU2 verify. Format
/// description and conversion only; it moves no protected content and enables no circumvention.
/// </summary>
public static class Cu2
{
    /// <summary>The 150-sector (2-second) lead-in offset CU2 positions carry.</summary>
    public const int LeadInOffset = 150;

    /// <summary>Write a revision-2 CU2 for a cue and its total sector count (from the data file's length).</summary>
    public static string Write(CueSheet cue, long totalSectors)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var tracks = cue.Tracks.OrderBy(t => t.Number).ToList();
        if (tracks.Count == 0) throw new ArgumentException("Cue has no tracks.", nameof(cue));

        var sb = new StringBuilder();
        sb.Append($"ntracks {tracks.Count}\r\n");
        sb.Append(Line("size", totalSectors + LeadInOffset));
        sb.Append(Line("data1", Index01(tracks[0]) + LeadInOffset));

        for (int i = 1; i < tracks.Count; i++)
        {
            var t = tracks[i];
            long pregap = (Index00(t) ?? Index01(t)) + LeadInOffset;
            sb.Append(Line($"pregap{t.Number:00}", pregap));
            sb.Append(Line($"track{t.Number:00}", Index01(t) + LeadInOffset));
        }
        // The closing line is preceded by a blank line and carries no trailing newline.
        sb.Append($"\r\ntrk end   {Msf(totalSectors + LeadInOffset)}");
        return sb.ToString();
    }

    /// <summary>Parse a CU2 sheet, subtracting the lead-in offset so positions are plain absolute LBAs.</summary>
    public static Cu2Sheet Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int nTracks = 0;
        long size = 0, data1 = 0, trkEnd = 0;
        var trackStart = new Dictionary<int, long>();
        var trackPregap = new Dictionary<int, long>();

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("ntracks", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line[7..].Trim(), out nTracks);
            }
            else if (Field(line, "size", out long sz)) size = sz - LeadInOffset;
            else if (Field(line, "data1", out long d1)) data1 = d1 - LeadInOffset;
            else if (line.StartsWith("trk end", StringComparison.OrdinalIgnoreCase))
            {
                if (TryMsf(line[7..].Trim(), out long e)) trkEnd = e - LeadInOffset;
            }
            else if (line.StartsWith("pregap", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line.AsSpan(6, 2), out int n) && TryMsf(AfterLabel(line), out long p))
                    trackPregap[n] = p - LeadInOffset;
            }
            else if (line.StartsWith("track", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line.AsSpan(5, 2), out int n) && TryMsf(AfterLabel(line), out long s))
                    trackStart[n] = s - LeadInOffset;
            }
        }

        var tracks = new List<Cu2Track> { new(1, data1, null) };
        foreach (var n in trackStart.Keys.OrderBy(x => x))
            tracks.Add(new Cu2Track(n, trackStart[n], trackPregap.TryGetValue(n, out var pg) ? pg : null));

        return new Cu2Sheet
        {
            NTracks = nTracks,
            SizeLba = size,
            Data1Lba = data1,
            Tracks = tracks,
            TrkEndLba = trkEnd,
        };
    }

    /// <summary>Cross-check a CU2 against the cue it should mirror (track count, per-track start LBAs, size).</summary>
    public static Cu2VerifyResult Verify(CueSheet cue, long totalSectors, Cu2Sheet cu2)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(cu2);
        var diffs = new List<string>();
        var tracks = cue.Tracks.OrderBy(t => t.Number).ToList();

        if (cu2.NTracks != tracks.Count)
            diffs.Add($"track count {cu2.NTracks} vs cue {tracks.Count}");
        if (cu2.SizeLba != totalSectors)
            diffs.Add($"size {cu2.SizeLba} vs {totalSectors} sectors");
        if (tracks.Count > 0 && cu2.Data1Lba != Index01(tracks[0]))
            diffs.Add($"data1 at LBA {cu2.Data1Lba} vs cue track 1 at {Index01(tracks[0])}");

        foreach (var t in tracks.Skip(1))
        {
            var c = cu2.Tracks.FirstOrDefault(x => x.Number == t.Number);
            if (c is null) { diffs.Add($"track {t.Number} missing from CU2"); continue; }
            if (c.StartLba != Index01(t))
                diffs.Add($"track {t.Number} at LBA {c.StartLba} vs cue {Index01(t)}");
        }

        return new Cu2VerifyResult { Match = diffs.Count == 0, Differences = diffs };
    }

    // ---- helpers ------------------------------------------------------------

    private static long Index01(CueTrack t) =>
        (t.Indices.FirstOrDefault(i => i.Number == 1) ?? t.Indices.First()).Time.ToSectors();

    private static long? Index00(CueTrack t) =>
        t.Indices.FirstOrDefault(i => i.Number == 0)?.Time.ToSectors();

    private static string Line(string label, long lba) => $"{label,-10}{Msf(lba)}\r\n";

    private static string Msf(long sectors)
    {
        if (sectors < 0) sectors = 0;
        long m = sectors / (75 * 60), s = sectors / 75 % 60, f = sectors % 75;
        return $"{m:00}:{s:00}:{f:00}";
    }

    private static bool Field(string line, string label, out long lba)
    {
        lba = 0;
        if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return false;
        return TryMsf(AfterLabel(line), out lba);
    }

    private static string AfterLabel(string line)
    {
        int sp = line.IndexOf(' ');
        return sp < 0 ? "" : line[sp..].Trim();
    }

    private static bool TryMsf(string s, out long sectors)
    {
        sectors = 0;
        var parts = s.Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f)) return false;
        sectors = (m * 60L + sec) * 75 + f;
        return true;
    }
}
