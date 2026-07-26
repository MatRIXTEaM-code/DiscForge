// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Cdi;

namespace DiscForge.Core.Gdi;

/// <summary>
/// Converts a Dreamcast image between the two forms it circulates in: the .gdi
/// index (a text table of contents plus raw track files) and DiscJuggler CDI (a
/// single container). Both hold the same track data, so the conversion is a
/// re-container, not a re-encode — no sectors are altered.
///
/// The one thing that has to survive is the GD-ROM's two-area layout: the
/// low-density tracks (LBA &lt; 45000) and the high-density game tracks (LBA
/// ≥ 45000) are separate sessions with a large LBA gap between them. That gap is
/// metadata, not stored bytes, so grouping the tracks into two CDI sessions and
/// carrying each track's absolute start LBA preserves it exactly — a GDI → CDI →
/// GDI round trip reproduces the index and every track file byte-for-byte.
/// </summary>
public static class GdiConverter
{
    public sealed record GdiResult(string GdiText, IReadOnlyList<string> TrackFiles,
                                   IReadOnlyList<string> Warnings);

    // ---- GDI -> CDI ---------------------------------------------------------

    /// <summary>Convert a .gdi (and its track files) to a CDI written to
    /// <paramref name="output"/>.</summary>
    public static void GdiToCdi(string gdiPath, CdiVersion version, Stream output)
    {
        ArgumentNullException.ThrowIfNull(gdiPath);
        ArgumentNullException.ThrowIfNull(output);

        var disc = GdiParser.ParseFile(gdiPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(gdiPath)) ?? ".";

        // GD-ROM layout: low-density tracks form session 1, high-density (the
        // game, LBA ≥ 45000) form session 2. A track's file supplies its data.
        var lowDensity = disc.Tracks.Where(t => !t.IsHighDensity).OrderBy(t => t.StartLba).ToList();
        var highDensity = disc.Tracks.Where(t => t.IsHighDensity).OrderBy(t => t.StartLba).ToList();

        var sessions = new List<IReadOnlyList<CdiWriter.TrackInput>>();
        foreach (var group in new[] { lowDensity, highDensity })
        {
            if (group.Count == 0) continue;
            sessions.Add(group.Select(t => ToTrackInput(t, dir)).ToList());
        }
        if (sessions.Count == 0)
            throw new GdiFormatException("The .gdi has no tracks to convert.");

        CdiWriter.Write(output, version, sessions);
    }

    private static CdiWriter.TrackInput ToTrackInput(GdiTrack t, string gdiDirectory)
    {
        string path = Path.IsPathRooted(t.FileName)
            ? t.FileName
            : Path.Combine(gdiDirectory, t.FileName);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new GdiFormatException($"Track file '{t.FileName}' is not beside the .gdi.");

        long usable = info.Length - t.Offset;
        if (usable < 0)
            throw new GdiFormatException($"Track '{t.FileName}' offset {t.Offset} is past its end.");
        if (usable % t.SectorSize != 0)
            throw new GdiFormatException(
                $"Track '{t.FileName}' holds {usable:N0} usable bytes, not a whole number of " +
                $"{t.SectorSize}-byte sectors.");

        uint lengthSectors = (uint)(usable / t.SectorSize);
        long offset = t.Offset;
        string capturedPath = path;

        return new CdiWriter.TrackInput
        {
            // Dreamcast data tracks are Mode 1; audio is Red Book.
            Mode = t.IsData ? CdiTrackMode.Mode1 : CdiTrackMode.Audio,
            SectorSize = ToCdiSectorSize(t.SectorSize),
            PregapSectors = 0,
            LengthSectors = lengthSectors,
            StartLba = (uint)t.StartLba,
            Filename = t.FileName,
            DataWriter = os =>
            {
                using var src = new FileStream(capturedPath, FileMode.Open, FileAccess.Read,
                                               FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                src.Seek(offset, SeekOrigin.Begin);
                CopyExactly(src, os, (long)lengthSectors * t.SectorSize);
            },
        };
    }

    // ---- CDI -> GDI ---------------------------------------------------------

    /// <summary>Convert a CDI to a .gdi index plus one raw file per track, written
    /// into <paramref name="outputDir"/>.</summary>
    public static GdiResult CdiToGdi(Stream cdi, CdiImage image, string outputDir, string baseName)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(outputDir);
        Directory.CreateDirectory(outputDir);

        var warnings = new List<string>();
        var trackFiles = new List<string>();
        var lines = new List<string>();

        var tracks = image.AllTracks.ToList();
        foreach (var t in tracks)
        {
            bool isData = t.Mode != CdiTrackMode.Audio;
            string ext = isData ? "bin" : "raw";
            string name = $"{baseName}_track{t.Number:D2}.{ext}";
            trackFiles.Add(name);

            // Write the track's content sectors (pregap excluded), so the file is
            // exactly the sectors the GD-ROM track holds.
            using (var file = File.Create(Path.Combine(outputDir, name)))
            {
                var content = t with
                {
                    PregapSectors = 0,
                    TotalSectors = t.LengthSectors,
                    FileOffset = t.FileOffset + (long)t.PregapSectors * (int)t.SectorSize,
                };
                CdiExtractor.ExtractRaw(cdi, content, file);
            }

            if (t.PregapSectors > 0)
                warnings.Add($"Track {t.Number} had a {t.PregapSectors}-sector pregap, which .gdi " +
                             "does not record separately; its data is preserved from the track start.");

            int type = isData ? 4 : 0;
            lines.Add($"{t.Number} {t.StartLba} {type} {(int)t.SectorSize} {name} 0");
        }

        var sb = new StringBuilder();
        sb.Append(tracks.Count).Append('\n');
        foreach (var line in lines) sb.Append(line).Append('\n');
        var gdiText = sb.ToString();

        File.WriteAllText(Path.Combine(outputDir, baseName + ".gdi"), gdiText);
        return new GdiResult(gdiText, trackFiles, warnings);
    }

    // ---- helpers ------------------------------------------------------------

    private static CdiSectorSize ToCdiSectorSize(int sectorSize) => sectorSize switch
    {
        2048 => CdiSectorSize.S2048,
        2336 => CdiSectorSize.S2336,
        2352 => CdiSectorSize.S2352,
        _ => throw new GdiFormatException(
            $"Sector size {sectorSize} has no CDI equivalent (expected 2048, 2336 or 2352)."),
    };

    private static void CopyExactly(Stream src, Stream dst, long count)
    {
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            int want = (int)Math.Min(buffer.Length, count);
            int n = src.Read(buffer, 0, want);
            if (n <= 0) throw new EndOfStreamException("A track file ended earlier than its size implies.");
            dst.Write(buffer, 0, n);
            count -= n;
        }
    }
}
