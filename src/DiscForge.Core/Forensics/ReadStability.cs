// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Forensics;

/// <summary>The overall read-stability verdict for a disc.</summary>
public enum DiscStability
{
    /// <summary>Every sector read identically on every pass — a healthy, stable disc.</summary>
    Stable,
    /// <summary>A few sectors varied between passes — early marginal reflectivity worth watching / re-dumping soon.</summary>
    Marginal,
    /// <summary>Many sectors — or badly-disagreeing ones — vary between passes: the disc is failing; image it now.</summary>
    Degrading,
}

/// <summary>A run of consecutive sectors that read inconsistently across passes.</summary>
public sealed record UnstableRun(long StartSector, long EndSector, int WorstAgreement, int Passes)
{
    public long Count => EndSector - StartSector + 1;
    public override string ToString()
    {
        string where = Count == 1 ? $"{StartSector}" : $"{StartSector}-{EndSector} (×{Count})";
        return $"{where}: at worst {WorstAgreement}/{Passes} passes agreed";
    }
}

public sealed record ReadStabilityReport
{
    public required int Passes { get; init; }
    public required int Sectors { get; init; }
    public required int SectorSize { get; init; }
    public required int StableSectors { get; init; }
    public required int UnstableSectors { get; init; }
    /// <summary>Unstable sectors where FEWER than half the passes agreed — the most alarming.</summary>
    public required int SeverelyUnstable { get; init; }
    public required IReadOnlyList<UnstableRun> UnstableRuns { get; init; }
    public required DiscStability Health { get; init; }

    public double UnstableFraction => Sectors == 0 ? 0 : (double)UnstableSectors / Sectors;

    public string Summary()
    {
        string verdict = Health switch
        {
            DiscStability.Stable => "STABLE — every sector read identically on all passes.",
            DiscStability.Marginal => "MARGINAL — a few sectors read inconsistently; re-dump soon and keep an eye on it.",
            _ => "DEGRADING — many/severe read inconsistencies; image this disc now while it still reads.",
        };
        return $"{verdict} {UnstableSectors:N0} of {Sectors:N0} sector(s) unstable across {Passes} pass(es) " +
               $"({UnstableFraction:P2}){(SeverelyUnstable > 0 ? $", {SeverelyUnstable:N0} severe" : "")}.";
    }
}

/// <summary>
/// read-stability — the disc-rot early-warning the community actually wants, without a C1/C2 scanner. A healthy
/// disc reads identically every time; a disc starting to fail returns DIFFERENT bytes for the same sector on
/// different passes, because the drive's error correction is silently papering over marginal reflectivity. By
/// comparing several full reads of the same disc sector-by-sector, this surfaces exactly those unstable sectors —
/// the leading edge of degradation — long before the disc becomes unreadable, and grades the disc stable /
/// marginal / degrading. Pure comparison of reads the person already has; it changes nothing on the disc.
/// </summary>
public static class ReadStability
{
    public const int RawSector = 2352;
    private const int MaxRuns = 8192;

    public static ReadStabilityReport Analyze(IReadOnlyList<byte[]> passes, int sectorSize = RawSector)
    {
        ArgumentNullException.ThrowIfNull(passes);
        if (passes.Count < 2) throw new ArgumentException("Read-stability needs at least two passes of the same disc.", nameof(passes));
        if (sectorSize <= 0) throw new ArgumentException("Sector size must be positive.", nameof(sectorSize));

        int len = passes[0].Length;
        for (int i = 1; i < passes.Count; i++)
            if (passes[i].Length != len)
                throw new ArgumentException($"All passes must be the same length; pass 1 is {len:N0} bytes, pass {i + 1} is {passes[i].Length:N0}.");
        if (len % sectorSize != 0)
            throw new ArgumentException($"Image length {len:N0} is not a whole number of {sectorSize}-byte sectors.");

        int sectors = len / sectorSize;
        int n = passes.Count;
        int stable = 0, unstable = 0, severe = 0;
        var runs = new List<UnstableRun>();

        long runStart = -1, runEnd = -1; int runWorst = n;
        void CloseRun()
        {
            if (runStart < 0) return;
            if (runs.Count < MaxRuns) runs.Add(new UnstableRun(runStart, runEnd, runWorst, n));
            runStart = -1; runWorst = n;
        }

        for (int s = 0; s < sectors; s++)
        {
            int at = s * sectorSize;
            int agreement = PluralityAgreement(passes, at, sectorSize);
            if (agreement == n) { stable++; CloseRun(); continue; }

            unstable++;
            if (agreement * 2 < n) severe++;          // fewer than half agreed
            if (runStart < 0) { runStart = s; runWorst = agreement; }
            else runWorst = Math.Min(runWorst, agreement);
            runEnd = s;
        }
        CloseRun();

        double frac = sectors == 0 ? 0 : (double)unstable / sectors;
        // Degrading is driven by severe (majority-disagreement) sectors, or by a
        // meaningful fraction of unstable sectors — but the fraction needs an absolute
        // floor so one or two flaky sectors on a small image don't read as "degrading".
        DiscStability health = unstable == 0 ? DiscStability.Stable
            : (severe > 0 || (unstable >= 3 && frac >= 0.01)) ? DiscStability.Degrading
            : DiscStability.Marginal;

        return new ReadStabilityReport
        {
            Passes = n, Sectors = sectors, SectorSize = sectorSize,
            StableSectors = stable, UnstableSectors = unstable, SeverelyUnstable = severe,
            UnstableRuns = runs, Health = health,
        };
    }

    /// <summary>How many passes share the most common byte-content for this sector (n = all agree = stable).</summary>
    private static int PluralityAgreement(IReadOnlyList<byte[]> passes, int at, int size)
    {
        int n = passes.Count;
        Span<bool> counted = stackalloc bool[n <= 64 ? n : 64];
        counted.Clear();
        int best = 1;
        for (int i = 0; i < n; i++)
        {
            if (i < counted.Length && counted[i]) continue;
            int c = 1;
            for (int j = i + 1; j < n; j++)
            {
                if (j < counted.Length && counted[j]) continue;
                if (passes[i].AsSpan(at, size).SequenceEqual(passes[j].AsSpan(at, size)))
                {
                    c++;
                    if (j < counted.Length) counted[j] = true;
                }
            }
            if (c > best) best = c;
        }
        return best;
    }
}
