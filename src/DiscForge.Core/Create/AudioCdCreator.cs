// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Audio;
using DiscForge.Core.Cdi;

namespace DiscForge.Core.Create;

/// <summary>Raised when a compilation can't be built as asked.</summary>
public sealed class AudioCdException(string message) : Exception(message);

/// <summary>One track of a compilation, as supplied by the caller.</summary>
public sealed record AudioTrackSource
{
    /// <summary>Path to a Red Book WAV (44.1 kHz, 16-bit, stereo).</summary>
    public required string Path { get; init; }
    /// <summary>Gap before this track, in sectors (75 = 1 second). Track 1 always
    /// gets the mandatory 150-sector lead-in gap regardless.</summary>
    public uint PregapSectors { get; init; } = 150;

    /// <summary>
    /// Silence appended AFTER this track, in sectors. Zero for a normal disc.
    ///
    /// Some third-party images omit the post-gap the standard expects before the
    /// lead-out, and a few players click or truncate the last moment of audio as
    /// a result. 150 sectors (two seconds) is the customary value.
    /// </summary>
    public uint PostgapSectors { get; init; }
}

/// <summary>
/// Builds a Red Book audio CD image from WAV files — the authoring half of the
/// audio story (we could already extract audio to WAV, but not the reverse).
///
/// Red Book rules this enforces:
///  - 44.1 kHz / 16-bit / stereo only. No resampling: a wrong conversion done
///    silently is worse than a refusal.
///  - Every track is 2352 bytes/sector, and audio that doesn't fill its last
///    sector is padded with silence (a CD has no concept of a partial sector).
///  - Track 1 carries the mandatory 150-sector (2 second) pregap; later tracks
///    default to the customary 150 but may be set to 0 for a gapless disc.
///  - A CD holds 74 or 80 minutes; over-length compilations are refused with the
///    actual running time, not a cryptic error.
/// </summary>
public static class AudioCdCreator
{
    private const int SectorBytes = 2352;
    private const int SectorsPerSecond = 75;

    /// <summary>Standard 74-minute CD capacity, in sectors.</summary>
    public const uint Capacity74Min = 74 * 60 * SectorsPerSecond;   // 333,000
    /// <summary>80-minute CD capacity, in sectors — common, slightly out of spec.</summary>
    public const uint Capacity80Min = 80 * 60 * SectorsPerSecond;   // 360,000

    public sealed record CompilationResult(
        long CdiBytes, int TrackCount, uint TotalSectors, IReadOnlyList<string> Warnings)
    {
        public TimeSpan Duration => TimeSpan.FromSeconds((double)TotalSectors / SectorsPerSecond);
    }

    /// <summary>
    /// Build an audio CDI from WAV files, in the order given.
    /// </summary>
    /// <param name="allow80Minute">Permit up to 80 minutes rather than 74.</param>
    public static CompilationResult Create(
        IReadOnlyList<AudioTrackSource> tracks, CdiVersion version, Stream output,
        bool allow80Minute = true)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(output);

        if (tracks.Count == 0)
            throw new AudioCdException("A compilation needs at least one track.");
        if (tracks.Count > 99)
            throw new AudioCdException(
                $"A CD holds at most 99 tracks; {tracks.Count} were supplied.");

        var warnings = new List<string>();
        var inputs = new List<CdiWriter.TrackInput>();

        uint lba = 0;
        uint totalSectors = 0;

