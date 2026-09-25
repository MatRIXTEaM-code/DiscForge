// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;

namespace DiscForge.Core.Audio;

/// <summary>One audio track as it will be ripped.</summary>
public sealed record AudioRipTrack
{
    public required int Number { get; init; }
    public required uint StartLba { get; init; }
    public required uint LengthSectors { get; init; }
    /// <summary>Filename this track will be written to, without a directory.</summary>
    public required string Filename { get; init; }

    /// <summary>Playing time. A CD frame is 1/75 second, always.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(LengthSectors / 75.0);
    public long PcmBytes => (long)LengthSectors * 2352;
}

/// <summary>A validated plan for ripping an audio CD to WAV files.</summary>
public sealed record AudioRipPlan
{
    public required IReadOnlyList<AudioRipTrack> Tracks { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Track start LBAs plus the lead-out, as AccurateRip's disc-ID
    /// calculation wants them.</summary>
    public required IReadOnlyList<int> TocOffsets { get; init; }

    public TimeSpan TotalDuration =>
        TimeSpan.FromSeconds(Tracks.Sum(t => t.LengthSectors) / 75.0);
    public long TotalPcmBytes => Tracks.Sum(t => t.PcmBytes);
}

/// <summary>
/// Plans an audio rip from a disc's table of contents.
///
/// Pure, like the other planners: what to read and where it goes, decided
/// before any hardware is touched. That matters here because an audio rip is
/// slow — jitter correction re-reads overlapping chunks — and discovering
/// half way through that a track was data, or that the disc has none, wastes
/// several minutes for nothing.
/// </summary>
public static class AudioRipPlanner
{
    /// <summary>Characters Windows forbids in a filename, plus the ones that
    /// merely cause trouble.</summary>
    private static readonly char[] Unsafe = Path.GetInvalidFileNameChars()
        .Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
        .Distinct().ToArray();

    public static AudioRipPlan Plan(DiscToc toc, DriveCapabilities drive)
    {
        ArgumentNullException.ThrowIfNull(toc);
        ArgumentNullException.ThrowIfNull(drive);

        var warnings = new List<string>();

        if (!drive.CdRead)
            throw new InvalidOperationException(
                $"{drive.Vendor} {drive.Model} cannot read CDs, and audio is a CD format.");

        var audio = toc.Tracks.Where(t => t.IsAudio && t.LengthSectors > 0).ToList();
        if (audio.Count == 0)
            throw new InvalidOperationException(
                "This disc has no audio tracks. A data CD's contents are files — " +
                "use Browse to extract them.");

        if (toc.Tracks.Any(t => !t.IsAudio))
            warnings.Add($"This is a mixed-mode disc: {toc.Tracks.Count(t => !t.IsAudio)} data " +
                         "track(s) are present and will be skipped. Only the audio is ripped.");

        var tracks = audio.Select(t => new AudioRipTrack
        {
            Number = t.Number,
            StartLba = t.StartLba,
            LengthSectors = t.LengthSectors,
            Filename = $"Track {t.Number:D2}.wav",
        }).ToList();

        // Very short tracks are usually deliberate — index markers, hidden
        // gaps — but they're also what a mis-parsed TOC produces, so say so.
        foreach (var t in tracks.Where(t => t.LengthSectors < 150))
            warnings.Add($"Track {t.Number} is only {t.LengthSectors} sectors " +
                         $"({t.Duration.TotalSeconds:0.0}s) — unusually short.");

        // AccurateRip identifies a disc by its track offsets and lead-out. The
        // lead-out must be included or the ID won't match anyone else's.
        var offsets = new List<int>();
        foreach (var t in toc.Tracks.OrderBy(t => t.Number))
            offsets.Add((int)t.StartLba);
        offsets.Add((int)toc.LeadOutLba);

        return new AudioRipPlan
        {
            Tracks = tracks,
            Warnings = warnings,
            TocOffsets = offsets,
        };
    }

    /// <summary>
    /// A filename safe for the filesystem, built from a track number and an
    /// optional title. Titles come from CD-TEXT or a user, and either may
    /// contain anything at all.
    /// </summary>
    public static string SafeFilename(int trackNumber, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return $"Track {trackNumber:D2}.wav";

        var cleaned = new string(title.Select(c => Unsafe.Contains(c) ? '_' : c).ToArray()).Trim();

        // A title that sanitises to nothing, or to dots, would produce a file
        // that can't be created or one that hides.
        if (cleaned.Length == 0 || cleaned.All(c => c == '.' || c == '_'))
            return $"Track {trackNumber:D2}.wav";

        const int MaxTitle = 120;      // leaves room for the number and extension
        if (cleaned.Length > MaxTitle) cleaned = cleaned[..MaxTitle].TrimEnd();

        return $"{trackNumber:D2} - {cleaned}.wav";
    }
}