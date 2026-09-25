// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Forensics;

/// <summary>The channel-level physics of one sector, once it is scrambled and EFM-encoded the way it
/// physically sits on the disc.</summary>
public sealed record SectorChannel(int Lba, double TransitionDensity, int MaxAbsDsv, double MeanRunT, int MaxRunT);

/// <summary>What a weak-sector scan concluded.</summary>
public sealed record WeakSectorReport
{
    public required int SectorsAnalyzed { get; init; }
    public required double MeanTransitionDensity { get; init; }
    public required IReadOnlyList<SectorChannel> Weak { get; init; }
    public bool AnyWeak => Weak.Count > 0;

    public string Summary() => AnyWeak
        ? $"{Weak.Count} of {SectorsAnalyzed:N0} sector(s) are channel-weak — their encoded stream is hard to track (a deliberate weak-sector layout)."
        : $"{SectorsAnalyzed:N0} sector(s) analyzed — none are channel-weak.";
}

/// <summary>
/// Weak-sector prediction — model copy protection at the physical layer where it actually lives. A
/// SafeDisc-style "weak sector" is not corrupt data; it is data whose <i>scrambled</i> form, once EFM-
/// encoded, yields a channel stream that stresses the servo — either too few transitions or (the
/// dominant real signature, see below) a Digital Sum Value that wanders far from zero — so different
/// drives read it differently or not at all. This runs that pipeline — CD scramble (ECMA-130) then EFM
/// (<see cref="Efm"/>) — for each sector and measures the result: transition density, DSV excursion, run
/// lengths. Sectors whose channel is a stark outlier on either axis are exactly the deliberately-weak
/// ones, predicted from the data alone. Pure modelling and detection; it explains and flags the physics,
/// and defeats nothing.
///
/// Uses <see cref="Efm"/>'s authoritative ECMA-130 codebook (landed once the flux/RF moonshot's data
/// swap was done — see docs/DIFFERENTIATORS.md). That swap changed which signature actually dominates:
/// content chosen to defeat scrambling (e.g. data equal to the scramble sequence, so scrambling recovers
/// all-zero) turns out, under the real table, to have an almost <i>normal</i> transition density but a
/// Digital Sum Value dozens of times any ordinary sector's — DSV excursion, not density collapse, is
/// the real tell. Both are still checked, since either one independently means a stressed channel.
/// Encoding every sector is heavy, so callers can bound how many are analysed.
/// </summary>
public static class WeakSectorAnalyzer
{
    private const int RawSectorSize = 2352;

    /// <summary>Below this fraction of the disc's mean transition density, or above this multiple of
    /// its mean DSV excursion, a sector's channel is a stark enough outlier to call weak. The DSV
    /// multiple has the wide margin it does because genuinely weak content (see the class doc comment)
    /// is not a marginal outlier on this axis — it is 50-100x the population's typical excursion — so a
    /// generous threshold still comfortably separates it from a disc's ordinary sector-to-sector noise.</summary>
    private const double DensityFactor = 0.6;
    private const double DsvFactor = 5.0;

    /// <summary>Measure one stored (unscrambled) raw sector's on-disc channel health.</summary>
    public static SectorChannel Measure(int lba, ReadOnlySpan<byte> stored2352)
    {
        if (stored2352.Length != RawSectorSize)
            throw new ArgumentException($"A raw sector is {RawSectorSize} bytes.", nameof(stored2352));
        var onDisc = stored2352.ToArray();
        CdScrambler.ScrambleInPlace(onDisc);        // the form actually written to the surface
        var ch = Efm.Analyze(onDisc);
        return new SectorChannel(lba, ch.TransitionDensity, ch.MaxAbsDsv, ch.MeanRunT, ch.MaxRunT);
    }

    /// <summary>Scan a raw image for channel-weak sectors. Only sync-bearing (data) sectors are
    /// scrambled + EFM-modelled; <paramref name="maxSectors"/> bounds the (expensive) work.</summary>
    public static WeakSectorReport Analyze(byte[] rawImage, int maxSectors = 4096)
    {
        ArgumentNullException.ThrowIfNull(rawImage);
        int count = rawImage.Length / RawSectorSize;

        var metrics = new List<SectorChannel>();
        for (int i = 0; i < count && metrics.Count < maxSectors; i++)
        {
            var sec = rawImage.AsSpan(i * RawSectorSize, RawSectorSize);
            if (!HasSync(sec)) continue;
            metrics.Add(Measure(i, sec));
        }

        if (metrics.Count == 0)
            return new WeakSectorReport { SectorsAnalyzed = 0, MeanTransitionDensity = 0, Weak = System.Array.Empty<SectorChannel>() };

        double mean = metrics.Average(m => m.TransitionDensity);
        double meanDsv = metrics.Average(m => m.MaxAbsDsv);
        // Weak either way: transition density collapsed well below the disc's norm, or the DC balance
        // wandered far past it — see the class doc comment for why DSV is the more common real tell.
        double densityThreshold = mean * DensityFactor;
        double dsvThreshold = meanDsv * DsvFactor;
        var weak = metrics.Where(m => m.TransitionDensity < densityThreshold || m.MaxAbsDsv > dsvThreshold)
                          .OrderBy(m => m.TransitionDensity)
                          .ToList();

        return new WeakSectorReport
        {
            SectorsAnalyzed = metrics.Count,
            MeanTransitionDensity = mean,
            Weak = weak,
        };
    }

    public static string Render(WeakSectorReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        sb.AppendLine($"  typical transition density: {r.MeanTransitionDensity:P1}");
        foreach (var w in r.Weak.Take(32))
            sb.AppendLine($"  LBA {w.Lba}: transitions {w.TransitionDensity:P1}, peak DSV {w.MaxAbsDsv}, longest run {w.MaxRunT}T");
        return sb.ToString().TrimEnd();
    }

    private static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0x00 || s[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }
}
