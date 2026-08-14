// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Cue;

public sealed record CueRepairResult
{
    public required string CueText { get; init; }
    public required IReadOnlyList<string> Changes { get; init; }
    /// <summary>Problems that could not be fixed automatically (need a human).</summary>
    public required IReadOnlyList<string> Unresolved { get; init; }
    public bool Changed => Changes.Count > 0;

    public string Summary() => Changed
        ? $"Repaired {Changes.Count} issue(s){(Unresolved.Count > 0 ? $", {Unresolved.Count} need a look" : "")}."
        : Unresolved.Count > 0 ? $"No auto-fixes applied; {Unresolved.Count} issue(s) need a look."
                               : "Already clean — nothing to repair.";
}

/// <summary>
/// cue-repair — fix the everyday ways a cue sheet breaks, where <c>cue-check</c> only reports them. Bad tools,
/// renames and hand-edits leave a cue that points at the wrong file, numbers its tracks out of order, or forgets
/// an INDEX 01; the image is fine but nothing will mount it. This cross-checks the cue against the actual track
/// files beside it and repairs the FILE references (case / rename / the single obvious candidate), renumbers the
/// tracks sequentially, adds any missing INDEX 01, and re-emits a clean, normalised cue — reporting every change
/// and anything it could not safely fix. It rewrites the cue text only; it never touches the track data.
/// </summary>
public static class CueRepair
{
    public static CueRepairResult Repair(string cuePath)
    {
        ArgumentNullException.ThrowIfNull(cuePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        var sheet = CueSheet.Parse(File.ReadAllText(cuePath));

        var changes = new List<string>();
        var unresolved = new List<string>();

        // Actual track files present beside the cue.
        var actual = Directory.EnumerateFiles(dir)
            .Where(f => IsTrackFile(f))
            .ToList();
        var actualByName = actual.ToDictionary(f => Path.GetFileName(f), StringComparer.Ordinal);

        // 1) Resolve each distinct FILE reference against what is actually on disk.
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var refName in sheet.Tracks.Select(t => t.File).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.Ordinal))
        {
            if (actualByName.ContainsKey(refName)) continue;                 // exact match, fine
            string? resolved = ResolveFile(refName, actual, sheet, actualByName);
            if (resolved is not null && resolved != refName)
            {
                remap[refName] = resolved;
                changes.Add($"FILE \"{refName}\" → \"{resolved}\" (matched the track file on disk).");
            }
            else
                unresolved.Add(refName);   // held for the reconciliation pass below
        }

        // Reconciliation: a single still-unresolved reference with a single orphan track file on disk almost
        // certainly pair up (e.g. "track2.bin" in the cue, "track02.bin" on disk).
        if (unresolved.Count == 1)
        {
            var used = new HashSet<string>(
                sheet.Tracks.Select(t => remap.TryGetValue(t.File, out var m) ? m : t.File), StringComparer.Ordinal);
            var orphans = actual.Select(Path.GetFileName).Where(n => n is not null && !used.Contains(n!)).ToList();
            if (orphans.Count == 1)
            {
                remap[unresolved[0]] = orphans[0]!;
                changes.Add($"FILE \"{unresolved[0]}\" → \"{orphans[0]}\" (the only unmatched track file on disk).");
                unresolved.Clear();
            }
        }
        var unresolvedMsgs = unresolved
            .Select(r => $"FILE \"{r}\" is not on disk and no single candidate matched — fix by hand.")
            .ToList();

        // 2) Rebuild tracks: apply the remap, renumber 1..N in order, guarantee an INDEX 01.
        var newTracks = new List<CueTrack>();
        int expected = 1;
        foreach (var t in sheet.Tracks)
        {
            string file = remap.TryGetValue(t.File, out var r) ? r : t.File;

            var indices = t.Indices.ToList();
            if (!indices.Any(x => x.Number == 1))
            {
                var at = indices.FirstOrDefault(x => x.Number == 0)?.Time ?? new Msf(0, 0, 0);
                indices.Add(new CueIndex(1, at));
                indices = indices.OrderBy(x => x.Number).ToList();
                changes.Add($"track {t.Number:D2}: added a missing INDEX 01 at {at}.");
            }

            int number = t.Number;
            if (number != expected)
            {
                changes.Add($"track number {t.Number:D2} → {expected:D2} (renumbered sequentially).");
                number = expected;
            }
            expected++;

            newTracks.Add(t with { Number = number, File = file, Indices = indices });
        }

        var repaired = new CueSheet
        {
            Tracks = newTracks, Catalog = sheet.Catalog, Title = sheet.Title, Performer = sheet.Performer,
        };
        string text = repaired.Write();

        // 3) Formatting-only normalisation counts as a change if the bytes differ and nothing else was reported.
        if (changes.Count == 0 && !string.Equals(text, File.ReadAllText(cuePath).Replace("\r\n", "\n"), StringComparison.Ordinal)
            && File.ReadAllText(cuePath).Contains('\r'))
            changes.Add("normalised line endings and whitespace to the standard cue format.");

        return new CueRepairResult { CueText = text, Changes = changes, Unresolved = unresolvedMsgs };
    }

    /// <summary>Find the on-disk track file a broken FILE reference most likely meant.</summary>
    private static string? ResolveFile(string refName, List<string> actual, CueSheet sheet,
                                       Dictionary<string, string> actualByName)
    {
        // Case-insensitive exact filename.
        var ci = actual.FirstOrDefault(f => string.Equals(Path.GetFileName(f), refName, StringComparison.OrdinalIgnoreCase));
        if (ci is not null) return Path.GetFileName(ci);

        // Same stem, any extension (a rename that changed only the extension/case).
        string stem = Path.GetFileNameWithoutExtension(refName);
        var byStem = actual.Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byStem.Count == 1) return Path.GetFileName(byStem[0]);

        // A single-file cue whose one FILE is broken, with exactly one track file on disk.
        int distinctRefs = sheet.Tracks.Select(t => t.File).Distinct(StringComparer.Ordinal).Count();
        var unused = actual.Where(f => !actualByName.ContainsKey(Path.GetFileName(f))).ToList();
        if (distinctRefs == 1 && unused.Count == 1) return Path.GetFileName(unused[0]);

        return null;
    }

    private static bool IsTrackFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".bin" or ".img" or ".iso" or ".raw";
    }
}
