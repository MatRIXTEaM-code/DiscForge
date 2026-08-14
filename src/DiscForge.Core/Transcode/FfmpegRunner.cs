// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DiscForge.Core.Transcode;

/// <summary>
/// Runs FFmpeg to execute a <see cref="TranscodePlanner"/> spec, reporting
/// progress. The process invocation is injectable (<see cref="IProcessRunner"/>)
/// so the orchestration and the progress-line parsing are unit-testable without
/// FFmpeg installed — the real runner is a thin adapter over
/// <see cref="Process"/>.
///
/// DiscForge does not bundle FFmpeg; it locates an installed <c>ffmpeg</c> (on
/// PATH or a configured path). If none is found, transcoding reports that
/// clearly rather than failing cryptically. This keeps DiscForge's own
/// distribution free of FFmpeg's separate licensing.
/// </summary>
public sealed partial class FfmpegRunner
{
    public interface IProcessRunner
    {
        /// <summary>Run a process to completion, delivering each stderr line to
        /// <paramref name="onLine"/>. Returns the exit code.</summary>
        int Run(string exe, IReadOnlyList<string> args, Action<string> onLine, CancellationToken ct);
    }

    public sealed record Progress
    {
        public required double? OutTimeSeconds { get; init; }
        public required double? Fps { get; init; }
        public required double? SpeedX { get; init; }
        public double? Percent { get; init; }   // when total duration is known
    }

    private readonly IProcessRunner _runner;
    private readonly string _ffmpegPath;

    public FfmpegRunner(string ffmpegPath, IProcessRunner? runner = null)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner ?? new RealProcessRunner();
    }

    /// <summary>
    /// Locate an ffmpeg executable: an explicit path, then PATH, then common
    /// install locations. Returns null if none is usable.
    /// </summary>
    public static string? Locate(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        // PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        // Common Windows locations.
        if (OperatingSystem.IsWindows())
        {
            foreach (var p in new[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            })
                if (File.Exists(p)) return p;
        }

        return null;
    }

    /// <summary>Execute every argument vector for a title in order, forwarding
    /// progress. Two-pass runs both passes; returns true on success.</summary>
    public bool Run(TranscodePlanner.TitleEncode title,
                    IReadOnlyList<string[]> argVectors,
                    Action<Progress>? onProgress = null,
                    Action<string>? onLog = null,
                    CancellationToken ct = default)
    {
        double total = title.DurationSeconds;
        foreach (var args in argVectors)
        {
            int code = _runner.Run(_ffmpegPath, args, line =>
            {
                onLog?.Invoke(line);
                var p = ParseProgress(line, total);
                if (p is not null) onProgress?.Invoke(p);
            }, ct);

            if (code != 0)
            {
                onLog?.Invoke($"ffmpeg exited with code {code}.");
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Parse an FFmpeg stderr progress line. FFmpeg emits lines like:
    /// <c>frame= 1234 fps= 30 q=28.0 size=  10240kB time=00:01:23.45 bitrate=...x speed=1.2x</c>
    /// We extract time, fps and speed; percent when total duration is known.
    /// </summary>
    public static Progress? ParseProgress(string line, double totalSeconds)
    {
        var tm = TimeRegex().Match(line);
        if (!tm.Success) return null;

        double? outTime = ParseTimestamp(tm.Groups["t"].Value);
        double? fps = null, speed = null;

        var fm = FpsRegex().Match(line);
        if (fm.Success && double.TryParse(fm.Groups["v"].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) fps = f;

        var sm = SpeedRegex().Match(line);
        if (sm.Success && double.TryParse(sm.Groups["v"].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) speed = s;

        double? pct = (outTime is not null && totalSeconds > 0)
            ? Math.Clamp(outTime.Value / totalSeconds * 100.0, 0, 100)
            : null;

        return new Progress { OutTimeSeconds = outTime, Fps = fps, SpeedX = speed, Percent = pct };
    }

    private static double? ParseTimestamp(string hhmmss)
    {
        // Format: HH:MM:SS.ss
        var parts = hhmmss.Split(':');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out int h)) return null;
        if (!int.TryParse(parts[1], out int m)) return null;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
            return null;
        return h * 3600 + m * 60 + sec;
    }

    [GeneratedRegex(@"time=(?<t>\d{2}:\d{2}:\d{2}\.\d+)")]
    private static partial Regex TimeRegex();
    [GeneratedRegex(@"fps=\s*(?<v>[\d.]+)")]
    private static partial Regex FpsRegex();
    [GeneratedRegex(@"speed=\s*(?<v>[\d.]+)x")]
    private static partial Regex SpeedRegex();

    /// <summary>The real process runner, over System.Diagnostics.Process.</summary>
    private sealed class RealProcessRunner : IProcessRunner
    {
        public int Run(string exe, IReadOnlyList<string> args, Action<string> onLine, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            while (!proc.WaitForExit(200))
            {
                if (ct.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return -1;
                }
            }
            return proc.ExitCode;
        }
    }
}
