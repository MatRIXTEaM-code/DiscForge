// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Dat;

namespace DiscForge.Core.Library;

/// <summary>How a rebuilt set is laid out on disk.</summary>
public enum RebuildLayout
{
    /// <summary>Every file in one folder, named canonically.</summary>
    Flat,
    /// <summary>One sub-folder per game (right for multi-track disc sets).</summary>
    PerGameFolder,
}

/// <summary>One planned copy/move: put <see cref="SourcePath"/> at <see cref="DestPath"/>.</summary>
public sealed record RebuildAction(string SourcePath, string DestPath, string Game);

/// <summary>The plan for turning a messy folder into a canonical, DAT-named set.</summary>
public sealed record RebuildPlan
{
    public required string DestRoot { get; init; }
    public required IReadOnlyList<RebuildAction> Actions { get; init; }
    /// <summary>Source files that matched no DAT entry.</summary>
    public required IReadOnlyList<string> UnknownFiles { get; init; }
    /// <summary>DAT entries with no matching source file (gaps in the set).</summary>
    public required IReadOnlyList<DatRom> MissingRoms { get; init; }
    /// <summary>Verified files already sitting at their canonical path (no move needed).</summary>
    public required int AlreadyInPlace { get; init; }

    public int ToPlace => Actions.Count;
    public int Missing => MissingRoms.Count;
    public int Unknown => UnknownFiles.Count;
}

/// <summary>
/// The set rebuilder: given a scanned folder and the DAT it was checked against, it
/// plans a clean, canonically-named set — every verified file placed under its DAT
/// name (flat, or one folder per game for multi-track disc sets) — and reports what's
/// still missing from the set and what didn't match. It builds on
/// <see cref="LibraryScanner"/>'s verification; it only ever copies or moves whole
/// files, never rewrites their contents.
/// </summary>
public static class SetRebuilder
{
    public static RebuildPlan Plan(LibraryReport scan, string destRoot, RebuildLayout layout = RebuildLayout.Flat)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(destRoot);

        var actions = new List<RebuildAction>();
        var unknown = new List<string>();
        int alreadyInPlace = 0;

        foreach (var e in scan.Entries)
        {
            // Only place confirmed-good files; duplicates and unknowns are reported, not moved.
            if (e.Match is null || e.Status is LibraryStatus.Duplicate)
            {
                if (e.Status is LibraryStatus.Unknown or LibraryStatus.Unchecked)
                    unknown.Add(e.Path);
                continue;
            }

            string canonicalName = e.Match.Name.Length > 0 ? e.Match.Name : e.FileName;
            string dir = layout == RebuildLayout.PerGameFolder
                ? System.IO.Path.Combine(destRoot, SanitizeFolder(e.Match.Game))
                : destRoot;
            string dest = System.IO.Path.Combine(dir, LeafName(canonicalName));

            if (PathsEqual(dest, e.Path)) { alreadyInPlace++; continue; }
            actions.Add(new RebuildAction(e.Path, dest, e.Match.Game));
        }

        return new RebuildPlan
        {
            DestRoot = destRoot,
            Actions = actions,
            UnknownFiles = unknown,
            MissingRoms = scan.Missing,
            AlreadyInPlace = alreadyInPlace,
        };
    }

    /// <summary>
    /// Execute the plan: create the destination folders and copy (or move) each file to
    /// its canonical path. Returns how many files were placed. A destination that
    /// already exists is left untouched (idempotent re-runs don't duplicate work).
    /// </summary>
    public static int Apply(RebuildPlan plan, bool move = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int done = 0;
        foreach (var a in plan.Actions)
        {
            string? dir = System.IO.Path.GetDirectoryName(a.DestPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            if (File.Exists(a.DestPath)) continue;

            if (move) File.Move(a.SourcePath, a.DestPath);
            else File.Copy(a.SourcePath, a.DestPath);
            done++;
        }
        return done;
    }

    private static string LeafName(string name) =>
        name.Replace('\\', '/').Split('/').Last();

    private static string SanitizeFolder(string name)
    {
        var cleaned = string.Concat(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim();
        return cleaned.Length == 0 ? "game" : cleaned;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
