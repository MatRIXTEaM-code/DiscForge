// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text.RegularExpressions;

namespace DiscForge.Core.Dumping;

/// <summary>One file entry from the <c>dat:</c> block of a redumper .log — the same
/// <c>&lt;rom name=… size=… crc=… md5=… sha1=… /&gt;</c> line Redump's own datfiles use.</summary>
public sealed record RedumperRomEntry
{
    public required string Name { get; init; }
    public long? Size { get; init; }
    public string? Crc32 { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
}

/// <summary>
/// What <see cref="RedumperLogParser"/> could extract from a redumper .log. redumper is the dumper the
/// Redump project now prefers for CD (and increasingly DVD/BD); DiscForge's Read Disc tile can launch it
/// (and MPF, the front-end that drives it), and this is the import side of that: bring a dump someone made
/// with redumper into DiscForge's own tooling without re-reading the disc. Like
/// <see cref="DicLogInfo"/>, it is a read-only report of what the log states — it verifies nothing itself.
/// </summary>
public sealed record RedumperLogInfo
{
    public required string SourcePath { get; init; }
    public string? RedumperVersion { get; init; }
    public string? DriveVendor { get; init; }
    public string? DriveProduct { get; init; }
    public string? DriveRevision { get; init; }
    public string? DiscType { get; init; }
    /// <summary>Exactly as the log states it, e.g. <c>+2</c> or <c>-647</c>.</summary>
    public string? WriteOffset { get; init; }
    /// <summary>From the last <c>media errors:</c> block (redumper can write several across a multi-pass dump;
    /// the last is the final state). Null when the log carries no such block.</summary>
    public long? ScsiErrors { get; init; }
    public long? C2Errors { get; init; }
    public long? QErrors { get; init; }
    /// <summary>From the last <c>dat:</c> block.</summary>
    public IReadOnlyList<RedumperRomEntry> Roms { get; init; } = Array.Empty<RedumperRomEntry>();

    /// <summary>True when an error block was found and every count in it is zero.</summary>
    public bool LooksClean => (ScsiErrors ?? 0) == 0 && (C2Errors ?? 0) == 0 && (QErrors ?? 0) == 0;

