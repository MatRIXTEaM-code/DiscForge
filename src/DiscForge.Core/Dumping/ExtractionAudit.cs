// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;

namespace DiscForge.Core.Dumping;

/// <summary>
/// The post-extraction audit: an independent pass over the file a dump just
/// WROTE, trusting nothing the extraction engine said about itself. This
/// exists because of the half-void dump — a drive reported SUCCESS while
/// muting 135,417 sectors to zeros, the engine believed it, and the lie sat
/// on disk for days because nothing ever looked at the output by default.
///
/// The audit reads every sector back. Data spans get a sync census (a raw
/// data sector without the 12-byte sync pattern is structurally impossible on
/// a healthy dump), an all-zero census, and a sampled EDC sweep over
/// recognizable Mode 1 / XA sectors. Audio and boundary spans get the
/// all-zero census only — digital silence is legitimate audio, so it is
/// reported, never failed. The verdict is deliberately blunt: any sync-less
/// or EDC-failing sector in a data span fails the audit, even if the
/// extraction graded itself COMPLETE. Disagreement between the two is
/// precisely the finding.
///
/// Not to be confused with <see cref="Verify.DumpAudit"/>, the cue-level
/// dump-audit verb (DAT match, sidecar holes, whole-file EDC). This one is
/// span-aware and runs INLINE at the end of every drive extraction, before
/// the operator has a chance not to bother.
/// </summary>
public static class ExtractionAudit
{
    /// <summary>One contiguous region of the audited file, in file-sector coordinates.</summary>
    public sealed record SpanSpec(string Label, long FileOffsetSectors, long SectorCount, bool Audio, bool Boundary);

    public sealed record SpanFinding
    {
        public required string Label { get; init; }
        public required long Sectors { get; init; }
        public required bool Audio { get; init; }
        public required bool Boundary { get; init; }
        /// <summary>Data spans: sectors missing the 12-byte sync pattern. Audio spans: always 0 (not applicable).</summary>
        public long SyncMissing { get; init; }
        /// <summary>Sectors that are entirely zero, whatever the span type.</summary>
        public long AllZero { get; init; }
        /// <summary>Longest run of consecutive all-zero sectors — a void reads as one long run, scattered silence doesn't.</summary>
        public long LongestZeroRun { get; init; }
        public int EdcChecked { get; init; }
        public int EdcErrors { get; init; }
    }

    public sealed record Result
    {
        public required IReadOnlyList<SpanFinding> Spans { get; init; }
        public required IReadOnlyList<string> Failures { get; init; }
        public bool Passed => Failures.Count == 0;
        public string Grade => Passed ? "PASS" : "FAIL";
    }

    /// <summary>
    /// Audit a file of raw 2352-byte sectors against the span layout the
    /// extraction used. <paramref name="edcSampleTarget"/> caps EDC checks per
    /// data span (evenly spaced; first and last sectors always included);
    /// pass 0 to check every sector.
    /// </summary>
    public static Result Run(Stream image, IReadOnlyList<SpanSpec> spans, int edcSampleTarget = 2048)
    {
        var findings = new List<SpanFinding>();
        var failures = new List<string>();
        var main = new byte[2352];

        foreach (var span in spans)
        {
            long syncMissing = 0, allZero = 0, zeroRun = 0, longestZeroRun = 0;
            int edcChecked = 0, edcErrors = 0;
            long edcStep = span.Audio || span.Boundary || edcSampleTarget <= 0
                ? 1
                : Math.Max(1, span.SectorCount / edcSampleTarget);

            for (long i = 0; i < span.SectorCount; i++)
            {
                image.Position = (span.FileOffsetSectors + i) * 2352;
                image.ReadExactly(main, 0, 2352);

                bool zero = IsAllZero(main);
                if (zero) { allZero++; zeroRun++; longestZeroRun = Math.Max(longestZeroRun, zeroRun); }
                else zeroRun = 0;

                if (span.Audio) continue;               // silence is legitimate audio; census only

                if (!HasSync(main)) { syncMissing++; continue; }
                if (i % edcStep == 0 || i == span.SectorCount - 1)
                {
                    var check = CheckDataSector(main);
                    if (check is { } c)
                    {
                        edcChecked++;
                        if (!c.EdcOk) edcErrors++;
                    }
                }
            }

            findings.Add(new SpanFinding
            {
                Label = span.Label,
                Sectors = span.SectorCount,
                Audio = span.Audio,
                Boundary = span.Boundary,
                SyncMissing = syncMissing,
                AllZero = allZero,
                LongestZeroRun = longestZeroRun,
                EdcChecked = edcChecked,
                EdcErrors = edcErrors,
            });

            if (!span.Audio && !span.Boundary)
            {
                if (syncMissing > 0)
                    failures.Add($"{span.Label}: {syncMissing:N0} sector(s) carry no sync pattern — " +
                                 "unstructured (muted/zero-filled?) data inside a data span");
                if (edcErrors > 0)
                    failures.Add($"{span.Label}: {edcErrors:N0} of {edcChecked:N0} sampled sector(s) fail EDC");
            }
        }

        return new Result { Spans = findings, Failures = failures };
    }

    private static bool IsAllZero(ReadOnlySpan<byte> s)
    {
        for (int i = 0; i < s.Length; i++) if (s[i] != 0) return false;
        return true;
    }

    private static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0 || s[11] != 0) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }

    /// <summary>
    /// EDC-check one unscrambled data sector: Mode 1 EDC, or XA Form 1/2 EDC
    /// when the subheader duplicates. Null when nothing is checkable
    /// (formless Mode 2, or an unrecognized mode byte).
    /// </summary>
    private static (bool EdcOk, string Kind)? CheckDataSector(ReadOnlySpan<byte> main)
    {
        if (main[15] == 1)
            return (EdcEcc.VerifyMode1(main).EdcOk, "Mode 1 EDC");
        if (main[15] == 2)
        {
            if (main[16] != main[20] || main[17] != main[21] ||
                main[18] != main[22] || main[19] != main[23]) return null;
            bool form2 = (main[18] & 0x20) != 0;
            if (!form2)
            {
                uint edc = EdcEcc.ComputeEdc(main[16..2072]);
                uint stored = (uint)main[2072] | ((uint)main[2073] << 8)
                            | ((uint)main[2074] << 16) | ((uint)main[2075] << 24);
                return (edc == stored, "XA Form 1 EDC");
            }
            uint edc2 = EdcEcc.ComputeEdc(main[16..2348]);
            uint stored2 = (uint)main[2348] | ((uint)main[2349] << 8)
                         | ((uint)main[2350] << 16) | ((uint)main[2351] << 24);
            if (stored2 == 0) return null;
            return (edc2 == stored2, "XA Form 2 EDC");
        }
        return null;
    }
}
