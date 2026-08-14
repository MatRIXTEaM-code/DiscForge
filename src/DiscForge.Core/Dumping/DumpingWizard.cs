// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;

namespace DiscForge.Core.Dumping;

/// <summary>
/// Detects the sample offset that best aligns one PCM rip to a reference. A drive's
/// read offset shifts every audio sample by a fixed amount; comparing a rip against
/// a known-good reference of the same track reveals that shift, which is what a
/// Redump-grade dump has to correct. This is pure sample bookkeeping on 16-bit
/// stereo PCM — the same clean-room territory as <c>ReadOffset</c>.
/// </summary>
public static class ReadOffsetDetect
{
    private const int BytesPerSample = 4;   // 16-bit stereo

    /// <summary>
    /// Return the offset (in stereo samples) at which <paramref name="rip"/> best
    /// matches <paramref name="reference"/>: the value <c>off</c> for which
    /// <c>rip[i] ≈ reference[i + off]</c> over the overlap. Searched over
    /// ±<paramref name="maxSamples"/>. Positive means the rip sits later than the
    /// reference (a positive drive read offset).
    /// </summary>
    public static int DetectSampleOffset(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> rip, int maxSamples)
    {
        if (reference.Length % BytesPerSample != 0 || rip.Length % BytesPerSample != 0)
            throw new ArgumentException("PCM must be a whole number of 4-byte stereo samples.");
        if (maxSamples < 0) throw new ArgumentException("maxSamples must be non-negative.", nameof(maxSamples));

        int refN = reference.Length / BytesPerSample;
        int ripN = rip.Length / BytesPerSample;
        int minOverlap = Math.Max(1, Math.Min(refN, ripN) / 2);

        long bestAvg = long.MaxValue;
        int bestOff = 0;
        for (int off = -maxSamples; off <= maxSamples; off++)
        {
            long sad = 0;
            int count = 0;
            for (int i = 0; i < ripN; i++)
            {
                int r = i + off;
                if (r < 0 || r >= refN) continue;
                int ri = i * BytesPerSample, rr = r * BytesPerSample;
                sad += Math.Abs(rip[ri] - reference[rr])
                     + Math.Abs(rip[ri + 1] - reference[rr + 1])
                     + Math.Abs(rip[ri + 2] - reference[rr + 2])
                     + Math.Abs(rip[ri + 3] - reference[rr + 3]);
                count++;
            }
            if (count < minOverlap) continue;
            long avg = sad / count;
            if (avg < bestAvg) { bestAvg = avg; bestOff = off; }
        }
        return bestOff;
    }
}

/// <summary>Signals that describe how good a dump is.</summary>
public sealed record DumpQuality(
    int TotalSectors, int EdcCheckable, int EdcFailed, int C2Errors, int Unrecovered, bool OffsetKnown);

/// <summary>A 0-100 confidence score, a letter grade, and a one-line summary.</summary>
public sealed record DumpScore(int Score, char Grade, string Summary);

/// <summary>
/// Turns a dump's quality signals into a single, honest confidence score — the
/// number a guided dumping wizard shows so a person knows whether a rip is
/// submission-grade or needs another pass. Unrecovered sectors are treated as
/// disqualifying (an incomplete dump can never be an "A"); EDC failures and C2
/// errors scale the score down; a known, corrected read offset earns the last few
/// points of confidence.
/// </summary>
public static class DumpConfidence
{
    public static DumpScore Score(DumpQuality q)
    {
        ArgumentNullException.ThrowIfNull(q);
        if (q.TotalSectors == 0)
            return new DumpScore(0, 'F', "Empty image — nothing to score.");

        int score;
        if (q.Unrecovered > 0)
            score = Math.Clamp(50 - q.Unrecovered * 5, 0, 50);       // never above a D
        else
        {
            int penalty = q.EdcFailed * 15 + Math.Min(q.C2Errors, 20) + (q.OffsetKnown ? 0 : 5);
            score = Math.Clamp(100 - penalty, 0, 100);
        }

        char grade = score >= 90 ? 'A' : score >= 80 ? 'B' : score >= 70 ? 'C' : score >= 60 ? 'D' : 'F';
        string summary =
            $"Score {score}/100 (grade {grade}): {q.EdcFailed} EDC failure(s), {q.Unrecovered} unrecovered, " +
            $"{q.C2Errors} C2 error(s), read offset {(q.OffsetKnown ? "known" : "unknown")}.";
        return new DumpScore(score, grade, summary);
    }

    /// <summary>Scan a raw (2352-byte-sector) image and count how many data sectors
    /// can be EDC-checked and how many fail — the EDC half of the quality signals.</summary>
    public static DumpQuality ScanRaw(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int sectors = image.Length / 2352;
        int checkable = 0, failed = 0;
        for (int s = 0; s < sectors; s++)
        {
            var sec = image.AsSpan(s * 2352, 2352);
            if (!HasSync(sec)) continue;                         // audio: no EDC
            byte mode = sec[15];
            bool edcOk;
            if (mode == 1) edcOk = EdcEcc.VerifyMode1(sec).EdcOk;
            else if (mode == 2 && (sec[18] & 0x20) == 0) edcOk = EdcEcc.VerifyMode2Form1(sec).EdcOk;
            else continue;                                       // Mode 2 Form 2 has no reliable EDC
            checkable++;
            if (!edcOk) failed++;
        }
        return new DumpQuality(sectors, checkable, failed, C2Errors: 0, Unrecovered: 0, OffsetKnown: false);
    }

    private static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0x00 || s[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }
}
