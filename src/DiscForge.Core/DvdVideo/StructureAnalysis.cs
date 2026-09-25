// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.DvdVideo;

/// <summary>How a disc's declared structure reads.</summary>
public enum StructureVerdict
{
    /// <summary>An ordinary disc: a feature and some extras, or a series.</summary>
    Normal,
    /// <summary>Unusual, but explicable — a boxset, a compilation, a disc with
    /// many short items.</summary>
    Unusual,
    /// <summary>The structure looks deliberately obstructive: decoy titles, or
    /// declared content exceeding what the media can physically hold.</summary>
    Obfuscated,
}

public sealed record StructureFinding(StructureVerdict Verdict, string Summary,
                                      IReadOnlyList<string> Evidence);

/// <summary>
/// Judges whether a disc's declared structure describes its contents or is
/// trying to obscure them.
///
/// Some DVD protection schemes — ARccOS and its relatives — author a structure
/// that is technically legal and practically nonsense: ninety-nine titles where
/// there is one film, title sets that duplicate each other, directory records
/// declaring several times more video than the disc can physically hold. A
/// player follows the menu and never notices. Software reading the tables sees
/// a wall of plausible entries and cannot tell which is real.
///
/// DiscForge does not defeat this and has no interest in doing so. But
/// presenting such a structure as though it were a genuine contents listing is
/// misleading, and a user staring at ninety-nine rows and seventy-six gigabytes
/// deserves to be told what they are looking at rather than left to conclude the
/// software is broken.
///
/// The strongest evidence is arithmetic rather than pattern-matching: a DVD-9
/// holds 8.5 GB, so a disc declaring 76 GB of video is stating something that
/// cannot be true. That is a fact about the disc, not a heuristic about how its
/// titles are arranged, and it is checked first.
/// </summary>
public static class StructureAnalysis
{
    /// <summary>Above this, a disc has more titles than any real contents list.</summary>
    private const int ManyTitles = 30;

    /// <summary>The format's ceiling. A disc at exactly 99 has either been
    /// authored to the limit or is padding to it.</summary>
    private const int MaxTitles = 99;

    /// <summary>Usable capacity of a dual-layer DVD — the largest any DVD-Video
    /// disc can be. Anything declaring more than this is declaring the
    /// impossible.</summary>
    public const long Dvd9Bytes = 8_547_991_552L;

    public static StructureFinding Judge(IfoReader.DvdStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);

        var titles = structure.Titles;
        var evidence = new List<string>();

        if (titles.Count == 0)
            return new StructureFinding(StructureVerdict.Unusual,
                "The disc declares no titles at all.",
                new[] { "An empty title table is not a normal authoring result." });

        long declared = structure.TotalVideoBytes + structure.TotalMenuBytes;

        // The decisive test, and the only one that is arithmetic rather than
        // inference: no DVD holds more than 8.5 GB, so a volume declaring more
        // is declaring content that is not there.
        bool impossible = declared > Dvd9Bytes;

        // Duplicate title sets: several sets whose video totals match exactly,
        // to the byte. Authoring different content does not produce identical
        // sizes; pointing several directory entries at the same sectors does.
        var sizeGroups = structure.TitleSets
            .Where(s => s.TitleVobBytes > 0)
            .GroupBy(s => s.TitleVobBytes)
            .OrderByDescending(g => g.Count())
            .ToList();
        var biggestGroup = sizeGroups.FirstOrDefault();
        bool duplicated = biggestGroup is not null && biggestGroup.Count() >= 3;

        var byChapters = titles.GroupBy(t => t.Chapters)
                               .OrderByDescending(g => g.Count())
                               .ToList();
        var largest = byChapters[0];
        double repetition = (double)largest.Count() / titles.Count;

        int distinctSets = titles.Select(t => t.TitleSet).Distinct().Count();

        bool many = titles.Count >= ManyTitles;
        bool repetitive = repetition > 0.5 && largest.Count() >= 10;
        bool atLimit = titles.Count >= MaxTitles;

