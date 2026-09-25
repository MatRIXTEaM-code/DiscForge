// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using System.Xml;
using DiscForge.Core.DvdVideo;

namespace DiscForge.Core.Reauthor;

/// <summary>
/// Plans a DVD Shrink-style "reauthor": select which titles to keep (main movie
/// only, or a subset with chosen audio/subtitle streams), compress their video
/// to fit the target disc, and rebuild a playable <c>VIDEO_TS</c> structure.
///
/// Like the transcode layer, DiscForge orchestrates rather than reimplements:
/// the video is re-encoded with FFmpeg, and the DVD-Video muxing / IFO
/// generation is driven through <c>dvdauthor</c> (the established DVD authoring
/// tool). This class is the pure planning layer — it validates the selection,
/// resolves the compression from the budget, and emits the dvdauthor control
/// XML and the ordered build steps. Producing the plan needs no external tool,
/// so it is fully unit-testable; a thin runner executes the plan.
///
/// CSS-encrypted input is never processed — reauthor works on unprotected or
/// personally-authored DVD-Video only.
/// </summary>
public static class ReauthorPlanner
{
    public sealed record TitleSelection
    {
        public required IfoReader.Title Title { get; init; }
        /// <summary>Audio stream indices to keep (empty = all).</summary>
        public IReadOnlyList<int> KeepAudio { get; init; } = Array.Empty<int>();
        /// <summary>Subtitle stream indices to keep (empty = all).</summary>
        public IReadOnlyList<int> KeepSubtitles { get; init; } = Array.Empty<int>();
        public BitBudget.Mode CompressionMode { get; init; } = BitBudget.Mode.Automatic;
        public double CustomRatio { get; init; } = 1.0;
    }

    public sealed record ReauthorRequest
    {
        public required IReadOnlyList<TitleSelection> Titles { get; init; }
        public long TargetBytes { get; init; } = BitBudget.Dvd5;
        public string VolumeLabel { get; init; } = "DVD_VIDEO";
        /// <summary>If true, drop all menus (title plays straight through) — DVD
        /// Shrink's most common reauthor output.</summary>
        public bool DropMenus { get; init; } = true;
    }

    public sealed record EncodeStep
    {
        public required string TitleName { get; init; }
        public required double VideoRatio { get; init; }
        public required long TargetVideoBytes { get; init; }
        public IReadOnlyList<int> KeepAudio { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> KeepSubtitles { get; init; } = Array.Empty<int>();
    }

    public sealed record ReauthorPlan
    {
        public required BitBudget.BudgetResult Budget { get; init; }
        public required IReadOnlyList<EncodeStep> Encodes { get; init; }
        /// <summary>The dvdauthor control XML that assembles the re-encoded VOBs
        /// into a playable VIDEO_TS.</summary>
        public required string DvdAuthorXml { get; init; }
        public bool Fits => Budget.Fits;
        public string Summary => Fits
            ? $"Reauthor {Encodes.Count} title(s) → {Budget.PlannedFillPercent:F1}% of target; " +
              $"video ratio {Budget.AutomaticRatio:P0}."
            : "Selection does not fit the target even at the quality floor — " +
              "drop a title or a stream, or choose a larger target.";
    }

