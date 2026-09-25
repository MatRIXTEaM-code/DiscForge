// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Globalization;
using DiscForge.Core.DvdVideo;

namespace DiscForge.Core.Transcode;

/// <summary>
/// Turns an abstract <see cref="BitBudget"/> plan into concrete FFmpeg encode
/// parameters — the bridge between "compress this title's video to 54%" and the
/// actual bitrate, codec, and argument list FFmpeg needs.
///
/// This layer is pure: it computes target bitrates from the plan and builds the
/// argument vectors, but never launches anything. That keeps the tricky
/// arithmetic and argument construction fully unit-testable without FFmpeg
/// present. The <see cref="FfmpegRunner"/> executes what this produces.
///
/// The bitrate model is the standard one: a title's target video *size* implies
/// a target average *bitrate* given its duration
/// (bits = bytes×8; bitrate = bits / seconds). Two-pass ("Deep Analysis" in
/// DVD Shrink terms) hits that average accurately by measuring in pass 1 and
/// distributing in pass 2.
/// </summary>
public static class TranscodePlanner
{
    public enum Container { Mp4, Mkv, DvdVideoMpeg2 }
    public enum VideoCodec { H264, Hevc, Mpeg2 }

    public sealed record TitleEncode
    {
        public required string Name { get; init; }
        public required string InputPath { get; init; }
        public required string OutputPath { get; init; }
        public required double DurationSeconds { get; init; }
        /// <summary>Target video bitrate in bits/sec (0 = copy, no re-encode).</summary>
        public required long VideoBitrate { get; init; }
        public required VideoCodec Codec { get; init; }
        public required Container Container { get; init; }
        public bool TwoPass { get; init; }
        /// <summary>Audio stream indices to keep (empty = all).</summary>
        public IReadOnlyList<int> KeepAudio { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> KeepSubtitles { get; init; } = Array.Empty<int>();
        /// <summary>True when the plan said "no compression" — stream-copy video.</summary>
        public bool CopyVideo => VideoBitrate == 0;
    }

    /// <summary>
    /// Derive an encode spec for one title from its budget plan and duration.
    /// If the plan leaves the video untouched (ratio ≈ 1.0), we stream-copy.
    /// </summary>
    public static TitleEncode ForTitle(
        BitBudget.TitlePlan plan, string input, string output,
        double durationSeconds, VideoCodec codec, Container container,
        long originalVideoBytes, bool twoPass = false,
        IReadOnlyList<int>? keepAudio = null, IReadOnlyList<int>? keepSubs = null)
    {
        long bitrate;
        if (plan.VideoRatio >= 0.999 || durationSeconds <= 0)
        {
            bitrate = 0;   // stream-copy: no meaningful compression requested
        }
        else
        {
            // Target size (bytes) → average bitrate (bits/sec).
            long targetBytes = plan.PlannedVideoBytes > 0
                ? plan.PlannedVideoBytes
                : (long)Math.Round(originalVideoBytes * plan.VideoRatio);
            bitrate = (long)Math.Round(targetBytes * 8.0 / durationSeconds);
            if (bitrate < 100_000) bitrate = 100_000;   // 100 kbps floor sanity
        }

        return new TitleEncode
        {
            Name = plan.Name,
            InputPath = input,
            OutputPath = output,
            DurationSeconds = durationSeconds,
            VideoBitrate = bitrate,
            Codec = codec,
            Container = container,
            TwoPass = twoPass && bitrate > 0,
            KeepAudio = keepAudio ?? Array.Empty<int>(),
            KeepSubtitles = keepSubs ?? Array.Empty<int>(),
        };
    }

