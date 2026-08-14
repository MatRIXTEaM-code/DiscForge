// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Dat;

namespace DiscForge.Core.Redump;

/// <summary>The set of text artifacts that make up a submission bundle for a dump.</summary>
public sealed record SubmissionArtifacts
{
    /// <summary>The redump.org-style submission text (per-track + whole-disc hashes, cuesheet, subchannel).</summary>
    public required string InfoText { get; init; }
    /// <summary>A Logiqx DAT cataloguing the dump (whole-image CRC/MD5/SHA-1 + size).</summary>
    public required string Dat { get; init; }
    /// <summary>The cuesheet, when the dump has one (verbatim from a .cue input, else synthesised).</summary>
    public string? Cuesheet { get; init; }
    /// <summary>The per-track rows, for a caller that wants to also emit per-track DAT roms.</summary>
    public required IReadOnlyList<DatBuildRom> TrackRoms { get; init; }
}

/// <summary>
/// Assembles a submission <b>bundle</b> from an analysed dump — the packaging layer on top of
/// <see cref="SubmissionInfoGenerator"/> (which produces the info text) and <see cref="DatBuilder"/> (which
/// writes DATs). Turns a dump into the folder a preservation submitter actually needs: the submission info,
/// a matching DAT entry, and the cuesheet, all named from the game. Pure text generation — the CLI wraps this
/// with the file copying; nothing here touches the disk.
/// </summary>
public static class SubmissionPackage
{
    /// <summary>Build the text artifacts for a dump's submission bundle.</summary>
    public static SubmissionArtifacts Build(SubmissionInfo info, string gameName)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(gameName);

        // Whole-image DAT rom.
        var wholeRom = new DatBuildRom(gameName, info.FileName, info.TotalSize,
            info.CombinedCrc32, info.CombinedMd5, info.CombinedSha1);

        // Per-track roms (a multi-track disc catalogues each track file too).
        var trackRoms = info.Tracks
            .Select(t => new DatBuildRom(gameName, TrackFileName(info.FileName, t.Number, info.Tracks.Count),
                                         t.Size, t.Crc32, t.Md5, t.Sha1))
            .ToList();

        // The DAT lists the whole image plus any per-track files, so it verifies either way.
        var roms = new List<DatBuildRom> { wholeRom };
        if (info.Tracks.Count > 1) roms.AddRange(trackRoms);

        string dat = DatBuilder.Build(gameName, roms, description: gameName, author: "DiscForge");

        bool hasCue = !string.IsNullOrWhiteSpace(info.Cuesheet);
        return new SubmissionArtifacts
        {
            InfoText = info.ToRedumpText(),
            Dat = dat,
            Cuesheet = hasCue ? info.Cuesheet : null,
            TrackRoms = trackRoms,
        };
    }

    private static string TrackFileName(string image, int track, int trackCount)
    {
        // Best-effort per-track name: "<base> (Track N).bin" for a multi-track disc.
        string baseName = System.IO.Path.GetFileNameWithoutExtension(image);
        return trackCount > 1 ? $"{baseName} (Track {track}).bin" : image;
    }
}