    /// <summary>
    /// Build the plan: run the budget over the selected titles, derive per-title
    /// encode steps, and generate the dvdauthor XML. The per-title sizes come
    /// from the caller (measured from the source VOBs / streams).
    /// </summary>
    public static ReauthorPlan Plan(
        ReauthorRequest request,
        IReadOnlyDictionary<int, TitleSizesInput> sizesByTitleNumber)
    {
        if (request.Titles.Count == 0)
            throw new ArgumentException("Reauthor needs at least one title selected.");

        // 1) Budget over the selection.
        var budgetReqs = new List<BitBudget.TitlePlanRequest>(request.Titles.Count);
        foreach (var sel in request.Titles)
        {
            if (!sizesByTitleNumber.TryGetValue(sel.Title.TitleNumber, out var sz))
                throw new ArgumentException($"No sizes supplied for title {sel.Title.TitleNumber}.");

            budgetReqs.Add(new BitBudget.TitlePlanRequest
            {
                Title = new BitBudget.TitleSizes
                {
                    Name = $"Title {sel.Title.TitleNumber}",
                    VideoBytes = sz.VideoBytes,
                    AudioBytes = SumKept(sz.AudioBytesByStream, sel.KeepAudio),
                    SubtitleBytes = SumKept(sz.SubtitleBytesByStream, sel.KeepSubtitles),
                    OverheadBytes = sz.OverheadBytes,
                },
                Mode = sel.CompressionMode,
                CustomRatio = sel.CustomRatio,
            });
        }
        var budget = BitBudget.Compute(budgetReqs, request.TargetBytes);

        // 2) Per-title encode steps from the budget result.
        var encodes = new List<EncodeStep>(request.Titles.Count);
        for (int i = 0; i < request.Titles.Count; i++)
        {
            var sel = request.Titles[i];
            var plan = budget.Titles[i];
            encodes.Add(new EncodeStep
            {
                TitleName = plan.Name,
                VideoRatio = plan.VideoRatio,
                TargetVideoBytes = plan.PlannedVideoBytes,
                KeepAudio = sel.KeepAudio,
                KeepSubtitles = sel.KeepSubtitles,
            });
        }

        // 3) dvdauthor control XML.
        string xml = BuildDvdAuthorXml(request, encodes);

        return new ReauthorPlan { Budget = budget, Encodes = encodes, DvdAuthorXml = xml };
    }

    /// <summary>Per-title source sizes, measured by the caller from the VOBs.</summary>
    public sealed record TitleSizesInput
    {
        public required long VideoBytes { get; init; }
        public IReadOnlyDictionary<int, long> AudioBytesByStream { get; init; }
            = new Dictionary<int, long>();
        public IReadOnlyDictionary<int, long> SubtitleBytesByStream { get; init; }
            = new Dictionary<int, long>();
        public long OverheadBytes { get; init; }
    }

    private static long SumKept(IReadOnlyDictionary<int, long> sizes, IReadOnlyList<int> keep)
    {
        if (keep.Count == 0) return sizes.Values.Sum();       // keep all
        long total = 0;
        foreach (var i in keep) if (sizes.TryGetValue(i, out var s)) total += s;
        return total;
    }

    /// <summary>
    /// Generate the dvdauthor XML. Each kept title becomes a PGC playing its
    /// re-encoded VOB; menus are dropped when requested (the common case), so
    /// the disc plays the first title on insert.
    /// </summary>
    internal static string BuildDvdAuthorXml(ReauthorRequest request, IReadOnlyList<EncodeStep> encodes)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true };
        using var w = XmlWriter.Create(sb, settings);

        w.WriteStartElement("dvdauthor");
        w.WriteAttributeString("dest", "VIDEO_TS");

        // VMGM (top menu domain). When menus are dropped we still need a minimal
        // VMGM that jumps straight to the first titleset title.
        w.WriteStartElement("vmgm");
        w.WriteStartElement("menus");
        w.WriteStartElement("pgc");
        w.WriteAttributeString("entry", "title");
        w.WriteStartElement("post");
        w.WriteString("jump title 1;");
        w.WriteEndElement(); // post
        w.WriteEndElement(); // pgc
        w.WriteEndElement(); // menus
        w.WriteEndElement(); // vmgm

        // One titleset holding the kept titles as sequential PGCs.
        w.WriteStartElement("titleset");
        w.WriteStartElement("titles");
        foreach (var e in encodes)
        {
            w.WriteStartElement("pgc");
            w.WriteStartElement("vob");
            // The re-encoded file the transcode step will produce for this title.
            w.WriteAttributeString("file", SafeVobName(e.TitleName));
            w.WriteEndElement(); // vob
            w.WriteEndElement(); // pgc
        }
        w.WriteEndElement(); // titles
        w.WriteEndElement(); // titleset

        w.WriteEndElement(); // dvdauthor
        w.Flush();
        return sb.ToString();
    }

    /// <summary>The intermediate VOB filename for a title's re-encoded stream.</summary>
    public static string SafeVobName(string titleName)
    {
        var clean = new string(titleName.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-').ToArray())
            .Trim().Replace(' ', '_');
        if (clean.Length == 0) clean = "title";
        return clean + ".vob";
    }
}