    /// <summary>
    /// Build the FFmpeg argument vector(s) for an encode. Two-pass returns two
    /// argument lists (measure, then encode); single-pass returns one. The
    /// returned lists are argument arrays (already split), so the runner can
    /// pass them without shell-quoting pitfalls.
    /// </summary>
    public static IReadOnlyList<string[]> BuildArgs(TitleEncode e, string passLogPrefix = "ffpass")
    {
        if (e.CopyVideo)
            return new[] { SinglePassCopy(e) };

        if (e.TwoPass)
            return new[]
            {
                Pass(e, pass: 1, passLogPrefix),
                Pass(e, pass: 2, passLogPrefix),
            };

        return new[] { Pass(e, pass: 0, passLogPrefix) };
    }

    private static string[] SinglePassCopy(TitleEncode e)
    {
        var a = new List<string> { "-hide_banner", "-y", "-i", e.InputPath };
        AddStreamMaps(a, e);
        a.Add("-c"); a.Add("copy");
        a.Add(e.OutputPath);
        return a.ToArray();
    }

    private static string[] Pass(TitleEncode e, int pass, string prefix)
    {
        var a = new List<string> { "-hide_banner", "-y", "-i", e.InputPath };
        AddStreamMaps(a, e);

        // Video codec + target bitrate.
        a.Add("-c:v"); a.Add(CodecName(e.Codec));
        a.Add("-b:v"); a.Add(Bps(e.VideoBitrate));
        // Constrain the buffer for DVD-Video; harmless elsewhere.
        if (e.Codec == VideoCodec.Mpeg2)
        {
            a.Add("-maxrate"); a.Add(Bps((long)(e.VideoBitrate * 1.5)));
            a.Add("-bufsize"); a.Add(Bps(e.VideoBitrate * 2));
        }

        if (pass == 1)
        {
            // Measure pass: no audio, null output.
            a.Add("-pass"); a.Add("1");
            a.Add("-passlogfile"); a.Add(prefix);
            a.Add("-an");
            a.Add("-f"); a.Add(FormatFor(e.Container));
            a.Add(NullSink());
        }
        else
        {
            if (pass == 2) { a.Add("-pass"); a.Add("2"); a.Add("-passlogfile"); a.Add(prefix); }
            AddAudio(a, e);
            a.Add(e.OutputPath);
        }
        return a.ToArray();
    }

    private static void AddStreamMaps(List<string> a, TitleEncode e)
    {
        // Video is always stream 0 of the title.
        a.Add("-map"); a.Add("0:v:0");
        if (e.KeepAudio.Count == 0)
        {
            a.Add("-map"); a.Add("0:a?");   // all audio, if present
        }
        else
        {
            foreach (var idx in e.KeepAudio) { a.Add("-map"); a.Add($"0:a:{idx}"); }
        }
        if (e.KeepSubtitles.Count == 0)
        {
            a.Add("-map"); a.Add("0:s?");
        }
        else
        {
            foreach (var idx in e.KeepSubtitles) { a.Add("-map"); a.Add($"0:s:{idx}"); }
        }
    }

    private static void AddAudio(List<string> a, TitleEncode e)
    {
        // Keep audio as-is (DVD Shrink never re-encoded audio). For mp4/mkv the
        // original AC3/DTS copies fine; only transcode if a container demands it.
        a.Add("-c:a"); a.Add("copy");
        a.Add("-c:s"); a.Add(e.Container == Container.Mp4 ? "mov_text" : "copy");
    }

    private static string CodecName(VideoCodec c) => c switch
    {
        VideoCodec.H264 => "libx264",
        VideoCodec.Hevc => "libx265",
        VideoCodec.Mpeg2 => "mpeg2video",
        _ => "libx264",
    };

    private static string FormatFor(Container c) => c switch
    {
        Container.Mp4 => "mp4",
        Container.Mkv => "matroska",
        Container.DvdVideoMpeg2 => "dvd",
        _ => "mp4",
    };

    private static string Bps(long bits) => bits.ToString(CultureInfo.InvariantCulture);

    // FFmpeg's cross-platform null sink.
    private static string NullSink() =>
        OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
}
