// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.DvdVideo;

/// <summary>
/// The bit-budgeting engine — the arithmetic at the heart of a DVD Shrink-style
/// "fit this DVD-Video onto a smaller disc" operation. Given the titles to keep
/// and a target capacity, it computes the compression ratio each title's video
/// must take so the whole thing fits, with the same choices DVD Shrink offered:
/// automatic (compress everything evenly), or per-title custom ratios (protect
/// the main movie's quality by squeezing the extras harder).
///
/// This is pure size arithmetic over stream sizes — no video is touched here.
/// It decides the *plan*; a separate transcode stage (FFmpeg, later) executes
/// it. Because it's arithmetic, it is fully testable without any encoder.
///
/// Only the VIDEO stream is compressible in this model, exactly as DVD Shrink
/// worked: <c>DVD Shrink only shrinks the video and not the audio</c>, so audio
/// and subtitle sizes are fixed costs, and dropping an audio/subtitle stream
/// frees its whole size.
/// </summary>
public static class BitBudget
{
    // Standard recordable capacities (bytes). DVD Shrink's targets.
    public const long Dvd5 = 4_700_372_992;   // 4.7 GB (DVD±R single layer)
    public const long Dvd9 = 8_543_666_176;   // 8.5 GB (DVD±R DL)

    /// <summary>A title (main movie, an extra, a menu) as a set of stream sizes.</summary>
    public sealed record TitleSizes
    {
        public required string Name { get; init; }
        /// <summary>Video elementary-stream size in bytes (the compressible part).</summary>
        public required long VideoBytes { get; init; }
        /// <summary>Total of the audio streams kept, in bytes (not compressed).</summary>
        public long AudioBytes { get; init; }
        /// <summary>Total of the subtitle streams kept, in bytes (not compressed).</summary>
        public long SubtitleBytes { get; init; }
        /// <summary>Navigation/overhead (IFO, padding), in bytes (not compressed).</summary>
        public long OverheadBytes { get; init; }

        public long UncompressedTotal => VideoBytes + AudioBytes + SubtitleBytes + OverheadBytes;
    }

    /// <summary>How a title's video should be compressed.</summary>
    public enum Mode
    {
        /// <summary>Compress as needed to help hit the target (shares the load).</summary>
        Automatic,
        /// <summary>Keep at full quality — no compression (protect this title).</summary>
        NoCompression,
        /// <summary>Use exactly the given ratio (0.0–1.0 of original video size).</summary>
        CustomRatio,
        /// <summary>Drop this title's video entirely (still-menu / removed).</summary>
        StillOrOmit,
    }

    public sealed record TitlePlanRequest
    {
        public required TitleSizes Title { get; init; }
        public Mode Mode { get; init; } = Mode.Automatic;
        /// <summary>For <see cref="Mode.CustomRatio"/>: 0.05–1.0.</summary>
        public double CustomRatio { get; init; } = 1.0;
    }

    public sealed record TitlePlan
    {
        public required string Name { get; init; }
        /// <summary>Final video ratio applied (1.0 = untouched, 0.5 = half size).</summary>
        public required double VideoRatio { get; init; }
        public required long PlannedVideoBytes { get; init; }
        public required long PlannedTotalBytes { get; init; }
        public required Mode Mode { get; init; }
    }

    public sealed record BudgetResult
    {
        public required IReadOnlyList<TitlePlan> Titles { get; init; }
        public required long TargetBytes { get; init; }
        public required long PlannedTotalBytes { get; init; }
        public required bool Fits { get; init; }
        /// <summary>The uniform ratio applied to all Automatic titles (1.0 if none needed).</summary>
        public required double AutomaticRatio { get; init; }
        /// <summary>Smallest ratio we allow before we say "won't fit at acceptable quality".</summary>
        public double FloorRatio { get; init; }

        public double PlannedFillPercent => TargetBytes == 0 ? 0 : 100.0 * PlannedTotalBytes / TargetBytes;

