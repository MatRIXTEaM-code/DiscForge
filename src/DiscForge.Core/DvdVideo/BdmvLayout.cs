// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.DvdVideo;

/// <summary>The result of validating a BDMV (Blu-ray Disc Movie) folder.</summary>
public sealed record BdmvValidation
{
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required int PlaylistCount { get; init; }
    public required int ClipCount { get; init; }
    public required int StreamCount { get; init; }
    public required bool HasBackup { get; init; }
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates a Blu-ray <c>BDMV</c> folder for BD-Video assembly — pure logic over the set
/// of relative paths, no I/O. A BD-Video disc carries, under <c>BDMV/</c>: the two control
/// files <c>index.bdmv</c> and <c>MovieObject.bdmv</c>; one or more playlists in
/// <c>PLAYLIST/*.mpls</c>; clip-information files in <c>CLIPINF/*.clpi</c>; the AV streams
/// in <c>STREAM/*.m2ts</c>; and a <c>BACKUP/</c> copy of the control data. Blu-ray uses a
/// pure UDF 2.50 filesystem (no ISO 9660), which DiscForge's writer now produces — so an
/// assembler validates the structure here and hands the tree to the UDF 2.50 build.
///
/// This checks the presence and shape of the structure; it does not parse the playlist /
/// clip binaries or verify stream timing (a deeper step, and player-verified anyway).
/// </summary>
public static class BdmvLayout
{
    public static BdmvValidation Validate(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var paths = relativePaths
            .Select(p => p.Replace('\\', '/').Trim('/').ToUpperInvariant())
            .ToList();

        var errors = new List<string>();
        var warnings = new List<string>();

        bool Has(string exact) => paths.Contains(exact, StringComparer.Ordinal);
        int CountIn(string dir, string ext) => paths.Count(p =>
            p.StartsWith(dir + "/", StringComparison.Ordinal) && p.EndsWith(ext, StringComparison.Ordinal)
            && !p[(dir.Length + 1)..].Contains('/'));   // directly in the folder, not nested

        if (!Has("BDMV/INDEX.BDMV")) errors.Add("BDMV/index.bdmv is missing — not a BD-Video folder.");
        if (!Has("BDMV/MOVIEOBJECT.BDMV")) errors.Add("BDMV/MovieObject.bdmv is missing.");

        int playlists = CountIn("BDMV/PLAYLIST", ".MPLS");
        int clips = CountIn("BDMV/CLIPINF", ".CLPI");
        int streams = CountIn("BDMV/STREAM", ".M2TS");
        if (playlists == 0) errors.Add("No playlists found in BDMV/PLAYLIST (*.mpls).");
        if (clips == 0) errors.Add("No clip-info files found in BDMV/CLIPINF (*.clpi).");
        if (streams == 0) errors.Add("No AV streams found in BDMV/STREAM (*.m2ts).");
        if (clips != streams && clips > 0 && streams > 0)
            warnings.Add($"CLIPINF has {clips} .clpi but STREAM has {streams} .m2ts — usually one clip per stream.");

        bool backup = paths.Any(p => p.StartsWith("BDMV/BACKUP/", StringComparison.Ordinal));
        if (!backup) warnings.Add("BDMV/BACKUP/ is missing (BD-Video keeps a backup of the control files).");

        return new BdmvValidation
        {
            Errors = errors,
            Warnings = warnings,
            PlaylistCount = playlists,
            ClipCount = clips,
            StreamCount = streams,
            HasBackup = backup,
        };
    }
}
