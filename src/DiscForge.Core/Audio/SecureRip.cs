// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Audio;

/// <summary>
/// The secure-rip brain — the EAC-style depth that decides, from multi-pass evidence, whether an
/// audio track's bytes can be trusted and exactly what to re-read when they can't. It consumes what
/// the rip layer already gathers (per-sector C2 flags, pass agreement, drive read offset, an
/// AccurateRip verdict where the database knows the pressing) and produces a four-tier grade per
/// track plus a targeted, escalating re-read plan: mismatched sectors get best-of-N voting with an
/// odd pass count, C2-flagged sectors get cache-defeating re-reads, and clean tracks are left alone
/// rather than re-read ritualistically. Pure and unit-tested; the hardware layer executes the plan.
///
/// Grading is deliberately conservative, in the "provably correct or declined" spirit:
/// <b>Verified</b> needs an independent AccurateRip match — self-consistency alone can never earn
/// it, because a drive that mis-reads deterministically agrees with itself. <b>Consistent</b> is
/// the honest ceiling without external corroboration.
/// </summary>
public static class SecureRip
{
    /// <summary>Per-sector evidence, one byte per sector.</summary>
    public enum SectorState : byte
    {
        /// <summary>Every pass agreed, no C2 flag.</summary>
        Clean = 0,
        /// <summary>The drive raised a C2 error pointer on at least one pass.</summary>
        C2Flagged = 1,
        /// <summary>Passes returned different bytes (jitter, marginal media) without C2.</summary>
        PassMismatch = 2,
        /// <summary>No pass returned the sector at all.</summary>
        Unreadable = 3,
    }

    public sealed record TrackEvidence
    {
        public required int Number { get; init; }
        /// <summary>One <see cref="SectorState"/> per sector of the track.</summary>
        public required byte[] Sectors { get; init; }
        /// <summary>How many full read passes produced this evidence.</summary>
        public required int Passes { get; init; }
        /// <summary>AccurateRip: true = matched, false = pressing known but MISmatched, null = not in the database.</summary>
        public bool? AccurateRipMatch { get; init; }
        /// <summary>AccurateRip submission count backing a match (0 when unmatched/unknown).</summary>
        public int AccurateRipConfidence { get; init; }
    }

    public enum TrackGrade
    {
        /// <summary>Independently corroborated: AccurateRip matched and nothing is unreadable.</summary>
        Verified,
        /// <summary>No external corroboration available, but ≥2 passes agreed on every sector.</summary>
        Consistent,
        /// <summary>C2 flags, pass mismatches, or an AccurateRip MISmatch — treat with suspicion.</summary>
        Suspect,
        /// <summary>Sectors remain unreadable; the audio has holes.</summary>
        Failed,
    }

    public sealed record TrackVerdict(int Number, TrackGrade Grade, int Clean, int C2Flagged,
                                      int Mismatched, int Unreadable, string Reason);

    public sealed record RereadRange(int StartSector, int Count, SectorState Worst);

    public sealed record RereadPlan
    {
        public required int Track { get; init; }
        public required IReadOnlyList<RereadRange> Ranges { get; init; }
        /// <summary>Suggested passes for the re-read: odd, so best-of-N voting can't tie.</summary>
        public required int SuggestedPasses { get; init; }
        public required string Strategy { get; init; }
        public bool Nothing => Ranges.Count == 0;
    }

    // ---- grading ------------------------------------------------------------

    public static TrackVerdict Grade(TrackEvidence t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.Sectors.Length == 0)
            return new TrackVerdict(t.Number, TrackGrade.Suspect, 0, 0, 0, 0,
                "No per-sector evidence at all — a grade cannot be earned on zero sectors.");
        int clean = 0, c2 = 0, mismatch = 0, unreadable = 0;
        foreach (var s in t.Sectors)
        {
            switch ((SectorState)s)
            {
                case SectorState.Clean: clean++; break;
                case SectorState.C2Flagged: c2++; break;
                case SectorState.PassMismatch: mismatch++; break;
                default: unreadable++; break;
            }
        }

        if (unreadable > 0)
            return new TrackVerdict(t.Number, TrackGrade.Failed, clean, c2, mismatch, unreadable,
                $"{unreadable:N0} sector(s) never read — the track has holes.");

