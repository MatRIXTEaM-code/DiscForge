// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Diagnostics;
using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// Empirical CACHE-DEFEAT probe: when the adaptive re-read controller (or a jitter-consensus rip)
/// re-reads the SAME sector to resolve a stubborn error, does the drive genuinely go back to the
/// media, or does it silently hand back its own read-ahead cache? A drive that serves the cache is
/// useless for re-read escalation — every "retry" would just be the same cached bytes coming back,
/// never actually re-sampling the disc.
///
/// The test is a timing comparison, the same technique EAC/dBpoweramp use: read a sector once
/// (COLD — seek + spin-up + transfer), then read the identical sector again immediately (WARM). A
/// drive serving its cache answers the warm read far faster than the cold one (no seek, no media
/// access); a drive that defeats its cache answers both at roughly the same speed, because both
/// really did go back to the disc. Several trials are taken and the MEDIAN ratio used, since a
/// single timing sample is noisy (OS scheduling, USB/SATA bus contention, etc.).
///
/// This is read-only and non-destructive — the same sector is read repeatedly, nothing is written.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriveCacheDefeatProbe
{
    private const int RawSectorBytes = 2448;   // 2352 main + 96 sub, matching RawDiscReader/DriveOverreadProbe
    private const int Trials = 5;

    /// <param name="DiscPresent">False when there's no readable TOC to pick a probe sector from.</param>
    /// <param name="Defeats">True when the drive appears to genuinely re-read the media on a repeat
    /// request (warm read no faster than cold); false when it appears to answer from its own cache.</param>
    /// <param name="ColdMs">Median cold-read time across trials.</param>
    /// <param name="WarmMs">Median warm (immediate repeat) read time across trials.</param>
    /// <param name="Ratio">WarmMs / ColdMs — the number the verdict is based on.</param>
    /// <param name="Detail">Human-readable explanation, including the raw numbers, for a log/report.</param>
    public sealed record Result(bool DiscPresent, bool Defeats, double ColdMs, double WarmMs, double Ratio, string Detail);

    /// <summary>Cache-defeat verdict thresholds. A warm read taking under 50% of the cold read's time
    /// is graded "served from cache"; 80% or more is graded "genuinely re-read". The band between is
    /// reported as the closer of the two but flagged ambiguous in <see cref="Result.Detail"/> — real
    /// USB/optical timing is noisy enough that a hard cutover would overstate the probe's precision.</summary>
    private const double CachedBelow = 0.50;
    private const double GenuineAtOrAbove = 0.80;

    /// <summary>Probe the disc currently loaded in <paramref name="dev"/>.</summary>
    public static Result Probe(SptiDevice dev)
    {
        ArgumentNullException.ThrowIfNull(dev);

        var toc = DiscReader.ReadToc(dev);
        if (toc.Tracks.Count == 0)
            return new Result(false, false, 0, 0, 0, "no readable disc / TOC — load a disc to probe cache-defeat");

        // Any readable sector works; the first track's start is always present and cheap to seek to.
        uint lba = toc.Tracks[0].StartLba;
        var fields = toc.HasData ? MmcCommands.SectorFields.Raw : MmcCommands.SectorFields.UserData;
        var buf = new byte[RawSectorBytes];

        var cold = new List<double>();
        var warm = new List<double>();
        var sw = new Stopwatch();

        for (int i = 0; i < Trials; i++)
        {
            // Force a real seek away first, so the "cold" read of this trial can't itself be served
            // by a warm cache left over from the previous trial's warm read.
            SeekAway(dev, toc, lba, fields);

            sw.Restart();
            var r1 = dev.SendCommand(
                MmcCommands.ReadCd(lba, 1, MmcCommands.ExpectedSectorType.Any, fields, MmcCommands.SubChannel.None),
                buf, SptiDataDirection.In, 20);
            sw.Stop();
            if (!r1.Success)
                return new Result(true, false, 0, 0, 0,
                    $"read at LBA {lba} failed ({r1.Describe()}) — cannot probe cache-defeat on this disc/sector");
            cold.Add(sw.Elapsed.TotalMilliseconds);

            sw.Restart();
            var r2 = dev.SendCommand(
                MmcCommands.ReadCd(lba, 1, MmcCommands.ExpectedSectorType.Any, fields, MmcCommands.SubChannel.None),
                buf, SptiDataDirection.In, 20);
            sw.Stop();
            if (!r2.Success)
                return new Result(true, false, 0, 0, 0,
                    $"repeat read at LBA {lba} failed ({r2.Describe()}) — cannot probe cache-defeat on this disc/sector");
            warm.Add(sw.Elapsed.TotalMilliseconds);
        }

        double coldMs = Median(cold), warmMs = Median(warm);
        double ratio = coldMs <= 0 ? 1.0 : warmMs / coldMs;
        bool defeats = ratio >= GenuineAtOrAbove;
        string band = ratio < CachedBelow ? "served from cache" : ratio >= GenuineAtOrAbove ? "genuinely re-read" : "ambiguous — closer to " + (ratio < (CachedBelow + GenuineAtOrAbove) / 2 ? "cached" : "genuine");

        string detail = $"LBA {lba}: cold {coldMs:0.0} ms (median of {Trials}), warm (immediate repeat) {warmMs:0.0} ms " +
                         $"— ratio {ratio:0.00} → {band}";
        return new Result(true, defeats, coldMs, warmMs, ratio, detail);
    }

    /// <summary>Seek to a sector far from <paramref name="lba"/> and read it, so the drive's own
    /// read-ahead buffer can't be the thing serving the next "cold" read of <paramref name="lba"/>.</summary>
    private static void SeekAway(SptiDevice dev, DiscForge.Core.Mmc.DiscToc toc, uint lba, MmcCommands.SectorFields fields)
    {
        uint away = lba + 2000 < toc.LeadOutLba ? lba + 2000 : (lba > 2000 ? lba - 2000 : lba);
        if (away == lba) return;
        var buf = new byte[RawSectorBytes];
        try
        {
            dev.SendCommand(
                MmcCommands.ReadCd(away, 1, MmcCommands.ExpectedSectorType.Any, fields, MmcCommands.SubChannel.None),
                buf, SptiDataDirection.In, 20);
        }
        catch { /* best-effort seek; a failed away-read just makes this trial noisier, not wrong */ }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}
