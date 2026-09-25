// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Cue;

namespace DiscForge.Core.Forensics;

/// <summary>A dump-completeness certificate: what physical territory a bin/cue image accounts for, what
/// cross-checks agree, and what a bin/cue can never represent (so its absence isn't mistaken for damage).</summary>
public sealed record DumpCompletenessResult
{
    public required int TrackCount { get; init; }
    public required int SessionCount { get; init; }
    public required long TotalSectors { get; init; }
    public required IReadOnlyList<string> BinFiles { get; init; }
    public required bool AllBinsPresent { get; init; }
    public required bool WholeSector { get; init; }

    /// <summary>A subchannel sidecar (.sub) was found beside the image.</summary>
    public required bool SubchannelPresent { get; init; }
    /// <summary>Sectors the subchannel sidecar covers (its bytes ÷ 96), or null when absent.</summary>
    public long? SubchannelSectors { get; init; }
    /// <summary>The subchannel covers exactly the same sector count as the main data — a real completeness
    /// cross-check (an independent third account of the disc's length).</summary>
    public bool SubchannelMatches { get; init; }

    /// <summary>Concrete completeness gaps found (empty when the image accounts for everything it can).</summary>
    public required IReadOnlyList<string> Gaps { get; init; }
    /// <summary>Physical regions a bin/cue image inherently cannot carry — flagged so a catalogue is honest
    /// about what a file-image does and does not preserve.</summary>
    public required IReadOnlyList<string> NotRepresentable { get; init; }

    /// <summary>True when every track's own declared position was checked, sector-range by sector-range —
    /// not merely that totals add up. Per file: every track's INDEX 01 falls within the file's actual sector
    /// count, and consecutive tracks' INDEX 01 values strictly increase (each track claims a later starting
    /// sector than the one before it, so no two tracks claim the same sector and no track is silently skipped
    /// between two others). This is a genuinely stronger claim than <see cref="TotalSectors"/> agreeing with
    /// the file's byte length: two mis-ordered or duplicated INDEX 01s can sum to the right total sector count
    /// while actually leaving a real gap (an unclaimed sector range) or a real overlap (two tracks claiming the
    /// same range) — the sum alone can't see either. What this still can't see: it does not evaluate INDEX 00
    /// pregap timing (whether a pregap is physically stored in the file or generated on playback varies by
    /// authoring tool and both are legitimate, so asserting exact adjacency there would produce false
    /// positives on entirely valid dumps) — see <see cref="Gaps"/> for the specific violation(s) found when
    /// this is false.</summary>
    public required bool CoverageProven { get; init; }

    public bool Complete => Gaps.Count == 0;

    public string Summary()
    {
        var sb = new StringBuilder(
            $"{TrackCount} track(s) in {SessionCount} session(s), {TotalSectors:N0} sectors; " +
            $"{BinFiles.Count} data file(s) {(AllBinsPresent ? "present" : "MISSING")}, " +
            $"{(WholeSector ? "whole-sector" : "NOT whole-sector")}. ");
        sb.Append(CoverageProven
            ? "Every track's own position checked sector-by-sector — no gap or overlap between tracks. "
            : "Per-sector track coverage NOT proven (see gaps). ");
        sb.Append(SubchannelPresent
            ? $"Subchannel {(SubchannelMatches ? "covers all sectors ✓" : $"MISMATCH ({SubchannelSectors:N0} vs {TotalSectors:N0})")}. "
            : "No subchannel sidecar. ");
        sb.Append(Complete ? "Image is complete for what a bin/cue can hold."
                           : $"{Gaps.Count} completeness gap(s): {string.Join("; ", Gaps)}.");
        return sb.ToString();
    }
}

/// <summary>
/// completeness-check — issue a "did we capture everything?" certificate for a bin/cue dump. It reconciles
/// three independent accounts of the disc's extent: the cue's declared track layout, the data file's byte
/// length (÷ sector size), and — when a .sub sidecar is present — the subchannel's own sector count (÷ 96).
/// Agreement across the three is strong evidence the dump is whole; a mismatch pinpoints what's short. It
/// also states plainly what a file-image can never carry (lead-in/lead-out, PMA, ATIP) so their absence is
/// documented rather than mistaken for loss. Reads and reconciles; it changes nothing.
/// </summary>
public static class DumpCompleteness
{
    private const int SubchannelBytesPerSector = 96;   // packed P–W subchannel

