// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Dat;
using DiscForge.Core.Files;
using DiscForge.Core.Preservation;

namespace DiscForge.Core.Redump;

/// <summary>How one expected file compares to what the dump actually holds.</summary>
public enum RomVerdict { Verified, SizeMismatch, ContentMismatch, Missing, Extra }

/// <summary>One expected-vs-actual file comparison, with the plain-language reasons it differs.</summary>
public sealed record RomDiff
{
    public required int? Track { get; init; }
    public required string Role { get; init; }            // "cue" | "data" | "audio"
    public required string? ExpectedName { get; init; }
    public required long? ExpectedSize { get; init; }
    public required string? ActualName { get; init; }
    public required long? ActualSize { get; init; }
    public required RomVerdict Verdict { get; init; }
    public IReadOnlyList<string> Explanations { get; init; } = Array.Empty<string>();
}

public sealed record RedumpDiffReport
{
    public required string Game { get; init; }
    public required bool Identified { get; init; }
    public required bool Match { get; init; }
    public required int Verified { get; init; }
    public required int Total { get; init; }
    public required IReadOnlyList<RomDiff> Roms { get; init; }
    public required IReadOnlyList<string> Recommendations { get; init; }

    public string Summary()
    {
        if (!Identified) return $"Could not identify this dump in the DAT — {Game}.";
        if (Match) return $"MATCH — {Game}: all {Total} file(s) verify against Redump.";
        return $"NO MATCH — {Game}: {Verified}/{Total} file(s) verify. See the per-file diagnosis.";
    }
}

/// <summary>
/// redump-diff — the answer to "why doesn't my dump match Redump?". Every other tool stops at a yes/no against
/// a DAT; this reconciles the dump against the catalogued entry and explains each divergence in preservation
/// terms a person can act on. A hash-only DAT can't reveal WHY two files of equal size differ, but the layout
/// can: when the tracks' total size matches but the per-track sizes don't, the split is wrong (the gaps sit in
/// the wrong file) and <c>redump-cue</c> fixes it; when one track is off by a whole number of sectors it was
/// padded or truncated; and an accompanying bad-sector map names the exact holes that keep a dump from ever
/// matching until it is re-read. Analysis only — it diagnoses, it changes nothing.
/// </summary>
public static class RedumpDiffer
{
    private const int CdSector = 2352;

    public static RedumpDiffReport Diff(string cuePath, DatFile dat, string? gameHint = null,
                                        BadSectorMap? badSectors = null)
    {
        ArgumentNullException.ThrowIfNull(cuePath);
        ArgumentNullException.ThrowIfNull(dat);
        string dir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";

        // The dump's actual member files, in cue order: the cue itself, then each distinct track bin.
        var sheet = CueSheet.Parse(File.ReadAllText(cuePath));
        var actual = new List<(int? track, string role, string name, long size, string sha1)>();
        AddActual(actual, Path.GetFileName(cuePath), cuePath, null, "cue");
        string? lastFile = null;
        foreach (var t in sheet.Tracks)
        {
            if (string.Equals(t.File, lastFile, StringComparison.Ordinal)) continue;
            lastFile = t.File;
            var full = Path.Combine(dir, t.File);
            string role = t.Type == CueTrackType.Audio ? "audio" : "data";
            AddActual(actual, t.File, full, t.Number, role);
        }

        // Pick the catalogued game: the one whose entries share the most SHA-1s with the dump, or the hint, or —
        // for a single-game reference DAT — the only game present.
        string? game = ResolveGame(dat, actual, gameHint);
        if (game is null)
        {
            return new RedumpDiffReport
            {
                Game = gameHint is null ? "no game matched by hash (pass --game to name one)" : $"'{gameHint}' not found",
                Identified = false, Match = false, Verified = 0, Total = actual.Count,
                Roms = Array.Empty<RomDiff>(), Recommendations = new[] { "Pass --game \"<exact DAT name>\" to pin the comparison." },
            };
        }

        var expected = dat.Roms.Where(r => string.Equals(r.Game, game, StringComparison.Ordinal)).ToList();
        var expBins = expected.Where(r => !r.Name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)).ToList();
        var actBins = actual.Where(a => a.role != "cue").ToList();

        // Layout-level facts that let a hash-only DAT explain content differences.
        long expBinTotal = expBins.Sum(r => r.Size);
        long actBinTotal = actBins.Sum(a => a.size);
        bool sameTotal = expBinTotal == actBinTotal;
        int sizeMismatches = 0;

        var roms = new List<RomDiff>();
        int verified = 0;

