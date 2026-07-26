// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Core.Raw;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>Error counts for one band of the disc.</summary>
public sealed record QualityBand
{
    public required int Index { get; init; }
    public required uint StartLba { get; init; }
    public required uint EndLba { get; init; }
    public required int SectorsSampled { get; init; }
    /// <summary>Sectors where the drive reported at least one uncorrectable byte.</summary>
    public required int SectorsWithC2 { get; init; }
    /// <summary>Total uncorrectable bytes across the sampled sectors.</summary>
    public required long BadBytes { get; init; }
    /// <summary>Sectors the drive refused outright — unreadable, not merely damaged.</summary>
    public required int SectorsRefused { get; init; }

    public double ErrorRate => SectorsSampled == 0 ? 0 : (double)SectorsWithC2 / SectorsSampled;
    public double RefusalRate => SectorsSampled == 0 ? 0 : (double)SectorsRefused / SectorsSampled;
    public double BadBytesPerSector => SectorsSampled == 0 ? 0 : (double)BadBytes / SectorsSampled;
    public bool Perfect => SectorsWithC2 == 0 && SectorsRefused == 0;
}

public enum DiscHealth
{
    /// <summary>No uncorrectable errors anywhere sampled.</summary>
    Excellent,
    /// <summary>Occasional correctable trouble. Fine now, worth keeping an eye on.</summary>
    Good,
    /// <summary>Errors are common enough that the disc is working near its limits.</summary>
    Marginal,
    /// <summary>Heavy errors, or sectors that won't read at all. Image it now.</summary>
    Failing,
    /// <summary>Not enough was read to judge — usually the wrong media type.</summary>
    Unknown,
}

public sealed record QualityReport
{
    public required IReadOnlyList<QualityBand> Bands { get; init; }
    public required DiscHealth Health { get; init; }
    public required string Verdict { get; init; }
    public required IReadOnlyList<string> Findings { get; init; }
    public required int TotalSampled { get; init; }
    public required int TotalWithErrors { get; init; }
    public required int TotalRefused { get; init; }
    public required long TotalBadBytes { get; init; }
    public required TimeSpan Elapsed { get; init; }

    public double OverallErrorRate =>
        TotalSampled == 0 ? 0 : (double)TotalWithErrors / TotalSampled;
}

/// <summary>
/// Samples a disc's surface and reports how hard the drive is working to read
/// it — the measurement that tells you a disc is dying while it can still be
/// copied, rather than after it can't.
///
/// A disc doesn't fail all at once. Long before sectors become unreadable, the
/// drive's error correction starts having to work: bytes arrive wrong and get
/// fixed, invisibly, and the only outward sign is that the correction is being
/// used at all. C2 pointers expose that. A disc reporting uncorrectable bytes in
/// most sectors is still readable today and probably won't be next year.
///
/// Sampling rather than reading everything: a full CD at eight reads per sector
/// takes hours, and the shape of the damage is what matters, not an exact count.
/// Bands across the surface answer the question that actually distinguishes
/// causes — is the trouble at the outer edge (handling, scratches), at the
/// centre (hub cracking, delamination), or spread evenly (dye degradation)?
/// </summary>
[SupportedOSPlatform("windows")]
public static class DiscQualityScanner
{
    /// <summary>Bands the disc is divided into. Twelve gives a readable profile
    /// without making each band's sample too small to mean anything.</summary>
    public const int DefaultBands = 12;

    /// <summary>Sectors sampled per band. 40 is enough to separate "clean" from
    /// "occasional" from "constant" without a long wait.</summary>
    public const int DefaultSamplesPerBand = 40;

