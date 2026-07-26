// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Gdi;

public enum GdiIssueLevel { Info, Warning, Error }

/// <summary>One thing worth saying about a .gdi and the files beside it.</summary>
public sealed record GdiIssue(GdiIssueLevel Level, int? Track, string Message)
{
    public override string ToString() =>
        (Track is not null ? $"Track {Track}: " : "") + Message;
}

public sealed record GdiValidation
{
    public required IReadOnlyList<GdiIssue> Issues { get; init; }
    /// <summary>Track file → its byte length, or -1 if the file was not found.</summary>
    public required IReadOnlyDictionary<string, long> FileSizes { get; init; }

    public bool HasErrors => Issues.Any(i => i.Level == GdiIssueLevel.Error);
    public bool HasWarnings => Issues.Any(i => i.Level == GdiIssueLevel.Warning);
    public bool Clean => !HasErrors && !HasWarnings;
}

/// <summary>
/// Checks a parsed .gdi against the track files it names — the checks a text
/// editor cannot make because they need the files. A .gdi is a set of claims:
/// this track starts at this LBA, holds this kind of sector, lives in this file.
/// Nothing in the text confirms the file is present, is a whole number of
/// sectors, or that the tracks are laid out in a sane GD-ROM order. A patch or a
/// browse against a broken index fails obscurely; catching it here is the honest
/// place.
/// </summary>
public static class GdiValidator
{
    // Sector sizes a GD-ROM track legitimately uses.
    private static readonly int[] ValidSectorSizes = { 2048, 2352, 2336, 2448 };

    public static GdiValidation Validate(GdiDisc disc, string gdiDirectory)
    {
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(gdiDirectory);

        var issues = new List<GdiIssue>();
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // --- track numbering ------------------------------------------------

        var numbers = disc.Tracks.Select(t => t.Number).ToList();
        if (numbers.Count == 0)
        {
            issues.Add(new GdiIssue(GdiIssueLevel.Error, null, "The index lists no tracks."));
            return new GdiValidation { Issues = issues, FileSizes = sizes };
        }
        for (int i = 0; i < numbers.Count; i++)
            if (numbers[i] != i + 1)
                issues.Add(new GdiIssue(GdiIssueLevel.Warning, numbers[i],
                    $"Tracks are normally numbered 1..N in order; track {i + 1} is listed as " +
                    $"{numbers[i]}. Most tools expect sequential numbering."));

        // --- LBAs ascending -------------------------------------------------

        for (int i = 1; i < disc.Tracks.Count; i++)
            if (disc.Tracks[i].StartLba <= disc.Tracks[i - 1].StartLba)
                issues.Add(new GdiIssue(GdiIssueLevel.Error, disc.Tracks[i].Number,
                    $"Track {disc.Tracks[i].Number} starts at LBA {disc.Tracks[i].StartLba}, not after " +
                    $"track {disc.Tracks[i - 1].Number} at {disc.Tracks[i - 1].StartLba}. Track LBAs " +
                    "must ascend."));

        // --- sector sizes and files ----------------------------------------

        foreach (var t in disc.Tracks)
        {
            if (Array.IndexOf(ValidSectorSizes, t.SectorSize) < 0)
                issues.Add(new GdiIssue(GdiIssueLevel.Warning, t.Number,
                    $"Sector size {t.SectorSize} is unusual; GD-ROM tracks are almost always 2352 " +
                    "(raw) or 2048 (cooked)."));

            string path = Path.IsPathRooted(t.FileName)
                ? t.FileName
                : Path.Combine(gdiDirectory, t.FileName);

            if (!File.Exists(path))
            {
                sizes[t.FileName] = -1;
                issues.Add(new GdiIssue(GdiIssueLevel.Error, t.Number,
                    $"'{t.FileName}' is not beside the index. Without the track file there is " +
                    "nothing to patch, browse or convert."));
                continue;
            }

            long size = new FileInfo(path).Length;
            sizes[t.FileName] = size;

            long usable = size - t.Offset;
            if (usable < 0)
                issues.Add(new GdiIssue(GdiIssueLevel.Error, t.Number,
                    $"The offset {t.Offset} is past the end of '{t.FileName}' ({size:N0} bytes)."));
            else if (t.SectorSize > 0 && usable % t.SectorSize != 0)
                issues.Add(new GdiIssue(GdiIssueLevel.Warning, t.Number,
                    $"'{t.FileName}' holds {usable:N0} usable bytes, not a whole number of " +
                    $"{t.SectorSize}-byte sectors — the file may be truncated or the sector size wrong."));
        }

        // --- GD-ROM shape ---------------------------------------------------

        if (!disc.DataTracks.Any())
            issues.Add(new GdiIssue(GdiIssueLevel.Error, null,
                "The index has no data track, so it describes no filesystem."));

        if (disc.BootDataTrack is null)
            issues.Add(new GdiIssue(GdiIssueLevel.Warning, null,
                $"No data track begins in the high-density area (LBA ≥ {GdiParser.HighDensityStart}). " +
                "A normal GD-ROM keeps the game there; this image may be a low-density-only dump " +
                "or non-standard."));
        else
            issues.Add(new GdiIssue(GdiIssueLevel.Info, disc.BootDataTrack.Number,
                $"Track {disc.BootDataTrack.Number} is the high-density data track — the bootable " +
                "game filesystem, and what a PPF patch or a browse targets."));

        return new GdiValidation { Issues = issues, FileSizes = sizes };
    }
}
