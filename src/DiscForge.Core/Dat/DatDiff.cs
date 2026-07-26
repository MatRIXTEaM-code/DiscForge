// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Dat;

/// <summary>A game present in both DATs whose rom set changed.</summary>
public sealed record DatGameChange(string Game, string Detail);

/// <summary>What changed between two DAT revisions.</summary>
public sealed record DatDiffReport
{
    /// <summary>Games in the new DAT but not the old.</summary>
    public required IReadOnlyList<string> Added { get; init; }
    /// <summary>Games in the old DAT but not the new.</summary>
    public required IReadOnlyList<string> Removed { get; init; }
    /// <summary>Games in both whose rom set (names / sizes / hashes) differs.</summary>
    public required IReadOnlyList<DatGameChange> Changed { get; init; }
    public required int OldGames { get; init; }
    public required int NewGames { get; init; }

    public bool Identical => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

/// <summary>
/// Compares two revisions of a DAT — the everyday task when a preservation set updates —
/// and reports which games were added, removed, or had their catalogued dump change
/// (a redump, a new revision, a corrected hash). Pure over two parsed
/// <see cref="DatFile"/>s.
/// </summary>
public static class DatDiff
{
    public static DatDiffReport Compare(DatFile oldDat, DatFile newDat)
    {
        ArgumentNullException.ThrowIfNull(oldDat);
        ArgumentNullException.ThrowIfNull(newDat);

        var oldGames = Group(oldDat);
        var newGames = Group(newDat);

        var added = newGames.Keys.Where(g => !oldGames.ContainsKey(g)).OrderBy(g => g, StringComparer.Ordinal).ToList();
        var removed = oldGames.Keys.Where(g => !newGames.ContainsKey(g)).OrderBy(g => g, StringComparer.Ordinal).ToList();

        var changed = new List<DatGameChange>();
        foreach (var g in newGames.Keys.Where(oldGames.ContainsKey).OrderBy(g => g, StringComparer.Ordinal))
        {
            var before = Signature(oldGames[g]);
            var after = Signature(newGames[g]);
            if (before != after)
                changed.Add(new DatGameChange(g, DescribeChange(oldGames[g], newGames[g])));
        }

        return new DatDiffReport
        {
            Added = added,
            Removed = removed,
            Changed = changed,
            OldGames = oldGames.Count,
            NewGames = newGames.Count,
        };
    }

    private static Dictionary<string, List<DatRom>> Group(DatFile dat) =>
        dat.Roms.GroupBy(r => r.Game, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    // A stable signature of a game's rom set: each rom's name/size/sha1-or-crc, sorted.
    private static string Signature(List<DatRom> roms) =>
        string.Join("|", roms
            .Select(r => $"{r.Name}:{r.Size}:{r.Sha1 ?? r.Crc ?? ""}")
            .OrderBy(s => s, StringComparer.Ordinal));

    private static string DescribeChange(List<DatRom> before, List<DatRom> after)
    {
        if (before.Count != after.Count)
            return $"{before.Count} → {after.Count} rom(s)";
        var beforeHashes = before.Select(r => r.Sha1 ?? r.Crc).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int rehashed = after.Count(r => !beforeHashes.Contains(r.Sha1 ?? r.Crc));
        return rehashed > 0 ? $"{rehashed} rom(s) re-hashed" : "renamed or resized";
    }
}