    public static QualityReport Scan(SptiDevice dev, uint totalSectors,
                                     int bands = DefaultBands,
                                     int samplesPerBand = DefaultSamplesPerBand,
                                     bool c2MsbFirst = true,
                                     IProgress<double>? progress = null,
                                     CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(dev);
        if (totalSectors == 0) throw new ArgumentException("The disc reports no sectors.", nameof(totalSectors));

        bands = Math.Max(1, bands);
        samplesPerBand = Math.Max(1, samplesPerBand);

        var started = DateTime.UtcNow;
        var results = new List<QualityBand>(bands);
        var buffer = new byte[MmcCommands.SectorBytesWithC2];

        uint perBand = Math.Max(1, totalSectors / (uint)bands);
        int totalSampled = 0, totalErrors = 0, totalRefused = 0;
        long totalBadBytes = 0;

        for (int b = 0; b < bands; b++)
        {
            cancel.ThrowIfCancellationRequested();

            uint bandStart = (uint)b * perBand;
            uint bandEnd = b == bands - 1 ? totalSectors - 1 : bandStart + perBand - 1;
            if (bandStart >= totalSectors) break;

            uint span = bandEnd - bandStart + 1;
            uint step = Math.Max(1, span / (uint)samplesPerBand);

            int sampled = 0, withC2 = 0, refused = 0;
            long badBytes = 0;

            for (int s = 0; s < samplesPerBand; s++)
            {
                cancel.ThrowIfCancellationRequested();

                uint lba = bandStart + (uint)s * step;
                if (lba > bandEnd || lba >= totalSectors) break;

                Array.Clear(buffer);
                var r = dev.SendCommand(
                    MmcCommands.ReadCdWithC2(lba, 1), buffer, SptiDataDirection.In,
                    timeoutSeconds: 20);

                sampled++;
                if (!r.Success)
                {
                    refused++;
                    continue;
                }

                var map = C2ErrorMap.Parse(
                    buffer.AsSpan(C2ErrorMap.SectorBytes, C2ErrorMap.C2Bytes), c2MsbFirst);
                if (!map.Clean)
                {
                    withC2++;
                    badBytes += map.BadByteCount;
                }
            }

            results.Add(new QualityBand
            {
                Index = b,
                StartLba = bandStart,
                EndLba = bandEnd,
                SectorsSampled = sampled,
                SectorsWithC2 = withC2,
                BadBytes = badBytes,
                SectorsRefused = refused,
            });

            totalSampled += sampled;
            totalErrors += withC2;
            totalRefused += refused;
            totalBadBytes += badBytes;

            progress?.Report((double)(b + 1) / bands);
        }

        var (health, verdict, findings) = Judge(results, totalSampled, totalErrors, totalRefused);

        return new QualityReport
        {
            Bands = results,
            Health = health,
            Verdict = verdict,
            Findings = findings,
            TotalSampled = totalSampled,
            TotalWithErrors = totalErrors,
            TotalRefused = totalRefused,
            TotalBadBytes = totalBadBytes,
            Elapsed = DateTime.UtcNow - started,
        };
    }

