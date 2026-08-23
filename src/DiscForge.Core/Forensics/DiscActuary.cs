// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscForge.Core.Forensics;

/// <summary>One quality scan of one disc, as the actuary stores it: when, the
/// medium-agnostic error triple (first-tier correctable / second-tier / hard
/// uncorrectable — C1/C2/CU on CD, PIE/PIF/POF on DVD), and where it came from.</summary>
public sealed record ActuaryScan(string WhenUtc, double Tier1Max, double Tier2Max, double Uncorrectable,
                                 string? Drive = null, string? Source = null);

/// <summary>A disc's longitudinal scan history — the time series everything
/// else in the actuary is built on.</summary>
public sealed record DiscScanHistory
{
    public string FormatVersion => "dact/1";
    /// <summary>A stable identity for the disc. The genome ShortId is ideal
    /// (content-derived, offset-invariant); any user label works.</summary>
    public required string DiscId { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<ActuaryScan> Scans { get; init; } = Array.Empty<ActuaryScan>();

    public DiscScanHistory Append(ActuaryScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return this with
        {
            Scans = Scans.Append(scan)
                .OrderBy(s => DateTimeOffset.Parse(s.WhenUtc, CultureInfo.InvariantCulture)).ToList(),
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static DiscScanHistory Load(string path) =>
        JsonSerializer.Deserialize<DiscScanHistory>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidDataException($"'{path}' is not a disc scan history.");
}

/// <summary>The actuary's assessment of one disc.</summary>
public sealed record ActuaryVerdict
{
    public required string DiscId { get; init; }
    public string? Title { get; init; }
    public required int ScanCount { get; init; }
    /// <summary>Latest measured values.</summary>
    public required double LatestTier1 { get; init; }
    public required double LatestUncorrectable { get; init; }
    /// <summary>The kinetics fit — null when fewer than 3 scans exist (a trend
    /// needs a time series, and the actuary says so instead of guessing).</summary>
    public RotKineticsResult? Kinetics { get; init; }

    public bool AlreadyFailing => LatestUncorrectable > 0 || Kinetics?.AlreadyFailing == true;
    public double? YearsRemaining => Kinetics?.YearsToThreshold;

    /// <summary>Sort key: failing discs first, then shortest projected life,
    /// then fastest growth; stable and unknown-trend discs last.</summary>
    public double Urgency =>
        AlreadyFailing ? double.MaxValue
        : YearsRemaining is { } y ? 1000.0 / Math.Max(y, 0.001)
        : Kinetics is { GrowthPerYear: > 0 } k ? k.GrowthPerYear
        : 0;

    public string Headline =>
        AlreadyFailing ? $"FAILING NOW — {LatestUncorrectable:0} uncorrectable at last scan"
        : YearsRemaining is { } y ? $"~{y:0.0} year(s) of readable life left (threshold {Kinetics!.Threshold:0})"
        : ScanCount < 3 ? $"{ScanCount} scan(s) on record — need 3+ for a trend"
        : "stable — no threshold crossing in trend";
}

/// <summary>
/// The Disc Actuary: BLER scanning says how a disc is TODAY; this says how
/// long it has LEFT, by keeping every scan as a time series and fitting
/// <see cref="RotKinetics"/>' first-order decay model per disc. The payoff is
/// a sentence no single scan can produce: "re-dump these N first — they are
/// dying fastest", computed across a whole collection and conditioned on the
/// shelf's storage environment. Prioritisation only — it schedules rescues,
/// it performs none.
/// </summary>
public static class DiscActuary
{
    /// <summary>Assess one disc from its history. Uses the first-tier error
    /// maximum as the trend metric (the earliest mover as dye degrades).</summary>
    public static ActuaryVerdict Assess(DiscScanHistory history,
        StorageEnvironment? storage = null, double threshold = RotKinetics.DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(history);
        var last = history.Scans.Count > 0 ? history.Scans[^1] : null;
        RotKineticsResult? fit = null;
        if (history.Scans.Count >= 3)
        {
            var samples = history.Scans
                .Select(s => new RotSample(DateTimeOffset.Parse(s.WhenUtc, CultureInfo.InvariantCulture), s.Tier1Max))
                .ToList();
            fit = RotKinetics.Fit(samples, threshold, storage);
        }
        return new ActuaryVerdict
        {
            DiscId = history.DiscId,
            Title = history.Title,
            ScanCount = history.Scans.Count,
            LatestTier1 = last?.Tier1Max ?? 0,
            LatestUncorrectable = last?.Uncorrectable ?? 0,
            Kinetics = fit,
        };
    }

    /// <summary>Rank a collection most-urgent first.</summary>
    public static IReadOnlyList<ActuaryVerdict> Rank(IEnumerable<DiscScanHistory> collection,
        StorageEnvironment? storage = null, double threshold = RotKinetics.DefaultThreshold)
        => collection.Select(h => Assess(h, storage, threshold))
                     .OrderByDescending(v => v.Urgency)
                     .ThenBy(v => v.DiscId, StringComparer.Ordinal)
                     .ToList();

    /// <summary>The collection triage, as text: who dies first, and why.</summary>
    public static string RenderTriage(IReadOnlyList<ActuaryVerdict> ranked, int urgentCount = 5)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        var sb = new StringBuilder();
        int urgent = ranked.Count(v => v.AlreadyFailing || v.YearsRemaining is < 2);
        sb.AppendLine($"Collection triage — {ranked.Count} disc(s), {urgent} urgent:");
        foreach (var v in ranked.Take(Math.Max(urgentCount, urgent)))
        {
            string name = v.Title is not null ? $"{v.Title} [{v.DiscId}]" : v.DiscId;
            sb.AppendLine($"  {name}: {v.Headline}" +
                (v.Kinetics is { } k && !v.AlreadyFailing && k.YearsToThreshold is not null
                    ? $" (growth {k.GrowthPerYear:P0}/yr, fit R²={k.RSquared:0.00})" : ""));
        }
        if (ranked.Count > Math.Max(urgentCount, urgent))
            sb.AppendLine($"  … {ranked.Count - Math.Max(urgentCount, urgent)} more, less urgent.");
        var first = ranked.FirstOrDefault(v => v.AlreadyFailing || v.YearsRemaining is not null);
        if (first is not null && (first.AlreadyFailing || first.YearsRemaining is < 5))
            sb.AppendLine($"  => re-dump \"{first.Title ?? first.DiscId}\" first.");
        return sb.ToString();
    }

    /// <summary>Store layout: one JSON per disc under a directory.</summary>
    public static string PathFor(string directory, string discId)
    {
        var safe = new string(discId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safe.Length == 0) safe = "disc";
        return Path.Combine(directory, safe + ".json");
    }

    public static DiscScanHistory LoadOrNew(string directory, string discId, string? title = null)
    {
        string path = PathFor(directory, discId);
        if (File.Exists(path))
        {
            var h = DiscScanHistory.Load(path);
            return title is not null && h.Title != title ? h with { Title = title } : h;
        }
        return new DiscScanHistory { DiscId = discId, Title = title };
    }

    public static IReadOnlyList<DiscScanHistory> LoadAll(string directory)
        => !Directory.Exists(directory)
            ? Array.Empty<DiscScanHistory>()
            : Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal)
                .Select(DiscScanHistory.Load).ToList();
}
