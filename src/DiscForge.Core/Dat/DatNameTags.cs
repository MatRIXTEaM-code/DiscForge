// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text.RegularExpressions;

namespace DiscForge.Core.Dat;

/// <summary>The structured fields No-Intro/Redump pack into a catalogued game's name/description, e.g.
/// "Super Smash Bros. Melee (USA) (Rev 2)" or "Interactive Multi-Game Demo Disc (USA) (Demo) (v35)".</summary>
public sealed record DatNameTags
{
    public required string Title { get; init; }
    public required IReadOnlyList<string> Regions { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public int? Revision { get; init; }
    public string? VersionRaw { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscCount { get; init; }
    public bool IsDemo { get; init; }
    public bool IsKiosk { get; init; }
    public bool IsBeta { get; init; }
    public bool IsProto { get; init; }
    public bool IsUnlicensed { get; init; }
    public bool IsSample { get; init; }
    public bool IsAlt { get; init; }
    public IReadOnlyList<string> OtherTags { get; init; } = Array.Empty<string>();

    /// <summary>False for anything that isn't a normal, full retail release (demo/kiosk/beta/proto/sample).</summary>
    public bool IsRetailFull => !(IsDemo || IsKiosk || IsBeta || IsProto || IsSample);

    public string Summary()
    {
        var bits = new List<string>();
        if (Regions.Count > 0) bits.Add(string.Join("/", Regions));
        if (Revision is { } r) bits.Add($"Rev {r}");
        if (VersionRaw is { Length: > 0 }) bits.Add(VersionRaw);
        if (DiscNumber is { } d) bits.Add(DiscCount is { } c ? $"Disc {d} of {c}" : $"Disc {d}");
        if (IsDemo) bits.Add("Demo");
        if (IsKiosk) bits.Add("Kiosk");
        if (IsBeta) bits.Add("Beta");
        if (IsProto) bits.Add("Prototype");
        if (IsUnlicensed) bits.Add("Unlicensed");
        if (IsSample) bits.Add("Sample");
        if (IsAlt) bits.Add("Alt");
        return bits.Count == 0 ? Title : $"{Title} [{string.Join(", ", bits)}]";
    }
}

/// <summary>
/// Parses the public No-Intro/Redump catalogued-name convention — "Title (Region[, Region…])
/// [(Languages)] (Rev N) (Demo|Kiosk|Beta|Proto|Unl|Sample|Alt) (Disc N of M) (vX.Y)…" — into structured
/// fields. This turns a plain <see cref="DatFile"/> hash match into a revision/variant-aware one: once
/// <see cref="DatFile.Verify"/> says WHICH catalogued dump a file's hashes match, this says which
/// revision, region, and disc-type (retail / demo / kiosk / unlicensed / prototype) that entry is,
/// without the caller having to hand-parse the free-text game name.
///
/// This is a documented, public naming convention shared across No-Intro/Redump-style DATs for every
/// system — not GameCube-specific — but it is what makes a GameCube dump's ring-code revision (see
/// <c>DiscForge.Core.GameCube.GcRingCode</c>) checkable against the DAT-matched entry's own declared
/// revision: parse the matched rom's name/description here, then pass the result's
/// <see cref="Revision"/> as <c>expectedRomRevision</c> to <c>GcRingCodeCheck.CrossCheck</c>.
///
/// Best-effort: a tag this parser doesn't recognize is kept verbatim in <see cref="DatNameTags.OtherTags"/>
/// rather than silently dropped or guessed at.
/// </summary>
public static class DatNameTagParser
{
    private static readonly HashSet<string> KnownRegionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "USA", "Europe", "Japan", "World", "Korea", "Asia", "Australia", "Brazil", "Canada", "China",
        "Denmark", "Finland", "France", "Germany", "Greece", "Hong Kong", "Ireland", "Israel", "Italy",
        "Netherlands", "New Zealand", "Norway", "Poland", "Portugal", "Russia", "Scandinavia", "Spain",
        "Sweden", "Switzerland", "Taiwan", "UK", "United Kingdom", "Latin America", "Unknown",
    };

    private static readonly Regex ParenGroupRx = new(@"\(([^()]*)\)", RegexOptions.Compiled);
    private static readonly Regex RevRx = new(@"^Rev(?:ision)?\s*([A-Za-z0-9]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VerRx = new(@"^v[\d][\d.]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DiscRx = new(@"^Disc\s+(\d+)(?:\s+of\s+(\d+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AltRx = new(@"^Alt\s*\d*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BetaRx = new(@"^Beta\s*\d*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static DatNameTags Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int firstParen = name.IndexOf('(');
        string title = (firstParen >= 0 ? name[..firstParen] : name).Trim();

        var groups = ParenGroupRx.Matches(name).Select(m => m.Groups[1].Value.Trim()).ToList();

        var regions = new List<string>();
        var languages = new List<string>();
        int? revision = null, discNum = null, discCount = null;
        string? versionRaw = null;
        bool demo = false, kiosk = false, beta = false, proto = false, unl = false, sample = false, alt = false;
        var other = new List<string>();
        bool regionConsumed = false;

        foreach (var g in groups)
        {
            if (g.Length == 0) continue;
            var parts = g.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (!regionConsumed && parts.Length > 0 && parts.All(p => KnownRegionWords.Contains(p)))
            {
                regions.AddRange(parts);
                regionConsumed = true;
                continue;
            }

            var rev = RevRx.Match(g);
            if (rev.Success)
            {
                if (int.TryParse(rev.Groups[1].Value, out int rv)) revision = rv;
                else other.Add(g);
                continue;
            }
            if (VerRx.IsMatch(g)) { versionRaw = g; continue; }

            var disc = DiscRx.Match(g);
            if (disc.Success)
            {
                discNum = int.Parse(disc.Groups[1].Value);
                if (disc.Groups[2].Success) discCount = int.Parse(disc.Groups[2].Value);
                continue;
            }

            if (g.StartsWith("Demo", StringComparison.OrdinalIgnoreCase)) { demo = true; continue; }
            if (g.Contains("Kiosk", StringComparison.OrdinalIgnoreCase)) { kiosk = true; continue; }
            if (BetaRx.IsMatch(g)) { beta = true; continue; }
            if (g.StartsWith("Proto", StringComparison.OrdinalIgnoreCase)) { proto = true; continue; }
            if (g.Equals("Unl", StringComparison.OrdinalIgnoreCase) || g.Contains("Unlicensed", StringComparison.OrdinalIgnoreCase)) { unl = true; continue; }
            if (g.Equals("Sample", StringComparison.OrdinalIgnoreCase)) { sample = true; continue; }
            if (AltRx.IsMatch(g)) { alt = true; continue; }

            // A comma-separated run of bare two-letter codes is the language group (e.g. "En,Fr,De,Es,It").
            if (parts.Length > 1 && parts.All(p => p.Length == 2 && p.All(char.IsLetter)))
            {
                languages.AddRange(parts);
                continue;
            }

            other.Add(g);
        }

        return new DatNameTags
        {
            Title = title,
            Regions = regions,
            Languages = languages,
            Revision = revision,
            VersionRaw = versionRaw,
            DiscNumber = discNum,
            DiscCount = discCount,
            IsDemo = demo,
            IsKiosk = kiosk,
            IsBeta = beta,
            IsProto = proto,
            IsUnlicensed = unl,
            IsSample = sample,
            IsAlt = alt,
            OtherTags = other,
        };
    }
}
