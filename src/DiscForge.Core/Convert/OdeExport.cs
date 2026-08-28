// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;

namespace DiscForge.Core.Convert;

/// <summary>One file operation in an export plan: copy a source file, or write generated text, to a relative path.</summary>
public sealed record OdeFileOp
{
    public required string Kind { get; init; }          // "copy" or "write"
    public string? SourcePath { get; init; }            // for copy
    public string? Content { get; init; }               // for write
    public required string DestRelPath { get; init; }   // relative to the export root
}

/// <summary>An export plan for a target device: the game folder, the file operations, and any notes.</summary>
public sealed record OdeExportPlan
{
    public required string Target { get; init; }
    public required string GameFolder { get; init; }
    public required IReadOnlyList<OdeFileOp> Ops { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// ode-export — lay a preserved disc out the way a real optical-drive emulator (ODE) expects, so a faithful
/// dump can actually be played on original hardware. This is where preservation meets use: the community lives
/// on PSIO/xStation, GDEMU, MODE and the like, and each wants a particular folder shape and sidecar set. The
/// planner turns a dump into that layout — for PSIO, the game folder with its bin/cue plus a generated CU2 track
/// map. It repackages an already-preserved dump for playback; it decrypts nothing and defeats no protection.
/// </summary>
/// <summary>One disc's cue, parsed, and its total sector count — what <see cref="OdeExporter.PsioSet"/>
/// needs per disc of a multi-disc export.</summary>
public sealed record OdeDiscInput(string CuePath, CueSheet Cue, long TotalSectors);

public static class OdeExporter
{
    /// <summary>
    /// Plan a PSIO / xStation (PlayStation) export: the game in its own folder with the track bin(s), the cue,
    /// and a generated CU2 named after the cue (PSIO reads the CU2 for exact track geometry).
    /// </summary>
    public static OdeExportPlan Psio(string cuePath, CueSheet cue, long totalSectors, string gameName) =>
        PsioSet(new[] { new OdeDiscInput(cuePath, cue, totalSectors) }, gameName);

    /// <summary>
    /// Plan a PSIO / xStation multi-disc export: every disc's bin(s) + cue + generated CU2, all in ONE
    /// shared game folder, plus (when there's more than one disc) a MULTIDISC.LST at that folder's root
    /// naming each disc's cue in play order — the layout the PSIO Systems Manual documents (all of a
    /// title's images and MULTIDISC.LST together in one folder; the first line is what boots first), not
    /// the "one folder per disc" a stale note here used to describe.
    /// </summary>
    public static OdeExportPlan PsioSet(IReadOnlyList<OdeDiscInput> discs, string gameName)
    {
        ArgumentNullException.ThrowIfNull(discs);
        if (discs.Count == 0) throw new ArgumentException("At least one disc is required.", nameof(discs));

        string folder = SanitizeFolder(gameName);
        var ops = new List<OdeFileOp>();
        var destSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // dest -> source, collision check
        var cueLeafNamesInOrder = new List<string>();

        void AddOp(OdeFileOp op)
        {
            string dest = op.DestRelPath;
            string source = op.SourcePath ?? "(generated)";
            if (destSeen.TryGetValue(dest, out var existing) && !string.Equals(existing, source, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Two different source files both map to '{dest}' in the export folder " +
                    $"('{existing}' and '{source}') — rename one before exporting, or the second would " +
                    "silently overwrite the first on the SD card.");
            destSeen[dest] = source;
            ops.Add(op);
        }

        foreach (var d in discs)
        {
            ArgumentNullException.ThrowIfNull(d.Cue);
            string cueDir = Path.GetDirectoryName(Path.GetFullPath(d.CuePath)) ?? ".";
            string cueName = Path.GetFileName(d.CuePath);
            string cueBase = Path.GetFileNameWithoutExtension(d.CuePath);
            cueLeafNamesInOrder.Add(cueName);

            foreach (var binName in d.Cue.Tracks.Select(t => t.File)
                         .Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                AddOp(new OdeFileOp
                {
                    Kind = "copy",
                    SourcePath = Path.Combine(cueDir, binName),
                    DestRelPath = Path.Combine(folder, binName),
                });
            }

            AddOp(new OdeFileOp
            {
                Kind = "copy",
                SourcePath = Path.GetFullPath(d.CuePath),
                DestRelPath = Path.Combine(folder, cueName),
            });

            AddOp(new OdeFileOp
            {
                Kind = "write",
                Content = Cu2.Write(d.Cue, d.TotalSectors),
                DestRelPath = Path.Combine(folder, cueBase + ".cu2"),
            });
        }

        var notes = new List<string>
        {
            "PSIO/xStation: place the game folder on the SD card. Each disc's .cu2 carries its exact track map.",
        };

        if (discs.Count > 1)
        {
            AddOp(new OdeFileOp
            {
                Kind = "write",
                Content = PsioMultiDisc.BuildLst(cueLeafNamesInOrder),
                DestRelPath = Path.Combine(folder, "MULTIDISC.LST"),
            });
            notes.Add($"Multi-disc: all {discs.Count} discs share this one folder; MULTIDISC.LST lists them " +
                      "in play order (disc 1 boots first) per the PSIO Systems Manual.");
        }

        return new OdeExportPlan { Target = "psio", GameFolder = folder, Ops = ops, Notes = notes };
    }

    /// <summary>Characters no common filesystem accepts in a name. This is the strict
    /// Windows/FAT/exFAT set applied on every host OS — an ODE SD card is usually
    /// FAT/exFAT, and the folder may be authored on Linux or macOS (where
    /// <see cref="Path.GetInvalidFileNameChars"/> would only flag '/' and NUL and let
    /// ':' '*' '?' through, producing a folder the target device rejects).</summary>
    private static readonly char[] ReservedNameChars = "<>:\"/\\|?*".ToCharArray();

    internal static bool IsReservedNameChar(char c) =>
        c < ' ' || System.Array.IndexOf(ReservedNameChars, c) >= 0;

    /// <summary>Make a game name safe to use as a folder: strip characters no common filesystem accepts.</summary>
    public static string SanitizeFolder(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "GAME";
        var chars = name.Trim().Select(c => IsReservedNameChar(c) ? '_' : c).ToArray();
        string cleaned = new string(chars).Trim('.', ' ');
        return cleaned.Length == 0 ? "GAME" : cleaned;
    }
}

/// <summary>
/// Builds the PSIO MULTIDISC.LST sidecar: one image file name per line, in play order
/// (the first line is what PSIO boots first). Source: the PSIO Systems Manual's
/// "Multi-Disc Games" section — all of a title's disc images and MULTIDISC.LST live
/// together in one folder, and the manual is explicit about the exact byte format: a
/// CR+LF between lines, and no trailing CR+LF after the last line at all ("ensure that
/// there is no 'return' on the last line") — getting either wrong makes the PSIO
/// firmware wrap every disc onto one line. Names are written as bare file names (no
/// directory), matching every image sitting beside MULTIDISC.LST as the manual lays
/// the folder out.
/// </summary>
public static class PsioMultiDisc
{
    public const string FileName = "MULTIDISC.LST";

    public static string BuildLst(IEnumerable<string> discPathsInOrder)
    {
        ArgumentNullException.ThrowIfNull(discPathsInOrder);
        var names = discPathsInOrder
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => LeafName(p.Trim()))
            .ToList();
        if (names.Count == 0)
            throw new ArgumentException("No disc paths given.", nameof(discPathsInOrder));

        return string.Join("\r\n", names);
    }

    private static string LeafName(string path) => path.Replace('\\', '/').Split('/').Last();
}