        if (impossible || duplicated || (many && repetitive))
        {
            if (impossible)
            {
                evidence.Add($"The volume declares {Bytes(declared)} of video and menus. " +
                             $"A dual-layer DVD holds {Bytes(Dvd9Bytes)}, and a single-layer one " +
                             "about half that — so this is more content than the disc can " +
                             "physically contain.");
                evidence.Add("Directory records can declare whatever they like; the sectors " +
                             "behind them are finite. Several entries here point at the same " +
                             "physical video, which is why the total exceeds the medium.");
            }

            if (duplicated && biggestGroup is not null)
            {
                var sets = string.Join(", ", biggestGroup.Select(s => s.Number).OrderBy(n => n));
                evidence.Add($"{biggestGroup.Count()} title sets ({sets}) declare exactly " +
                             $"{Bytes(biggestGroup.Key)} each — identical to the byte. " +
                             "Independently authored content does not produce matching sizes.");
            }

            if (many && repetitive)
                evidence.Add($"{titles.Count} titles are declared, of which {largest.Count()} " +
                             $"({repetition:P0}) have exactly {largest.Key} chapters each.");

            if (atLimit)
                evidence.Add($"The count is {titles.Count}, at or near the format's maximum of 99 — " +
                             "a disc authored for content rarely approaches the limit.");

            if (distinctSets > 5)
                evidence.Add($"They are spread across {distinctSets} title sets, so no single " +
                             "set can be disregarded.");

            var emptySets = structure.TitleSets.Count(s => s.TitleVobBytes == 0);
            if (emptySets > 0)
                evidence.Add($"{emptySets} title set(s) have no video file at all — their IFOs " +
                             "exist but the VOBs they describe do not, so those titles cannot " +
                             "play anything.");

            evidence.Add("This is the shape of structural copy protection — ARccOS and similar " +
                         "schemes add decoys so that software reading the tables cannot identify " +
                         "the feature. A player follows the disc's menus and never encounters " +
                         "them.");

            evidence.Add("The disc is not damaged and the structure is legal; it is simply not " +
                         "a description of the contents.");

            string summary = impossible
                ? $"{Bytes(declared)} declared on a disc that holds at most {Bytes(Dvd9Bytes)} — " +
                  "this structure is obfuscation."
                : duplicated
                    ? $"{biggestGroup!.Count()} title sets declare identical sizes — the structure " +
                      "is obfuscation rather than a contents listing."
                    : $"{titles.Count} titles, {largest.Count()} of them identical — this table is " +
                      "obfuscation rather than a contents listing.";

            return new StructureFinding(StructureVerdict.Obfuscated, summary, evidence);
        }

        if (many)
        {
            evidence.Add($"{titles.Count} titles across {distinctSets} title set(s), " +
                         $"{Bytes(structure.TotalVideoBytes)} of video.");
            evidence.Add("Chapter counts and sizes vary, so these look like genuine separate " +
                         "items — a series, a compilation, or a disc of many short pieces.");
            return new StructureFinding(StructureVerdict.Unusual,
                $"{titles.Count} titles — a lot, but they differ from one another.",
                evidence);
        }

        // The ordinary case, and worth describing rather than merely passing.
        var longest = titles.OrderByDescending(t => t.Chapters).First();
        evidence.Add($"{titles.Count} title(s) across {distinctSets} title set(s), " +
                     $"{Bytes(structure.TotalVideoBytes)} of video.");
        evidence.Add($"Title {longest.TitleNumber} has the most chapters ({longest.Chapters}), " +
                     "which on most discs is the main feature.");

        int shorts = titles.Count(t => t.Chapters <= 2);
        if (shorts > 0)
            evidence.Add($"{shorts} title(s) have one or two chapters — typically menus, " +
                         "trailers or short extras.");

        return new StructureFinding(StructureVerdict.Normal,
            $"{titles.Count} title(s); title {longest.TitleNumber} looks like the main feature.",
            evidence);
    }

    /// <summary>
    /// Which titles are worth attention on an obfuscated disc.
    ///
    /// Not an attempt to defeat anything — the decoys are still there and this
    /// does not remove them. It narrows a wall of near-identical rows to those
    /// that differ, which is the difference between a listing a person can read
    /// and one they cannot.
    /// </summary>
    public static IReadOnlyList<IfoReader.Title> Distinctive(IfoReader.DvdStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);

        var titles = structure.Titles;
        if (titles.Count == 0) return Array.Empty<IfoReader.Title>();

        // A title in a set with no video cannot play anything, whatever it
        // claims — those go first.
        var realSets = structure.TitleSets
            .Where(s => s.TitleVobBytes > 0)
            .Select(s => s.Number)
            .ToHashSet();

        var candidates = realSets.Count > 0
            ? titles.Where(t => realSets.Contains(t.TitleSet)).ToList()
            : titles.ToList();

        // Where several sets declare the same size to the byte, they are copies
        // of one another. Keep the first of each such group: whatever the disc
        // actually holds is in there once, not eleven times.
        var duplicateGroups = structure.TitleSets
            .Where(s => s.TitleVobBytes > 0)
            .GroupBy(s => s.TitleVobBytes)
            .Where(g => g.Count() >= 3)
            .ToList();

        foreach (var group in duplicateGroups)
        {
            var keep = group.OrderBy(s => s.Number).First().Number;
            var drop = group.Select(s => s.Number).Where(n => n != keep).ToHashSet();
            candidates = candidates.Where(t => !drop.Contains(t.TitleSet)).ToList();
        }

        if (candidates.Count <= 12) return candidates.OrderBy(t => t.TitleNumber).ToList();

        // Still too many: thin out repeated chapter counts, keeping one
        // representative of each since the feature usually hides among them.
        var byChapters = candidates.GroupBy(t => t.Chapters)
                                   .OrderByDescending(g => g.Count())
                                   .ToList();
        var crowd = byChapters.Where(g => g.Count() >= 10).Select(g => g.Key).ToHashSet();

        var distinctive = candidates.Where(t => !crowd.Contains(t.Chapters)).ToList();
        foreach (var group in byChapters.Where(g => g.Count() >= 10))
            distinctive.Add(group.OrderByDescending(t => t.Chapters).First());

        return distinctive.OrderBy(t => t.TitleNumber).ToList();
    }

    private static string Bytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):N2} GB",
        >= 1024L * 1024 => $"{b / (1024.0 * 1024):N0} MB",
        _ => $"{b:N0} bytes",
    };
}