    public static DumpCompletenessResult Check(string cuePath)
    {
        ArgumentNullException.ThrowIfNull(cuePath);
        if (!File.Exists(cuePath)) throw new FileNotFoundException("Cue sheet not found.", cuePath);

        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        string dir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        var gaps = new List<string>();

        // Per data file: the sector size its tracks use, and the sectors its byte length implies.
        var files = cue.Tracks.Select(t => t.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        long totalSectors = 0;
        bool allPresent = true, wholeSector = true, coverageProven = true;

        foreach (var f in files)
        {
            var tracksInFile = cue.Tracks.Where(t => string.Equals(t.File, f, StringComparison.OrdinalIgnoreCase))
                                          .OrderBy(t => t.Number).ToList();
            var sizes = tracksInFile.Select(t => CueSheet.TypeToToken(t.Type).sectorSize).Distinct().ToList();
            if (sizes.Count > 1)
                gaps.Add($"'{f}' mixes sector sizes {string.Join("/", sizes)} in one file");
            int sectorSize = sizes.Max();

            string path = Path.IsPathRooted(f) ? f : Path.Combine(dir, f);
            if (!File.Exists(path)) { allPresent = false; coverageProven = false; gaps.Add($"data file '{f}' is missing"); continue; }

            long bytes = new FileInfo(path).Length;
            if (bytes % sectorSize != 0)
            {
                wholeSector = false;
                gaps.Add($"'{f}' is {bytes:N0} B — not a whole number of {sectorSize}-byte sectors");
            }
            long fileSectors = bytes / sectorSize;
            totalSectors += fileSectors;

            // Per-sector coverage proof: every track's INDEX 01 must fall inside this file's actual sector
            // count, and consecutive tracks (in track-number order) must claim STRICTLY increasing start
            // sectors — two tracks can sum to the right total while one silently skips or duplicates a range,
            // and only checking each track's start position against its neighbours catches that. INDEX 00
            // pregap timing is deliberately NOT checked here (see the CoverageProven doc comment): whether a
            // pregap's samples are physically stored in the file is an authoring-tool choice, not a defect.
            long? prevStart = null;
            int? prevTrackNum = null;
            foreach (var t in tracksInFile)
            {
                var i1 = t.Indices.FirstOrDefault(i => i.Number == 1) ?? t.Indices.FirstOrDefault();
                if (i1 is null)
                {
                    gaps.Add($"track {t.Number} has no INDEX 01");
                    coverageProven = false;
                    continue;
                }
                long start = i1.Time.ToSectors();
                if (start < 0 || start >= fileSectors)
                {
                    gaps.Add($"track {t.Number} INDEX 01 ({i1.Time}) is outside '{f}' ({fileSectors:N0} sectors) " +
                             "— references a sector the file doesn't have");
                    coverageProven = false;
                }
                if (prevStart is long ps && start <= ps)
                {
                    gaps.Add($"track {t.Number} INDEX 01 ({i1.Time}) does not come after track {prevTrackNum}'s " +
                             $"({Msf.FromSectors(ps)}) in '{f}' — indices out of order or duplicated, so a real " +
                             "sector range is either skipped or claimed twice");
                    coverageProven = false;
                }
                prevStart = start;
                prevTrackNum = t.Number;
            }
        }

        // Subchannel sidecar: try <cue-base>.sub and each bin's <base>.sub.
        var subCandidates = new List<string> { Path.ChangeExtension(cuePath, ".sub") };
        subCandidates.AddRange(files.Select(f => Path.Combine(dir, Path.ChangeExtension(f, ".sub"))));
        string? subPath = subCandidates.FirstOrDefault(File.Exists);

        bool subPresent = subPath is not null;
        long? subSectors = null;
        bool subMatches = false;
        if (subPath is not null)
        {
            long subBytes = new FileInfo(subPath).Length;
            subSectors = subBytes / SubchannelBytesPerSector;
            subMatches = subSectors == totalSectors && totalSectors > 0;
            if (subBytes % SubchannelBytesPerSector != 0)
                gaps.Add($"subchannel '{Path.GetFileName(subPath)}' is not a whole number of 96-byte packs");
            else if (!subMatches)
                gaps.Add($"subchannel covers {subSectors:N0} sector(s) but the data is {totalSectors:N0}");
        }

        int sessions = cue.Tracks.Select(t => t.Session).DefaultIfEmpty(1).Distinct().Count();

        var notRepresentable = new List<string>
        {
            "lead-in / lead-out / PMA / ATIP (physical-only — a drive dump sidecar is required to attest these)",
        };
        if (!subPresent)
            notRepresentable.Add("subchannel (no .sub sidecar — any LibCrypt/protection data in Q would be lost)");

        return new DumpCompletenessResult
        {
            TrackCount = cue.Tracks.Count,
            SessionCount = Math.Max(sessions, 1),
            TotalSectors = totalSectors,
            BinFiles = files,
            AllBinsPresent = allPresent,
            WholeSector = wholeSector,
            CoverageProven = coverageProven,
            SubchannelPresent = subPresent,
            SubchannelSectors = subSectors,
            SubchannelMatches = subMatches,
            Gaps = gaps,
            NotRepresentable = notRepresentable,
        };
    }

    public static string Render(DumpCompletenessResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder(r.Summary());
        foreach (var n in r.NotRepresentable)
            sb.Append($"\n  · cannot be in a bin/cue: {n}");
        return sb.ToString();
    }
}
