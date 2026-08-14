// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Devices;

/// <summary>
/// Aggregates a sequence of timed read samples into a transfer-rate profile — the read-speed half of
/// ImgBurn's "Discovery" mode. A drive slows down over damaged or marginal areas long before a read
/// outright fails, so the SLOWEST region is often the first sign of a rotting disc. This class is the
/// pure, unit-tested arithmetic (samples → MB/s min/avg/max + the slowest region); the physical timed
/// reads live in the Devices layer.
/// </summary>
public static class ReadRateProfile
{
    /// <summary>Raw CD sector size the benchmark transfers (2352 main-channel bytes, no sub-channel).</summary>
    public const int RawSectorBytes = 2352;

    public sealed record Sample(long StartLba, int Sectors, double ElapsedMs)
    {
        public double MbPerSecond => ElapsedMs <= 0
            ? 0
            : (Sectors * (double)RawSectorBytes / (1024.0 * 1024.0)) / (ElapsedMs / 1000.0);
    }

    public sealed record Report
    {
        public required long SectorsRead { get; init; }
        public required int Samples { get; init; }
        public required double MinMbps { get; init; }
        public required double AvgMbps { get; init; }
        public required double MaxMbps { get; init; }
        public required double ElapsedSeconds { get; init; }
        public Sample? Slowest { get; init; }

        public string Summary => Samples == 0
            ? "No sectors were read — no disc, or the read was refused at the start."
            : $"Read {SectorsRead:N0} sectors in {ElapsedSeconds:F1}s — avg {AvgMbps:F2} MB/s " +
              $"(min {MinMbps:F2}, max {MaxMbps:F2})" +
              (Slowest is null ? "." : $"; slowest at LBA {Slowest.StartLba:N0} ({Slowest.MbPerSecond:F2} MB/s).");
    }

    /// <summary>Summarize timed read samples into a rate profile.</summary>
    public static Report Summarize(IReadOnlyList<Sample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            return new Report
            {
                SectorsRead = 0, Samples = 0, MinMbps = 0, AvgMbps = 0, MaxMbps = 0, ElapsedSeconds = 0,
            };

        long sectors = samples.Sum(s => (long)s.Sectors);
        double totalMs = samples.Sum(s => s.ElapsedMs);
        double totalMb = sectors * (double)RawSectorBytes / (1024.0 * 1024.0);
        var rates = samples.Select(s => s.MbPerSecond).ToList();

        return new Report
        {
            SectorsRead = sectors,
            Samples = samples.Count,
            MinMbps = rates.Min(),
            AvgMbps = totalMs <= 0 ? 0 : totalMb / (totalMs / 1000.0),
            MaxMbps = rates.Max(),
            ElapsedSeconds = totalMs / 1000.0,
            Slowest = samples.OrderBy(s => s.MbPerSecond).ThenBy(s => s.StartLba).First(),
        };
    }
}