        for (int i = 0; i < tracks.Count; i++)
        {
            var source = tracks[i];
            if (!File.Exists(source.Path))
                throw new FileNotFoundException($"Track {i + 1}: file not found.", source.Path);

            WavInfo info;
            using (var wav = File.OpenRead(source.Path))
                info = WavReader.ReadCdAudio(wav, Path.GetFileName(source.Path));

            // Track 1 must carry the 150-sector lead-in gap.
            uint pregap = i == 0 ? Math.Max(150, source.PregapSectors) : source.PregapSectors;

            uint sectors = info.SectorCount;
            long tail = info.DataLength % SectorBytes;
            if (tail != 0)
                warnings.Add(
                    $"Track {i + 1} ({Path.GetFileName(source.Path)}) doesn't fill its last " +
                    $"sector; {SectorBytes - tail} byte(s) of silence added. This is normal.");

            if (info.Duration < TimeSpan.FromSeconds(4))
                warnings.Add(
                    $"Track {i + 1} is {info.Duration.TotalSeconds:N1}s. Red Book requires at " +
                    "least 4 seconds per track; some players skip shorter ones.");

            string path = source.Path;
            long dataOffset = info.DataOffset;
            long dataLength = info.DataLength;
            long padding = (long)sectors * SectorBytes - dataLength;
            long pregapBytes = (long)pregap * SectorBytes;

            // A post-gap is silence after the audio. It counts as part of the
            // track's length, so the next track's start moves accordingly.
            uint postgap = source.PostgapSectors;
            long postgapBytes = (long)postgap * SectorBytes;
            uint trackSectors = sectors + postgap;

            if (postgap > 0)
                warnings.Add(
                    $"Track {i + 1}: {postgap} sector(s) ({postgap / (double)SectorsPerSecond:N1}s) " +
                    "of post-gap silence appended.");

            inputs.Add(new CdiWriter.TrackInput
            {
                Mode = CdiTrackMode.Audio,
                SectorSize = CdiSectorSize.S2352,
                PregapSectors = pregap,
                LengthSectors = trackSectors,
                StartLba = lba + pregap,
                Filename = $"TRACK{i + 1:D2}.WAV",
                // Streamed: pregap silence, the samples, sector padding, post-gap.
                DataWriter = os =>
                {
                    WriteSilence(os, pregapBytes);
                    CopyRange(path, dataOffset, dataLength, os);
                    WriteSilence(os, padding);
                    WriteSilence(os, postgapBytes);
                },
            });

            lba += pregap + trackSectors;
            totalSectors += pregap + trackSectors;
        }

        uint capacity = allow80Minute ? Capacity80Min : Capacity74Min;
        if (totalSectors > capacity)
        {
            var runtime = TimeSpan.FromSeconds((double)totalSectors / SectorsPerSecond);
            var limit = TimeSpan.FromSeconds((double)capacity / SectorsPerSecond);
            throw new AudioCdException(
                $"The compilation runs to {runtime:hh\\:mm\\:ss}, which won't fit a " +
                $"{limit.TotalMinutes:N0}-minute CD ({totalSectors:N0} sectors vs {capacity:N0}). " +
                "Remove a track" + (allow80Minute ? "." : ", or allow 80-minute media."));
        }

        if (allow80Minute && totalSectors > Capacity74Min)
            warnings.Add(
                $"The compilation is over 74 minutes, so it needs 80-minute media " +
                $"({totalSectors:N0} sectors).");

        long start = output.CanSeek ? output.Position : 0;
        CdiWriter.Write(output, version, new[] { (IReadOnlyList<CdiWriter.TrackInput>)inputs });
        long written = output.CanSeek ? output.Position - start : 0;

        return new CompilationResult(written, inputs.Count, totalSectors, warnings);
    }

    private static void WriteSilence(Stream output, long bytes)
    {
        if (bytes <= 0) return;
        var zeros = new byte[Math.Min(bytes, 1 << 16)];
        long remaining = bytes;
        while (remaining > 0)
        {
            int n = (int)Math.Min(zeros.Length, remaining);
            output.Write(zeros, 0, n);
            remaining -= n;
        }
    }

    private static void CopyRange(string path, long offset, long count, Stream output)
    {
        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                       bufferSize: 1 << 16, FileOptions.SequentialScan);
        src.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[1 << 16];
        long remaining = count;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int n = src.Read(buffer, 0, want);
            if (n <= 0)
                throw new EndOfStreamException(
                    $"'{Path.GetFileName(path)}' ended {remaining:N0} bytes early — it may have " +
                    "changed while being read.");
            output.Write(buffer, 0, n);
            remaining -= n;
        }
    }
}
