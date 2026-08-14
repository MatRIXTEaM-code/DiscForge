// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Dat;

/// <summary>One catalogued game (a DAT &lt;game&gt;) with its name parsed and its roms.</summary>
public sealed record DatGameRef
{
    public required string Game { get; init; }
    public required GameNameInfo Parsed { get; init; }
    public required IReadOnlyList<DatRom> Roms { get; init; }
    /// <summary>The parent game when this is a clone (from the DAT), else null.</summary>
    public string? CloneOf { get; init; }
}

/// <summary>The winner and losers within one game family.</summary>
public sealed record OneGameChoice
{
    /// <summary>The grouping key shown to the user (base title, plus disc when multi-disc).</summary>
    public required string Family { get; init; }
    public required DatGameRef Chosen { get; init; }
    public required IReadOnlyList<DatGameRef> Rejected { get; init; }
}

/// <summary>The full 1G1R result over a DAT.</summary>
public sealed record OneGameOneRomReport
{
    public required IReadOnlyList<OneGameChoice> Choices { get; init; }
    public IReadOnlyList<DatGameRef> ChosenGames => Choices.Select(c => c.Chosen).ToList();
    public int TotalGames { get; init; }
    public int Families => Choices.Count;
}

/// <summary>How to pick the one keeper per family.</summary>
public sealed class OneGameOneRomOptions
{
    /// <summary>Region names in preference order; earlier wins. Default USA &gt; World &gt; Europe &gt; Japan.</summary>
    public IReadOnlyList<string> RegionPriority { get; init; } = new[] { "USA", "World", "Europe", "Japan" };
    /// <summary>Drop beta / proto / demo / sample builds when a final exists in the family.</summary>
    public bool ExcludePrerelease { get; init; } = true;
    /// <summary>Drop unlicensed / aftermarket entries when a licensed one exists in the family.</summary>
    public bool ExcludeUnlicensed { get; init; } = false;
}

/// <summary>
/// The "one game, one ROM" set builder: collapses every regional/revision variant of
/// a game down to the single best copy, using the region and status encoded in the
/// No-Intro / Redump name (see <see cref="GameName"/>). Multi-disc games keep each
/// disc (Disc 1, Disc 2 …) as its own choice. Pure over a parsed <see cref="DatFile"/>
/// — it selects catalogued entries, it does not touch any game data.
/// </summary>
public static class OneGameOneRom
{
    public static OneGameOneRomReport Build(DatFile dat, OneGameOneRomOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dat);
        options ??= new OneGameOneRomOptions();

        // 1) DAT roms → distinct games (grouped by the DAT game name), name parsed.
        var games = dat.Roms
            .GroupBy(r => r.Game, StringComparer.Ordinal)
            .Select(g => new DatGameRef
            {
                Game = g.Key,
                Parsed = GameName.Parse(g.Key),
                Roms = g.ToList(),
                CloneOf = g.Select(r => r.CloneOf).FirstOrDefault(c => c is not null),
            })
            .ToList();

        // 2) Family key: the parent's base title (clones fold into their parent), plus
        //    the disc number so multi-disc games keep every disc.
        var titleByGame = games.ToDictionary(g => g.Game, g => g.Parsed.Title, StringComparer.Ordinal);
        string FamilyKey(DatGameRef g)
        {
            string parentTitle = g.CloneOf is not null && titleByGame.TryGetValue(g.CloneOf, out var pt) && pt.Length > 0
                ? pt : g.Parsed.Title;
            string key = parentTitle.ToLowerInvariant();
            return g.Parsed.Disc > 0 ? $"{key}|disc{g.Parsed.Disc}" : key;
        }

        var choices = new List<OneGameChoice>();
        foreach (var family in games.GroupBy(FamilyKey).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var candidates = family.ToList();

            // Filter prerelease / unlicensed only when a "better" alternative survives.
            if (options.ExcludePrerelease && candidates.Any(c => !c.Parsed.IsPrerelease))
                candidates = candidates.Where(c => !c.Parsed.IsPrerelease).ToList();
            if (options.ExcludeUnlicensed && candidates.Any(c => !c.Parsed.IsUnlicensed))
                candidates = candidates.Where(c => !c.Parsed.IsUnlicensed).ToList();

            var ordered = candidates
                .OrderBy(c => RegionRank(c, options.RegionPriority))
                .ThenBy(c => c.Parsed.IsPrerelease ? 1 : 0)
                .ThenBy(c => c.Parsed.IsUnlicensed ? 1 : 0)
                .ThenByDescending(c => c.Parsed.Revision)
                .ThenBy(c => c.Parsed.Tags.Count)
                .ThenBy(c => c.Game, StringComparer.Ordinal)
                .ToList();

            var chosen = ordered[0];
            var rejected = family.Where(g => !ReferenceEquals(g, chosen)).ToList();
            string display = chosen.Parsed.Disc > 0 ? $"{chosen.Parsed.Title} (Disc {chosen.Parsed.Disc})" : chosen.Parsed.Title;
            choices.Add(new OneGameChoice { Family = display, Chosen = chosen, Rejected = rejected });
        }

        return new OneGameOneRomReport { Choices = choices, TotalGames = games.Count };
    }

    // Lower is better. The best (lowest-index) region present in the game wins; a game
    // with no listed-priority region ranks after all prioritised ones.
    private static int RegionRank(DatGameRef g, IReadOnlyList<string> priority)
    {
        int best = int.MaxValue;
        foreach (var region in g.Parsed.Regions)
        {
            for (int i = 0; i < priority.Count; i++)
                if (string.Equals(priority[i], region, StringComparison.OrdinalIgnoreCase) && i < best)
                    best = i;
        }
        return best == int.MaxValue ? priority.Count : best;
    }
}
