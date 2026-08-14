// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cdi;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Convert;

/// <summary>
/// Converts between a plain <c>.iso</c> (a bare stream of 2048-byte Mode 1
/// sectors) and <c>.cdi</c>.
///
/// An ISO carries no track structure at all — it IS the user data of a single
/// Mode 1 data track. So ISO -> CDI is a matter of wrapping it in a descriptor,
/// and CDI -> ISO is unwrapping the data track's cooked user bytes. Both stream,
/// so a DVD9 doesn't land in memory.
/// </summary>
public static class IsoConverter
{
    public sealed record ConvertResult(long BytesWritten, int Sectors, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Wrap an existing .iso as a single-track data CDI.
    /// </summary>
    public static ConvertResult IsoToCdi(string isoPath, CdiVersion version, Stream output,
                                         string? volumeLabel = null)
    {
        ArgumentNullException.ThrowIfNull(isoPath);
        ArgumentNullException.ThrowIfNull(output);

        var info = new FileInfo(isoPath);
        if (!info.Exists) throw new FileNotFoundException("ISO not found.", isoPath);
        if (info.Length == 0) throw new InvalidDataException($"'{info.Name}' is empty.");

        var warnings = new List<string>();

        if (info.Length % IsoBuilder.SectorSize != 0)
            throw new InvalidDataException(
                $"'{info.Name}' is {info.Length:N0} bytes, which is not a whole number of " +
                $"{IsoBuilder.SectorSize}-byte sectors. It may be truncated, or it may not be a " +
                "plain ISO (a raw 2352-byte BIN, for example, needs the BIN/CUE path).");

        int sectors = (int)(info.Length / IsoBuilder.SectorSize);

        // Sanity: a real ISO 9660 has "CD001" at sector 16. Warn rather than
        // refuse — a UDF-only or HFS disc image is still legitimately a data
        // track, it just isn't ISO 9660.
        if (!LooksLikeIso9660(isoPath, sectors))
            warnings.Add(
                "No ISO 9660 signature at sector 16. Converting anyway — the image may use " +
                "UDF or another filesystem, which is fine, but check it's not a raw BIN.");

        string label = string.IsNullOrWhiteSpace(volumeLabel)
            ? Path.GetFileNameWithoutExtension(isoPath).ToUpperInvariant()
            : volumeLabel;

        var track = new CdiWriter.TrackInput
        {
            Mode = CdiTrackMode.Mode1,
            SectorSize = CdiSectorSize.S2048,
            PregapSectors = 0,
            LengthSectors = (uint)sectors,
            StartLba = 0,
            Filename = Path.GetFileName(isoPath),
            DataWriter = os => CopyFile(isoPath, os),
        };

        long start = output.CanSeek ? output.Position : 0;
        CdiWriter.Write(output, version, new[] { (IReadOnlyList<CdiWriter.TrackInput>)new[] { track } });
        long written = output.CanSeek ? output.Position - start : 0;

        return new ConvertResult(written, sectors, warnings);
    }

    /// <summary>
    /// Unwrap a CDI's data track to a plain .iso. Only the cooked 2048-byte user
    /// data is written, which is exactly what an ISO is.
    /// </summary>
    public static ConvertResult CdiToIso(Stream cdi, CdiImage image, Stream output)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);

        var warnings = new List<string>();

        var dataTracks = image.AllTracks.Where(t => t.Mode != CdiTrackMode.Audio).ToList();
        if (dataTracks.Count == 0)
            throw new InvalidDataException(
                "This image has no data track — an audio disc cannot become an ISO. " +
                "Use 'extract' to get the audio as WAV files.");

        if (image.AllTracks.Any(t => t.Mode == CdiTrackMode.Audio))
            warnings.Add(
                "The image is mixed-mode: only the data track becomes the ISO, and the audio " +
                "tracks are dropped. An ISO cannot carry audio.");
        if (dataTracks.Count > 1)
            warnings.Add(
                $"The image has {dataTracks.Count} data tracks; only the first becomes the ISO. " +
                "An ISO holds a single track.");
        if (image.Sessions.Count > 1)
            warnings.Add(
                "The image is multisession; only the first session's data becomes the ISO.");

        var track = dataTracks[0];
        long written = CdiExtractor.ExtractUserData(cdi, track, output);
        int sectors = (int)(written / IsoBuilder.SectorSize);

        return new ConvertResult(written, sectors, warnings);
    }

    private static bool LooksLikeIso9660(string path, int sectors)
    {
        if (sectors <= 16) return false;
        try
        {
            using var fs = File.OpenRead(path);
            fs.Seek(16L * IsoBuilder.SectorSize, SeekOrigin.Begin);
            var buf = new byte[6];
            if (fs.Read(buf, 0, 6) < 6) return false;
            // byte 0 is the descriptor type; bytes 1..5 are "CD001".
            return buf[1] == 'C' && buf[2] == 'D' && buf[3] == '0' && buf[4] == '0' && buf[5] == '1';
        }
        catch { return false; }
    }

    private static void CopyFile(string path, Stream output)
    {
        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                       bufferSize: 1 << 16, FileOptions.SequentialScan);
        src.CopyTo(output, 1 << 16);
    }
}
