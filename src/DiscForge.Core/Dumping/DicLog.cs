// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Globalization;
using System.Text.RegularExpressions;

namespace DiscForge.Core.Dumping;

/// <summary>One track's hashes and type, as recorded in a DiscImageCreator .log file.</summary>
public sealed record DicTrackInfo
{
    public required int Track { get; init; }
    public string? Type { get; init; }             // "Audio" | "Data" | null if not stated
    public string? Crc32 { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
}

/// <summary>
/// What <see cref="DicLogParser"/> could extract from a DiscImageCreator (DIC) .log file — the log format
/// that has become close to a de facto submission standard in the Redump community, and one DiscForge does
/// not yet produce on its own. This is a read-only importer: it lets a dump someone already made with DIC be
/// brought into DiscForge's own tooling (redump-diff, dump-ledger) without re-dumping the disc, and it lets
/// someone comparing the two tools see exactly what DIC recorded. It creates nothing DIC didn't already state
/// and verifies nothing; that's still the drive's and the hash's job, not this parser's.
/// </summary>
public sealed record DicLogInfo
{
    public required string SourcePath { get; init; }
    public string? DicVersion { get; init; }
    public string? CommandLine { get; init; }
    public string? DriveVendor { get; init; }
    public string? DriveProduct { get; init; }
    public string? DriveRevision { get; init; }
    public string? MediaType { get; init; }
    public int? WriteOffset { get; init; }
    public int? TotalErrors { get; init; }
    public int? C2ErrorCount { get; init; }
    public IReadOnlyList<long> C2ErrorLbas { get; init; } = Array.Empty<long>();
    public IReadOnlyList<DicTrackInfo> Tracks { get; init; } = Array.Empty<DicTrackInfo>();

    public bool LooksClean => (TotalErrors is 0 or null) && C2ErrorLbas.Count == 0;

