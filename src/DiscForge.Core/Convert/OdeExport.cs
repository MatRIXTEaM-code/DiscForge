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
public static class OdeExporter
{
    /// <summary>
    /// Plan a PSIO / xStation (PlayStation) export: the game in its own folder with the track bin(s), the cue,
    /// and a generated CU2 named after the cue (PSIO reads the CU2 for exact track geometry).
    /// </summary>
    public static OdeExportPlan Psio(string cuePath, CueSheet cue, long totalSectors, string gameName)
    {
        ArgumentNullException.ThrowIfNull(cuePath);
        ArgumentNullException.ThrowIfNull(cue);

        string folder = SanitizeFolder(gameName);
        string cueDir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        string cueName = Path.GetFileName(cuePath);
        string cueBase = Path.GetFileNameWithoutExtension(cuePath);

        var ops = new List<OdeFileOp>();

        // Track bin(s), in cue order, de-duplicated.
        foreach (var binName in cue.Tracks.Select(t => t.File)
                     .Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new OdeFileOp
            {
                Kind = "copy",
                SourcePath = Path.Combine(cueDir, binName),
                DestRelPath = Path.Combine(folder, binName),
            });
        }

        // The cue itself.
        ops.Add(new OdeFileOp
        {
            Kind = "copy",
            SourcePath = Path.GetFullPath(cuePath),
            DestRelPath = Path.Combine(folder, cueName),
        });

        // The generated CU2 track map (PSIO/xStation), named after the cue.
        ops.Add(new OdeFileOp
        {
            Kind = "write",
            Content = Cu2.Write(cue, totalSectors),
            DestRelPath = Path.Combine(folder, cueBase + ".cu2"),
        });

        var notes = new List<string>
        {
            "PSIO/xStation: place the game folder on the SD card. The .cu2 carries the exact track map.",
            "Multi-disc: put each disc's folder on the card and add a MULTIDISC.LST listing them for in-game swapping.",
        };

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
