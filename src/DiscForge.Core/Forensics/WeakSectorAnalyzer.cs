// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

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
/// encoded, yields a channel stream with too few transitions and a wandering DC balance, so different
/// drives read it differently or not at all. This runs that pipeline — CD scramble (ECMA-130) then EFM
/// (<see cref="Efm"/>) — for each sector and measures the result: transition density, DSV excursion, run
/// lengths. Sectors whose channel is a stark outlier (far below the disc's typical transition density)
/// are exactly the deliberately-weak ones, predicted from the data alone. Pure modelling and detection;
/// it explains and flags the physics, and defeats nothing.
///
/// (Uses <see cref="Efm"/>'s canonical codeword enumeration; the run-length/DSV mechanics it depends on
/// are faithful. Encoding every sector is heavy, so callers can bound how many are analysed.)
/// </summary>
public static class WeakSectorAnalyzer
{
    private const int RawSectorSize = 2352;

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
        // A weak sector's transition density collapses well below the disc's norm.
        double threshold = mean * 0.6;
        var weak = metrics.Where(m => m.TransitionDensity < threshold)
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
