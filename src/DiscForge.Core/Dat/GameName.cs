// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text.RegularExpressions;

namespace DiscForge.Core.Dat;

/// <summary>The pieces parsed out of a No-Intro / Redump game name.</summary>
public sealed record GameNameInfo
{
    /// <summary>The title with every parenthetical tag removed and trimmed.</summary>
    public required string Title { get; init; }
    /// <summary>Canonical region names in the order they appeared (e.g. "USA", "Europe").</summary>
    public required IReadOnlyList<string> Regions { get; init; }
    /// <summary>Two-letter language codes (upper-case) when the name lists them; else empty.</summary>
    public required IReadOnlyList<string> Languages { get; init; }
    /// <summary>Revision number (Rev 1 → 1, Rev A → 1, v1.1 → best-effort); 0 when none.</summary>
    public required int Revision { get; init; }
    /// <summary>A beta / proto / demo / sample / alpha build rather than a final release.</summary>
    public required bool IsPrerelease { get; init; }
    /// <summary>Unlicensed / aftermarket / pirate tag present.</summary>
    public required bool IsUnlicensed { get; init; }
    /// <summary>Disc number when the name says "(Disc N)"; 0 when single-disc.</summary>
    public required int Disc { get; init; }
    /// <summary>Every parenthetical tag, verbatim, in order.</summary>
    public required IReadOnlyList<string> Tags { get; init; }
}

/// <summary>
/// Parses the community naming convention that No-Intro and Redump use — a base
/// title followed by parenthetical tags for region, languages, revision and status
/// (e.g. <c>Chrono Trigger (USA) (Rev 1)</c>, <c>Metal Slug (Europe) (En,Fr,De)</c>).
/// This is what makes region-aware set building ("1G1R") possible without a separate
/// metadata source: the region and status are encoded in the name itself.
/// </summary>
public static class GameName
{
    // The regions No-Intro/Redump use, with a default language for language-priority
    // fallback when a name gives a region but no explicit language list.
    private static readonly Dictionary<string, string> RegionLang = new(StringComparer.OrdinalIgnoreCase)
    {
        ["World"] = "EN", ["USA"] = "EN", ["Europe"] = "EN", ["Japan"] = "JA",
        ["Australia"] = "EN", ["Canada"] = "EN", ["UK"] = "EN", ["Ireland"] = "EN",
        ["Germany"] = "DE", ["France"] = "FR", ["Spain"] = "ES", ["Italy"] = "IT",
        ["Netherlands"] = "NL", ["Sweden"] = "SV", ["Norway"] = "NO", ["Denmark"] = "DA",
        ["Finland"] = "FI", ["Portugal"] = "PT", ["Brazil"] = "PT", ["Mexico"] = "ES",
        ["Korea"] = "KO", ["China"] = "ZH", ["Taiwan"] = "ZH", ["Hong Kong"] = "ZH",
        ["Russia"] = "RU", ["Poland"] = "PL", ["Greece"] = "EL", ["Asia"] = "EN",
    };

    private static readonly HashSet<string> Languages2 = new(StringComparer.OrdinalIgnoreCase)
    {
        "En","Ja","Fr","De","Es","It","Nl","Pt","Sv","No","Da","Fi","Zh","Ko","Pl","Ru","El","Cs","Hu","Ca","Gd","Tr",
    };

    private static readonly Regex TagRx = new(@"\(([^()]*)\)", RegexOptions.Compiled);
    private static readonly Regex RevRx = new(@"^(?:Rev\s+([0-9A-Za-z]+)|v([0-9][0-9.]*))$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DiscRx = new(@"^Disc\s+(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static GameNameInfo Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var regions = new List<string>();
        var languages = new List<string>();
        var tags = new List<string>();
        int revision = 0, disc = 0;
        bool prerelease = false, unlicensed = false;

        foreach (Match m in TagRx.Matches(name))
        {
            string body = m.Groups[1].Value.Trim();
            if (body.Length == 0) continue;
            tags.Add(body);

            // Regions: the whole tag, or a comma-list, matches known region names.
            if (RegionLang.ContainsKey(body)) { regions.Add(Canonical(body)); continue; }
            var parts = body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts.All(p => RegionLang.ContainsKey(p)))
            {
                foreach (var p in parts) regions.Add(Canonical(p));
                continue;
            }

            // Languages: a comma-list of two-letter codes.
            if (parts.Length > 0 && parts.All(p => Languages2.Contains(p)))
            {
                foreach (var p in parts) languages.Add(p.ToUpperInvariant());
                continue;
            }

            // Revision / version.
            var rev = RevRx.Match(body);
            if (rev.Success)
            {
                if (rev.Groups[1].Success)
                    revision = int.TryParse(rev.Groups[1].Value, out int rn) ? rn
                             : rev.Groups[1].Value.Length == 1 ? char.ToUpperInvariant(rev.Groups[1].Value[0]) - 'A' + 1 : 1;
                else if (rev.Groups[2].Success)
                    revision = (int)Math.Round(double.Parse(rev.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * 10);
                continue;
            }

            var d = DiscRx.Match(body);
            if (d.Success) { disc = int.Parse(d.Groups[1].Value); continue; }

            string lower = body.ToLowerInvariant();
            if (lower.StartsWith("beta") || lower.StartsWith("proto") || lower.StartsWith("demo") ||
                lower.StartsWith("sample") || lower.StartsWith("alpha") || lower.StartsWith("preview"))
                prerelease = true;
            if (lower is "unl" || lower.StartsWith("unl") || lower.Contains("aftermarket") || lower.Contains("pirate"))
                unlicensed = true;
        }

        string title = TagRx.Replace(name, "").Trim();
        title = Regex.Replace(title, @"\s{2,}", " ");

        return new GameNameInfo
        {
            Title = title,
            Regions = regions,
            Languages = languages,
            Revision = revision,
            IsPrerelease = prerelease,
            IsUnlicensed = unlicensed,
            Disc = disc,
            Tags = tags,
        };
    }

    /// <summary>The default language for a region (for language-priority fallback), or "EN".</summary>
    public static string DefaultLanguage(string region) =>
        RegionLang.TryGetValue(region, out var l) ? l : "EN";

    private static string Canonical(string region) =>
        RegionLang.Keys.First(k => string.Equals(k, region, StringComparison.OrdinalIgnoreCase));
}