        public string Summary => Fits
            ? $"Fits: {PlannedTotalBytes:N0} / {TargetBytes:N0} bytes " +
              $"({PlannedFillPercent:F1}%), automatic video ratio {AutomaticRatio:P0}."
            : $"Does not fit at the quality floor ({FloorRatio:P0}); " +
              $"planned {PlannedTotalBytes:N0} > target {TargetBytes:N0}. " +
              "Drop a title/stream or split across two discs.";
    }

    /// <summary>
    /// Compute the compression plan. Fixed-mode titles (NoCompression, Custom,
    /// StillOrOmit) take their stated size; the remaining target space is shared
    /// among the Automatic titles by solving for the single ratio that makes the
    /// whole set equal the target.
    /// </summary>
    /// <param name="floorRatio">Lowest automatic video ratio allowed (DVD Shrink's
    /// practical floor was ~0.39 for 16:9; below that quality collapses).</param>
    public static BudgetResult Compute(
        IReadOnlyList<TitlePlanRequest> requests, long targetBytes, double floorRatio = 0.39)
    {
        // 1) Fixed (non-automatic) titles: their sizes are known up front.
        long fixedBytes = 0;
        long autoVideoBytes = 0;      // compressible video pool
        long autoFixedBytes = 0;      // audio/subs/overhead of automatic titles
        foreach (var r in requests)
        {
            var t = r.Title;
            switch (r.Mode)
            {
                case Mode.NoCompression:
                    fixedBytes += t.UncompressedTotal;
                    break;
                case Mode.CustomRatio:
                    fixedBytes += Scale(t.VideoBytes, Clamp(r.CustomRatio))
                                + t.AudioBytes + t.SubtitleBytes + t.OverheadBytes;
                    break;
                case Mode.StillOrOmit:
                    fixedBytes += t.AudioBytes + t.SubtitleBytes + t.OverheadBytes; // video dropped
                    break;
                case Mode.Automatic:
                default:
                    autoVideoBytes += t.VideoBytes;
                    autoFixedBytes += t.AudioBytes + t.SubtitleBytes + t.OverheadBytes;
                    break;
            }
        }

        // 2) Space left for the automatic titles' *video* after everything fixed.
        long spaceForAutoVideo = targetBytes - fixedBytes - autoFixedBytes;

        // 3) Solve for the automatic ratio.
        double autoRatio;
        if (autoVideoBytes == 0)
            autoRatio = 1.0;                                   // nothing to compress
        else if (spaceForAutoVideo >= autoVideoBytes)
            autoRatio = 1.0;                                   // already fits, no compression
        else if (spaceForAutoVideo <= 0)
            autoRatio = floorRatio;                            // no room; clamp to floor
        else
            autoRatio = (double)spaceForAutoVideo / autoVideoBytes;

        bool clampedByFloor = autoRatio < floorRatio;
        if (clampedByFloor) autoRatio = floorRatio;

        // 4) Emit per-title plans.
        var plans = new List<TitlePlan>(requests.Count);
        long planned = 0;
        foreach (var r in requests)
        {
            var t = r.Title;
            double ratio;
            long video;
            switch (r.Mode)
            {
                case Mode.NoCompression: ratio = 1.0; video = t.VideoBytes; break;
                case Mode.CustomRatio:   ratio = Clamp(r.CustomRatio); video = Scale(t.VideoBytes, ratio); break;
                case Mode.StillOrOmit:   ratio = 0.0; video = 0; break;
                default:                 ratio = autoRatio; video = Scale(t.VideoBytes, ratio); break;
            }
            long total = video + t.AudioBytes + t.SubtitleBytes + t.OverheadBytes;
            planned += total;
            plans.Add(new TitlePlan
            {
                Name = t.Name, VideoRatio = ratio, PlannedVideoBytes = video,
                PlannedTotalBytes = total, Mode = r.Mode,
            });
        }

        return new BudgetResult
        {
            Titles = plans,
            TargetBytes = targetBytes,
            PlannedTotalBytes = planned,
            Fits = planned <= targetBytes,
            AutomaticRatio = autoRatio,
            FloorRatio = floorRatio,
        };
    }

    private static long Scale(long bytes, double ratio) => (long)Math.Round(bytes * ratio);
    private static double Clamp(double r) => Math.Clamp(r, 0.05, 1.0);
}