    public string Summary()
    {
        var bits = new List<string>();
        if (DicVersion is { Length: > 0 }) bits.Add($"DiscImageCreator {DicVersion}");
        if (DriveVendor is { Length: > 0 } || DriveProduct is { Length: > 0 })
            bits.Add($"drive: {DriveVendor} {DriveProduct}".Trim());
        if (MediaType is { Length: > 0 }) bits.Add($"media: {MediaType}");
        bits.Add($"{Tracks.Count} track(s)");
        bits.Add(LooksClean ? "no errors recorded" : $"{C2ErrorLbas.Count} C2 error LBA(s), TotalErrors={TotalErrors?.ToString() ?? "?"}");
        return string.Join(", ", bits);
    }
}

/// <summary>
/// Best-effort parser for DiscImageCreator .log files. DIC's log format has drifted across versions and was
/// never formally specified, so this is deliberately tolerant: every field is optional, unrecognised lines are
/// skipped rather than rejected, and a log this can't fully parse still yields whatever it could find rather
/// than throwing. Fields not present in a given log (older/newer DIC versions, different media types) are left
/// null — this never guesses a value the log didn't actually state.
/// </summary>
public static class DicLogParser
{
    private static readonly Regex VersionRe = new(@"DiscImageCreator\s+([\d.]+)", RegexOptions.IgnoreCase);
    private static readonly Regex CommandRe = new(@"^Command line:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex VendorRe = new(@"VendorId\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex ProductRe = new(@"ProductId\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex RevisionRe = new(@"ProductRevisionLevel\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex MediaRe = new(@"(?:Disc Type|Media Type|DiscType)\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex WriteOffsetRe = new(@"(?:Combined Offset|Write Offset|CombinedOffset)\s*:?\s*(-?\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex TotalErrorsRe = new(@"TotalErrors?\s*(?:\[[^\]]*\])?\s*:?\s*(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex C2SummaryRe = new(@"Number of C2 error\(?s?\)?\s*:?\s*(\d+)", RegexOptions.IgnoreCase);
    // DIC C2/error report lines look like "LBA[  12345, 0x00003039]" or "[C2 Error] LBA 12345".
    private static readonly Regex C2LbaRe = new(@"LBA\[?\s*(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex TrackHashRe = new(@"^\s*Track\s*0*(\d+)\s*:\s*([0-9A-Fa-f]+)\s*$", RegexOptions.Multiline);
    private static readonly Regex TrackTypeRe = new(@"Track\s*0*(\d+)\s+(Audio|Data|Mode\s*[12])", RegexOptions.IgnoreCase);

    public static DicLogInfo Parse(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string text = File.ReadAllText(path);
        return ParseText(text, path);
    }

    public static DicLogInfo ParseText(string text, string sourcePath = "(text)")
    {
        ArgumentNullException.ThrowIfNull(text);

        string? dicVersion = Match1(VersionRe, text);
        string? cmdLine = Match1(CommandRe, text);
        string? vendor = Match1(VendorRe, text);
        string? product = Match1(ProductRe, text);
        string? revision = Match1(RevisionRe, text);
        string? media = Match1(MediaRe, text);

        int? writeOffset = null;
        var wo = WriteOffsetRe.Match(text);
        if (wo.Success && int.TryParse(wo.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var woVal))
            writeOffset = woVal;

        int? totalErrors = null;
        var te = TotalErrorsRe.Match(text);
        if (te.Success && int.TryParse(te.Groups[1].Value, out var teVal)) totalErrors = teVal;

        int? c2Count = null;
        var c2s = C2SummaryRe.Match(text);
        if (c2s.Success && int.TryParse(c2s.Groups[1].Value, out var c2Val)) c2Count = c2Val;

        var c2Lbas = new List<long>();
        // Only scan a plausible error-report region ("[NO ERROR]" means skip entirely) to avoid false hits
        // elsewhere in the log (e.g. TOC lines that also contain the substring "LBA").
        if (!text.Contains("[NO ERROR]", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match m in C2LbaRe.Matches(text))
            {
                if (long.TryParse(m.Groups[1].Value, out var lba)) c2Lbas.Add(lba);
            }
        }
        c2Lbas = c2Lbas.Distinct().OrderBy(x => x).ToList();

        // Track hashes: DIC emits three blocks in sequence (CRC32 hash / MD5 hash / SHA1 hash), each listing
        // every track. We walk the text once, remembering which block we're in.
        var byTrack = new Dictionary<int, DicTrackInfo>();
        string? block = null;
        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Contains("CRC32 hash", StringComparison.OrdinalIgnoreCase)) { block = "crc32"; continue; }
            if (line.Contains("MD5 hash", StringComparison.OrdinalIgnoreCase)) { block = "md5"; continue; }
            if (line.Contains("SHA1 hash", StringComparison.OrdinalIgnoreCase)) { block = "sha1"; continue; }
            if (block is null) continue;

            var hm = TrackHashRe.Match(line);
            if (!hm.Success) continue;
            int track = int.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture);
            string hash = hm.Groups[2].Value;
            if (!byTrack.TryGetValue(track, out var info))
                info = new DicTrackInfo { Track = track };
            byTrack[track] = block switch
            {
                "crc32" => info with { Crc32 = hash },
                "md5" => info with { Md5 = hash },
                "sha1" => info with { Sha1 = hash },
                _ => info,
            };
        }

        foreach (Match m in TrackTypeRe.Matches(text))
        {
            int track = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string type = m.Groups[2].Value.StartsWith("Mode", StringComparison.OrdinalIgnoreCase) ? "Data" : m.Groups[2].Value;
            if (!byTrack.TryGetValue(track, out var info)) info = new DicTrackInfo { Track = track };
            byTrack[track] = info with { Type = info.Type ?? type };
        }

        return new DicLogInfo
        {
            SourcePath = sourcePath,
            DicVersion = dicVersion,
            CommandLine = cmdLine,
            DriveVendor = vendor,
            DriveProduct = product,
            DriveRevision = revision,
            MediaType = media,
            WriteOffset = writeOffset,
            TotalErrors = totalErrors,
            C2ErrorCount = c2Count ?? (c2Lbas.Count > 0 ? c2Lbas.Count : null),
            C2ErrorLbas = c2Lbas,
            Tracks = byTrack.Values.OrderBy(t => t.Track).ToList(),
        };
    }

    private static string? Match1(Regex re, string text)
    {
        var m = re.Match(text);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