        if (t.AccurateRipMatch == false)
            return new TrackVerdict(t.Number, TrackGrade.Suspect, clean, c2, mismatch, unreadable,
                "AccurateRip knows this pressing and the rip does NOT match it.");

        if (c2 > 0 || mismatch > 0)
            return new TrackVerdict(t.Number, TrackGrade.Suspect, clean, c2, mismatch, unreadable,
                $"{c2:N0} C2-flagged and {mismatch:N0} pass-mismatched sector(s) remain.");

        if (t.AccurateRipMatch == true)
            return new TrackVerdict(t.Number, TrackGrade.Verified, clean, c2, mismatch, unreadable,
                $"AccurateRip match (confidence {t.AccurateRipConfidence}) with no flagged sectors.");

        if (t.Passes >= 2)
            return new TrackVerdict(t.Number, TrackGrade.Consistent, clean, c2, mismatch, unreadable,
                $"All sectors agreed across {t.Passes} passes; pressing not in AccurateRip, so " +
                "independent verification isn't possible — consistent is the honest ceiling.");

        return new TrackVerdict(t.Number, TrackGrade.Suspect, clean, c2, mismatch, unreadable,
            "Single pass with no external corroboration — re-read before trusting it.");
    }

    // ---- re-read planning ---------------------------------------------------

    /// <summary>
    /// Plan the targeted re-read for a track: suspect sectors coalesced into ranges (padded by
    /// <paramref name="padSectors"/> so the drive settles before the sector that matters), with an
    /// escalating odd pass count — more disagreement, more votes.
    /// </summary>
    public static RereadPlan PlanReread(TrackEvidence t, int padSectors = 2)
    {
        ArgumentNullException.ThrowIfNull(t);
        var ranges = new List<RereadRange>();
        int n = t.Sectors.Length;
        int i = 0;
        while (i < n)
        {
            if ((SectorState)t.Sectors[i] == SectorState.Clean) { i++; continue; }
            int start = i;
            var worst = SectorState.Clean;
            while (i < n && (SectorState)t.Sectors[i] != SectorState.Clean)
            {
                var s = (SectorState)t.Sectors[i];
                if (s > worst) worst = s;
                i++;
            }
            int from = Math.Max(0, start - padSectors);
            int to = Math.Min(n, i + padSectors);
            ranges.Add(new RereadRange(from, to - from, worst));
        }

        // Merge ranges the padding made adjacent/overlapping.
        var merged = new List<RereadRange>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.StartSector <= merged[^1].StartSector + merged[^1].Count)
            {
                var prev = merged[^1];
                int end = Math.Max(prev.StartSector + prev.Count, r.StartSector + r.Count);
                merged[^1] = new RereadRange(prev.StartSector, end - prev.StartSector,
                                             (SectorState)Math.Max((byte)prev.Worst, (byte)r.Worst));
            }
            else merged.Add(r);
        }

        var overallWorst = merged.Count == 0 ? SectorState.Clean : merged.Max(r => r.Worst);
        (int passes, string strategy) = overallWorst switch
        {
            SectorState.Clean => (0, "Nothing to re-read."),
            SectorState.C2Flagged => (3,
                "C2-flagged: re-read each range 3× with cache-defeating seeks between passes; accept when C2 clears and all passes agree."),
            SectorState.PassMismatch => (5,
                "Pass-mismatched: best-of-5 per-byte vote across cache-defeating re-reads; a value must win a strict majority or the sector stays flagged."),
            _ => (7,
                "Unreadable: 7 attempts per range at reduced speed; sectors that still fail are recorded as holes in the bad-sector map, never silently zero-filled."),
        };
        return new RereadPlan { Track = t.Number, Ranges = merged, SuggestedPasses = passes, Strategy = strategy };
    }

    // ---- offset -------------------------------------------------------------

    /// <summary>
    /// The sample shift to apply to raw audio: the drive's read offset plus any detected
    /// per-disc offset (from <c>offset-detect</c>), both in 16-bit stereo samples. AccurateRip
    /// verification must be computed on offset-corrected audio or every pressing looks wrong.
    /// </summary>
    public static int CombinedOffsetSamples(int driveReadOffsetSamples, int detectedDiscOffsetSamples = 0)
        => driveReadOffsetSamples + detectedDiscOffsetSamples;
}