    /// <summary>
    /// Turn the numbers into a verdict and the reasoning behind it.
    ///
    /// The thresholds are judgement rather than standards — the Red Book
    /// specifies C1 rates, not C2-sectors-per-sample — so they're chosen to be
    /// useful rather than authoritative: at what point would you stop trusting
    /// this disc as your only copy? Where the disc IS the only copy, the honest
    /// answer is earlier than most tools suggest.
    /// </summary>
    private static (DiscHealth, string, IReadOnlyList<string>) Judge(
        IReadOnlyList<QualityBand> bands, int sampled, int errors, int refused)
    {
        var findings = new List<string>();

        if (sampled == 0)
            return (DiscHealth.Unknown, "Nothing could be sampled.",
                new[] { "No sectors were read, so the disc's condition is unknown." });

        // Refusals dominate: a sector that won't read at all is past the point
        // where error rates are the interesting measurement.
        if (refused > 0)
        {
            double refusedRate = (double)refused / sampled;
            findings.Add($"{refused:N0} of {sampled:N0} sampled sectors could not be read at all " +
                         $"({refusedRate:P1}).");
            if (refusedRate > 0.5)
                return (DiscHealth.Failing,
                    "Most of this disc cannot be read. Recover what you can, immediately.",
                    findings);
        }

        double rate = (double)errors / sampled;

        // Where is the damage, and what kind?
        //
        // Refusals and correction errors are weighted differently on purpose: a
        // sector that won't read at all is a hole in the disc, while one that
        // needed correction is a sector the drive still delivered. Judging
        // location on correction errors alone — as this first did — reports
        // damage as evenly spread when in fact every hole is in one place.
        if (bands.Count >= 4 && (errors > 0 || refused > 0))
        {
            int half = bands.Count / 2;

            // A refusal counts for five error-sectors: severe enough to
            // dominate, not so much that one stray refusal outweighs a genuine
            // spread of correction trouble.
            const int RefusalWeight = 5;
            int Weight(QualityBand b) => b.SectorsWithC2 + b.SectorsRefused * RefusalWeight;

            int inner = bands.Take(half).Sum(Weight);
            int outer = bands.Skip(half).Sum(Weight);
            int innerRefused = bands.Take(half).Sum(x => x.SectorsRefused);

            if (outer > inner * 3 && outer > 2)
            {
                findings.Add("Damage is concentrated toward the outer edge — the usual signature " +
                             "of handling: scratches, fingerprints, or a disc stored loose.");
            }
            else if (inner > outer * 3 && inner > 2)
            {
                findings.Add("Damage is concentrated near the centre of the disc. That is not " +
                             "handling — scratches sit further out. It points at the hub area: " +
                             "clamping cracks, a label lifting, or delamination starting at the " +
                             "centre and working outward.");
                if (innerRefused > 0)
                    findings.Add($"{innerRefused:N0} of the {refused:N0} unreadable sectors are in " +
                                 "the inner half. On a data disc that is where the filesystem " +
                                 "lives, which is why such a disc often appears completely dead " +
                                 "while most of its content is still intact.");
            }
            else if (errors + refused > bands.Count)
            {
                findings.Add("Damage is spread across the whole surface rather than clustered. " +
                             "That points at the disc itself — dye degradation or a poor batch — " +
                             "rather than damage from handling.");
            }

            // Runs with no refusals are where an image would succeed, and that
            // is the practically useful fact about a failing disc.
            if (refused > 0)
            {
                var runs = ContiguousRuns(bands);
                if (runs.Count > 0)
                {
                    var largest = runs.OrderByDescending(r => r.End - r.Start).First();
                    int bandCount = largest.End - largest.Start + 1;
                    if (bandCount >= 2)
                        findings.Add($"LBA {bands[largest.Start].StartLba:N0}–" +
                                     $"{bands[largest.End].EndLba:N0} had no unreadable sectors at " +
                                     "all. An image of that range would likely succeed even though " +
                                     "the disc as a whole will not open.");
                }
            }
        }

        var worst = bands.Where(x => x.SectorsSampled > 0)
                         .OrderByDescending(x => x.RefusalRate)
                         .ThenByDescending(x => x.ErrorRate)
                         .FirstOrDefault();
        if (worst is not null && (worst.SectorsWithC2 > 0 || worst.SectorsRefused > 0))
            findings.Add($"Worst region: LBA {worst.StartLba:N0}–{worst.EndLba:N0} — " +
                         $"{worst.SectorsRefused:N0} unreadable, {worst.SectorsWithC2:N0} needed " +
                         $"correction, of {worst.SectorsSampled:N0} sampled.");

        if (refused > 0)
            return (DiscHealth.Failing,
                "Parts of this disc are unreadable. Image it now, and expect gaps.",
                findings);

        if (rate == 0)
        {
            findings.Add("No uncorrectable bytes anywhere sampled — the drive read this disc " +
                         "without difficulty.");
            return (DiscHealth.Excellent,
                "This disc is in excellent condition.", findings);
        }

        if (rate < 0.02)
            return (DiscHealth.Good,
                "This disc is in good condition. A few sectors needed correction, which is " +
                "normal and not a cause for concern.", findings);

        if (rate < 0.15)
        {
            findings.Add("A disc reporting errors at this rate is still readable, but it is " +
                         "working harder than a healthy one and the trend only goes one way.");
            return (DiscHealth.Marginal,
                "This disc is deteriorating. It reads today; copy it while that's still true.",
                findings);
        }

        findings.Add("At this error rate the disc is close to the limit of what correction can " +
                     "hide. Sectors will start failing outright.");
        return (DiscHealth.Failing,
            "This disc is failing. Image it now — and verify the copy.", findings);
    }

    /// <summary>Runs of consecutive bands with no unreadable sectors — the parts
    /// of a failing disc still worth imaging.</summary>
    private static List<(int Start, int End)> ContiguousRuns(IReadOnlyList<QualityBand> bands)
    {
        var runs = new List<(int Start, int End)>();
        int i = 0;
        while (i < bands.Count)
        {
            if (bands[i].SectorsRefused > 0) { i++; continue; }
            int start = i;
            while (i < bands.Count && bands[i].SectorsRefused == 0) i++;
            runs.Add((start, i - 1));
        }
        return runs;
    }
}