        // cue file
        var cueAct = actual.First(a => a.role == "cue");
        var cueExp = expected.FirstOrDefault(r => r.Name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase));
        roms.Add(CompareOne(null, "cue", cueExp, cueAct.name, cueAct.size, cueAct.sha1, dat, badSectors,
                            sameTotal, ref verified, ref sizeMismatches));

        // bins aligned by order (track order)
        for (int i = 0; i < Math.Max(expBins.Count, actBins.Count); i++)
        {
            var e = i < expBins.Count ? expBins[i] : null;
            var a = i < actBins.Count ? actBins[i] : (default);
            bool hasA = i < actBins.Count;
            roms.Add(CompareOne(hasA ? a.track : null, hasA ? a.role : "audio", e,
                                hasA ? a.name : null, hasA ? a.size : (long?)null, hasA ? a.sha1 : null,
                                dat, badSectors, sameTotal, ref verified, ref sizeMismatches));
        }

        bool match = roms.All(r => r.Verdict == RomVerdict.Verified);
        var recs = BuildRecommendations(roms, sameTotal, sizeMismatches, badSectors);

        return new RedumpDiffReport
        {
            Game = game, Identified = true, Match = match,
            Verified = verified, Total = roms.Count, Roms = roms, Recommendations = recs,
        };
    }

    private static RomDiff CompareOne(int? track, string role, DatRom? expected, string? actName, long? actSize,
                                      string? actSha1, DatFile dat, BadSectorMap? bad, bool sameTotal,
                                      ref int verified, ref int sizeMismatches)
    {
        var ex = new List<string>();

        if (expected is null && actName is not null)
            return new RomDiff { Track = track, Role = role, ExpectedName = null, ExpectedSize = null,
                ActualName = actName, ActualSize = actSize, Verdict = RomVerdict.Extra,
                Explanations = new[] { "the dump has a file the catalogued entry does not — an extra track or a naming mismatch." } };

        if (expected is not null && actName is null)
            return new RomDiff { Track = track, Role = role, ExpectedName = expected.Name, ExpectedSize = expected.Size,
                ActualName = null, ActualSize = null, Verdict = RomVerdict.Missing,
                Explanations = new[] { "the catalogued entry lists this file but the dump does not contain it." } };

        // Both present.
        bool sizeOk = actSize == expected!.Size;
        bool hashOk = expected.Sha1 is not null && actSha1 is not null &&
                      string.Equals(expected.Sha1, actSha1, StringComparison.OrdinalIgnoreCase);

        if (sizeOk && hashOk) { verified++; return new RomDiff { Track = track, Role = role,
            ExpectedName = expected.Name, ExpectedSize = expected.Size, ActualName = actName, ActualSize = actSize,
            Verdict = RomVerdict.Verified, Explanations = Array.Empty<string>() }; }

        if (!sizeOk)
        {
            sizeMismatches++;
            long delta = (actSize ?? 0) - expected.Size;
            if (role != "cue" && delta % CdSector == 0)
            {
                long sec = Math.Abs(delta) / CdSector;
                ex.Add(delta > 0
                    ? $"{sec} sector(s) LONGER than Redump ({delta:+#;-#;0} bytes) — trailing padding, an over-read, or a mis-cut pregap folded in."
                    : $"{sec} sector(s) SHORTER than Redump ({delta:+#;-#;0} bytes) — a truncated read or a pregap that belongs in this file cut into the previous one.");
                if (sameTotal) ex.Add("the tracks' TOTAL size still matches Redump, so no data is missing — only the split is wrong; re-cut with redump-cue.");
            }
            else ex.Add($"size differs by {delta:+#;-#;0} byte(s) — not a whole number of sectors, so this is padding/format rather than a track boundary.");
            AddBadSectorNote(ex, track, bad);
            return new RomDiff { Track = track, Role = role, ExpectedName = expected.Name, ExpectedSize = expected.Size,
                ActualName = actName, ActualSize = actSize, Verdict = RomVerdict.SizeMismatch, Explanations = ex };
        }

        // Same size, different content.
        bool badHere = AddBadSectorNote(ex, track, bad);
        if (!badHere)
        {
            if (role == "data")
                ex.Add("same size, different content in the DATA track — a region/version/build difference or a silently bad sector, not a pregap issue.");
            else if (role == "audio")
                ex.Add("same size, different content in an AUDIO track — most often a read-offset error; verify the drive's offset (AccurateRip) and re-rip, or a single damaged sample.");
            else
                ex.Add("same size, different content — the cue text differs (track paths, REM lines, or line endings); Redump cues are exact.");
            if (sameTotal && role != "cue")
                ex.Add("total track size matches Redump — if several tracks differ together, the split/pregap placement is the likely cause (redump-cue).");
        }
        return new RomDiff { Track = track, Role = role, ExpectedName = expected.Name, ExpectedSize = expected.Size,
            ActualName = actName, ActualSize = actSize, Verdict = RomVerdict.ContentMismatch, Explanations = ex };
    }

    private static bool AddBadSectorNote(List<string> ex, int? track, BadSectorMap? bad)
    {
        if (bad?.ByTrack is null || track is null) return false;
        var t = bad.ByTrack.FirstOrDefault(x => x.Track == track);
        if (t is null || (t.WithinFileLba.Count == 0 && t.InPregap == 0)) return false;
        string where = t.WithinFileLba.Count > 0
            ? $"at within-file LBA {string.Join(", ", t.WithinFileLba.Take(6))}{(t.WithinFileLba.Count > 6 ? ", …" : "")}"
            : "in this track's pregap";
        ex.Add($"the bad-sector map records {t.WithinFileLba.Count + t.InPregap} unreadable sector(s) {where} — " +
               "zero-filled and hashing as data, so this track can NEVER match Redump until the disc is re-read clean.");
        return true;
    }

    private static string? ResolveGame(DatFile dat, List<(int? track, string role, string name, long size, string sha1)> actual, string? hint)
    {
        if (hint is not null)
        {
            var exact = dat.Roms.FirstOrDefault(r => string.Equals(r.Game, hint, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.Game;
            var partial = dat.Roms.FirstOrDefault(r => r.Game.Contains(hint, StringComparison.OrdinalIgnoreCase));
            return partial?.Game;
        }

        var tally = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in actual)
            foreach (var r in dat.BySha1(a.sha1))
                tally[r.Game] = tally.GetValueOrDefault(r.Game) + 1;
        if (tally.Count > 0) return tally.OrderByDescending(kv => kv.Value).First().Key;

        // No hash overlap — only safe to assume the target if the DAT describes exactly one game.
        var games = dat.Roms.Select(r => r.Game).Distinct().ToList();
        return games.Count == 1 ? games[0] : null;
    }

    private static IReadOnlyList<string> BuildRecommendations(List<RomDiff> roms, bool sameTotal, int sizeMismatches,
                                                              BadSectorMap? bad)
    {
        var recs = new List<string>();
        if (roms.All(r => r.Verdict == RomVerdict.Verified)) { recs.Add("Nothing to do — this dump is Redump-verified."); return recs; }

        bool splitLikely = sameTotal && roms.Any(r => r.Role != "cue" &&
                            r.Verdict is RomVerdict.SizeMismatch or RomVerdict.ContentMismatch);
        if (splitLikely)
            recs.Add("Track boundaries look wrong but no data is missing — re-cut with `redump-cue <cue> <disc.sub> <out.cue>` (add --snap-pregap if a gap reads 149).");
        if (bad?.DamagePresent == true)
            recs.Add("The dump has genuine unreadable sectors — re-read the disc; a holed dump cannot match a Redump checksum.");
        if (roms.Any(r => r.Role == "audio" && r.Verdict == RomVerdict.ContentMismatch) && !splitLikely)
            recs.Add("An audio track differs at matching size — confirm the drive read-offset (AccurateRip) and re-rip.");
        if (roms.Any(r => r.Role == "data" && r.Verdict == RomVerdict.ContentMismatch))
            recs.Add("The data track differs — check region/version; this is content, not a boundary issue, so redump-cue will not help.");
        if (!sameTotal && roms.Any(r => r.Verdict == RomVerdict.SizeMismatch))
            recs.Add("A track's size differs and the totals don't reconcile — trim trailing padding or re-read to Redump's exact sector count.");
        if (roms.Any(r => r.Verdict is RomVerdict.Missing or RomVerdict.Extra))
            recs.Add("The file set doesn't line up with the catalogued entry — check for a missing/extra track or a wrong disc.");
        if (recs.Count == 0)
            recs.Add("Differences are present but no single structural cause stands out; compare against a known-good reference image for a byte-level diff.");
        return recs;
    }

    private static void AddActual(List<(int? track, string role, string name, long size, string sha1)> list,
                                  string name, string fullPath, int? track, string role)
    {
        if (!File.Exists(fullPath)) { list.Add((track, role, name, -1, "")); return; }
        var sums = ImageChecksums.ComputeFile(fullPath);
        list.Add((track, role, name, sums.Length, sums.Sha1.ToLowerInvariant()));
    }
}
