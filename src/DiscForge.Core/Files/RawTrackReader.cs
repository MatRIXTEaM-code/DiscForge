// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Cue;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Files;

/// <summary>
/// Opens the ISO 9660 filesystem inside a raw .bin/.cue image — the job psxrip
/// does for a PlayStation disc, and the reason a Redump-style bin (a raw
/// Mode 2/2352 image with no track table of its own) can now be browsed without
/// converting it to CDI or ISO first.
///
/// A .cue names the data track and its sector mode, which fixes where the 2048
/// user bytes sit inside each 2352-byte sector; with only a .bin the mode is
/// discovered by trying each layout and seeing which one puts the "CD001"
/// volume-descriptor tag where ISO 9660 says it must be (sector 16). Either way
/// the result is a plain user-data stream that IsoReader walks directly.
/// </summary>
public static class RawTrackReader
{
    /// <summary>A user-data view plus the base stream it reads from; the caller
    /// owns and disposes both.</summary>
    public sealed record Opened(Stream View, Stream Base, string Layout);

    /// <summary>Layouts to try for a bare .bin, most common first.</summary>
    private static readonly (int size, int off, int len, string name)[] Candidates =
    {
        (2352, 24, 2048, "Mode 2/2352"),
        (2352, 16, 2048, "Mode 1/2352"),
        (2336, 8, 2048, "Mode 2/2336"),
        (2048, 0, 2048, "raw 2048"),
    };

    private static (int size, int off, int len) Layout(CueTrackType t) => t switch
    {
        CueTrackType.Mode1_2048 => (2048, 0, 2048),
        CueTrackType.Mode1_2352 => (2352, 16, 2048),
        CueTrackType.Mode2_2336 => (2336, 8, 2048),
        CueTrackType.Mode2_2352 => (2352, 24, 2048),
        CueTrackType.Audio => throw new InvalidDataException(
            "The first track is audio — an audio disc has no filesystem to browse."),
        _ => throw new NotSupportedException($"No user-data layout for {t}."),
    };

    /// <summary>Open a .cue (or a bare .bin/.img) and return a user-data stream
    /// positioned so IsoReader can read the volume descriptors at sector 16.</summary>
    public static Opened Open(string imagePath)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        string ext = Path.GetExtension(imagePath);
        return ext.Equals(".cue", StringComparison.OrdinalIgnoreCase)
            ? OpenFromCue(imagePath)
            : OpenBin(imagePath);
    }

    private static Opened OpenFromCue(string cuePath)
    {
        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        var data = cue.Tracks.FirstOrDefault(t => t.Type != CueTrackType.Audio)
                   ?? throw new InvalidDataException(
                       "The cue has no data track — an audio disc has no filesystem to browse.");

        var (size, off, len) = Layout(data.Type);
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        string binPath = Path.Combine(baseDir, data.File);
        if (!File.Exists(binPath))
            throw new FileNotFoundException($"The cue's data file '{data.File}' is missing.", binPath);

        var fs = File.OpenRead(binPath);
        try
        {
            long sectors = fs.Length / size;
            var view = new RawTrackUserDataStream(fs, 0, size, off, len, sectors);
            return new Opened(view, fs, $"{TypeName(data.Type)} (from cue)");
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static Opened OpenBin(string binPath)
    {
        if (!File.Exists(binPath))
            throw new FileNotFoundException("Image not found.", binPath);

        var fs = File.OpenRead(binPath);
        try
        {
            foreach (var (size, off, len, name) in Candidates)
            {
                if (fs.Length < (16L * size) + off + len) continue;
                if (!LooksLikeIso(fs, size, off)) continue;
                long sectors = fs.Length / size;
                var view = new RawTrackUserDataStream(fs, 0, size, off, len, sectors);
                return new Opened(view, fs, name);
            }
            throw new InvalidDataException(
                "No ISO 9660 filesystem was found in this bin (tried Mode 2/2352, Mode 1/2352, " +
                "Mode 2/2336 and raw 2048). If it is an audio disc there is no filesystem; if it " +
                "is a multi-track set, point at the .cue instead.");
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>True when sector 16's user data carries the ISO 9660 "CD001"
    /// standard identifier — the cheap, decisive test for a given layout.</summary>
    private static bool LooksLikeIso(Stream s, int sectorSize, int userOffset)
    {
        Span<byte> id = stackalloc byte[5];
        long pos = 16L * sectorSize + userOffset + 1;   // +1 skips the descriptor type byte
        s.Seek(pos, SeekOrigin.Begin);
        int got = s.Read(id);
        return got == 5 && Encoding.ASCII.GetString(id) == "CD001";
    }

    private static string TypeName(CueTrackType t) => CueSheet.TypeToToken(t).token;
}