    public string Summary()
    {
        var bits = new List<string>();
        bits.Add(RedumperVersion is { Length: > 0 } ? $"redumper {RedumperVersion}" : "redumper");
        if (DriveVendor is { Length: > 0 } || DriveProduct is { Length: > 0 })
            bits.Add($"drive: {DriveVendor} {DriveProduct}".Trim());
        if (DiscType is { Length: > 0 }) bits.Add($"media: {DiscType}");
        bits.Add($"{Roms.Count} file(s) in dat");
        if (ScsiErrors is null && C2Errors is null && QErrors is null)
            bits.Add("no error summary in log");
        else
            bits.Add(LooksClean
                ? "no errors recorded"
                : $"errors: SCSI={ScsiErrors?.ToString(CultureInfo.InvariantCulture) ?? "?"}, " +
                  $"C2={C2Errors?.ToString(CultureInfo.InvariantCulture) ?? "?"}, " +
                  $"Q={QErrors?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        return string.Join(", ", bits);
    }
}

/// <summary>
/// Tolerant parser for redumper .log files. Written against the log's observable line shapes (the version
/// line near the top, <c>drive:</c>, <c>disc write offset:</c>, the <c>media errors:</c> block with its
/// <c>SCSI:</c>/<c>C2:</c>/<c>Q:</c> counts, and the <c>dat:</c> block of <c>&lt;rom&gt;</c> lines); every
/// field is optional and unknown lines are skipped, since the format is not formally specified and has
/// changed between builds.
/// </summary>
public static class RedumperLogParser
{
    private static readonly Regex VersionRe = new(@"^redumper\s+(.+)$", RegexOptions.IgnoreCase);
    private static readonly Regex DriveRe = new(
        @"^(?:drive|inquiry):\s*(.+?)\s+-\s+(.+?)\s*\(revision level:\s*([^,)]*)", RegexOptions.IgnoreCase);
    private static readonly Regex CountRe = new(@"^(SCSI|C2|Q):\s*(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex AttrRe = new(@"(\w+)=""([^""]*)""");

    /// <summary>Cheap sniff: does this text look like a redumper log rather than a DiscImageCreator one?</summary>
    public static bool LooksLikeRedumperLog(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // The version line sits within the first few lines (after the dump date, possibly a warning).
        using var reader = new StringReader(text);
        for (int i = 0; i < 8; i++)
        {
            string? line = reader.ReadLine();
            if (line is null) break;
            if (VersionRe.IsMatch(line.Trim())) return true;
        }
        return false;
    }

    public static RedumperLogInfo Parse(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return ParseText(File.ReadAllText(path), path);
    }

    public static RedumperLogInfo ParseText(string text, string sourcePath = "(text)")
    {
        ArgumentNullException.ThrowIfNull(text);

        string? version = null, vendor = null, product = null, revision = null, discType = null, offset = null;
        long? scsi = null, c2 = null, q = null;
        var roms = new List<RedumperRomEntry>();

        bool inErrors = false, inDat = false;
        int lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            string line = raw.Trim();

            if (inDat)
            {
                if (line.StartsWith("<rom", StringComparison.OrdinalIgnoreCase))
                {
                    var rom = ParseRom(line);
                    if (rom is not null) roms.Add(rom);
                    continue;
                }
                inDat = false;
            }

            if (version is null && lineNo <= 8)
            {
                var vm = VersionRe.Match(line);
                if (vm.Success) { version = vm.Groups[1].Value.Trim(); continue; }
            }

            var dm = DriveRe.Match(line);
            if (dm.Success)
            {
                vendor = dm.Groups[1].Value.Trim();
                product = dm.Groups[2].Value.Trim();
                revision = dm.Groups[3].Value.Trim();
                continue;
            }

            if (line.StartsWith("disc write offset:", StringComparison.OrdinalIgnoreCase))
            {
                offset = line["disc write offset:".Length..].Trim();
                continue;
            }
            if (discType is null && line.StartsWith("disc type:", StringComparison.OrdinalIgnoreCase))
            {
                discType = line["disc type:".Length..].Trim();
                continue;
            }
            if (discType is null && line.StartsWith("current profile:", StringComparison.OrdinalIgnoreCase))
            {
                discType = line["current profile:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("dat:", StringComparison.OrdinalIgnoreCase))
            {
                // A later dat block (e.g. after a refine pass) supersedes an earlier one.
                roms.Clear();
                inDat = true;
                inErrors = false;
                continue;
            }

            // "media errors:" (and "initial dump media errors:") open a fresh count block; the last one wins.
            if (line.EndsWith("media errors:", StringComparison.OrdinalIgnoreCase))
            {
                inErrors = true;
                scsi = c2 = q = null;
                continue;
            }
            if (inErrors)
            {
                var cm = CountRe.Match(line);
                if (cm.Success && long.TryParse(cm.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    switch (cm.Groups[1].Value.ToUpperInvariant())
                    {
                        case "SCSI": scsi = n; break;
                        case "C2": c2 = n; break;
                        case "Q": q = n; break;
                    }
                    continue;
                }
                if (line.Length == 0 || line.EndsWith(':')) inErrors = false;
            }
        }

        return new RedumperLogInfo
        {
            SourcePath = sourcePath,
            RedumperVersion = version,
            DriveVendor = vendor,
            DriveProduct = product,
            DriveRevision = revision,
            DiscType = discType,
            WriteOffset = offset,
            ScsiErrors = scsi,
            C2Errors = c2,
            QErrors = q,
            Roms = roms,
        };
    }

    private static RedumperRomEntry? ParseRom(string line)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRe.Matches(line)) attrs[m.Groups[1].Value] = m.Groups[2].Value;
        if (!attrs.TryGetValue("name", out var name) || name.Length == 0) return null;
        long? size = attrs.TryGetValue("size", out var s) &&
                     long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sz) ? sz : null;
        return new RedumperRomEntry
        {
            Name = System.Net.WebUtility.HtmlDecode(name),
            Size = size,
            Crc32 = attrs.GetValueOrDefault("crc"),
            Md5 = attrs.GetValueOrDefault("md5"),
            Sha1 = attrs.GetValueOrDefault("sha1"),
        };
    }
}
