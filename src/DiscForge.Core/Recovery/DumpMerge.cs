// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;

namespace DiscForge.Core.Recovery;

/// <summary>What happened to one sector when the copies were merged.</summary>
public enum SectorOutcome
{
    /// <summary>Every copy agreed byte-for-byte.</summary>
    Identical,
    /// <summary>The copies disagreed, but one copy's sector passed its EDC — so we
    /// know which one is correct and used it verbatim.</summary>
    EdcRecovered,
    /// <summary>The copies disagreed and none was individually correct, but a
    /// per-byte majority vote reconstructed a sector that THEN passed its EDC —
    /// a sector recovered from fragments no single copy had whole.</summary>
    VoteVerified,
    /// <summary>A sector with no EDC to check (CD-DA audio, Mode 2 Form 2): the
    /// per-byte majority is the best we can do, and it can't be independently
    /// confirmed.</summary>
    VoteBestEffort,
    /// <summary>A data sector that disagreed and never validated, even after
    /// voting — genuinely unrecoverable from these copies.</summary>
    Unrecovered,
}

/// <summary>The outcome of a merge, with per-outcome tallies and the list of
/// sectors that could not be recovered.</summary>
public sealed class DumpMergeReport
{
    public required int SourceCount { get; init; }
    public required int SectorCount { get; init; }
    public int Identical { get; set; }
    public int EdcRecovered { get; set; }
    public int VoteVerified { get; set; }
    public int VoteBestEffort { get; set; }
    public int Unrecovered { get; set; }

    /// <summary>Indices of sectors that never validated (capped for reporting).</summary>
    public List<int> UnrecoveredSectors { get; } = new();

    /// <summary>True when nothing was left unrecovered.</summary>
    public bool FullyRecovered => Unrecovered == 0;

    /// <summary>Sectors that disagreed between copies and were healed here.</summary>
    public int Repaired => EdcRecovered + VoteVerified;

    public string Summary() =>
        $"{SectorCount:N0} sector(s) from {SourceCount} cop{(SourceCount == 1 ? "y" : "ies")}: " +
        $"{Identical:N0} agreed, {EdcRecovered:N0} EDC-recovered, {VoteVerified:N0} vote-verified, " +
        $"{VoteBestEffort:N0} best-effort, {Unrecovered:N0} unrecovered.";
}

public sealed record DumpMergeResult(byte[] Image, DumpMergeReport Report);

/// <summary>
/// Merges several imperfect rips of the SAME disc into one best-possible image.
/// This is the core of multi-read recovery: a disc too scratched for any single
/// read to recover can often be rebuilt from the good parts of two or three reads
/// — different drives, different attempts, a second copy of the disc.
///
/// For each sector, in order of confidence:
///   1. if every copy agrees, keep it;
///   2. else, if one copy's sector passes its EDC (Mode 1 or Mode 2 Form 1), that
///      copy is provably correct — use it;
///   3. else, take a per-byte majority vote across the copies and re-check the EDC:
///      if the vote now validates, the sector has been reassembled from fragments
///      no single copy held whole;
///   4. for sectors with no EDC (CD-DA audio, Mode 2 Form 2) the majority vote is
///      the best available and is reported as such;
///   5. anything still failing is reported as unrecovered, with the sector list.
///
/// Pure recovery, entirely inside the clean-room line: it reconstructs a faithful
/// image from copies the owner already has. It defeats nothing. The physical
/// multi-read on real hardware feeds this engine; the engine itself is here and
/// fully testable.
/// </summary>
public static class DumpMerge
{
    public const int RawSectorSize = 2352;

    /// <summary>How many unrecovered sector indices to keep in the report.</summary>
    private const int MaxListed = 4096;

    public static DumpMergeResult Merge(IReadOnlyList<byte[]> images, int sectorSize = RawSectorSize)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
            throw new ArgumentException("Provide at least one image to merge.", nameof(images));
        if (sectorSize <= 0)
            throw new ArgumentException("Sector size must be positive.", nameof(sectorSize));

        int len = images[0].Length;
        for (int i = 1; i < images.Count; i++)
            if (images[i].Length != len)
                throw new ArgumentException(
                    $"All copies must be the same length; copy 1 is {len:N0} bytes, copy {i + 1} is {images[i].Length:N0}.");
        if (len % sectorSize != 0)
            throw new ArgumentException($"Image length {len:N0} is not a whole number of {sectorSize}-byte sectors.");

        int sectors = len / sectorSize;
        var report = new DumpMergeReport { SourceCount = images.Count, SectorCount = sectors };
        var outp = new byte[len];

        var cand = new byte[images.Count][];
        for (int s = 0; s < sectors; s++)
        {
            int at = s * sectorSize;
            for (int k = 0; k < images.Count; k++)
                cand[k] = images[k];   // reference; we index with `at` below

            MergeSector(cand, at, sectorSize, outp, s, report);
        }

        return new DumpMergeResult(outp, report);
    }

    private static void MergeSector(byte[][] images, int at, int size, byte[] outp, int sectorIndex, DumpMergeReport report)
    {
        // 1) All identical?
        bool allSame = true;
        for (int k = 1; k < images.Length && allSame; k++)
            allSame = SpanEqual(images[0].AsSpan(at, size), images[k].AsSpan(at, size));
        if (allSame)
        {
            images[0].AsSpan(at, size).CopyTo(outp.AsSpan(at, size));
            report.Identical++;
            return;
        }

        // 2) A single copy that validates on its own.
        for (int k = 0; k < images.Length; k++)
        {
            if (Validate(images[k].AsSpan(at, size)) == true)
            {
                images[k].AsSpan(at, size).CopyTo(outp.AsSpan(at, size));
                report.EdcRecovered++;
                return;
            }
        }

        // 3) Per-byte majority vote.
        var voted = outp.AsSpan(at, size);
        MajorityVote(images, at, size, voted);
        bool? v = Validate(voted);
        if (v == true) { report.VoteVerified++; return; }
        if (v == null) { report.VoteBestEffort++; return; }   // no EDC to confirm (audio / Form 2)

        // 4) A data sector that still fails — genuinely unrecovered.
        report.Unrecovered++;
        if (report.UnrecoveredSectors.Count < MaxListed)
            report.UnrecoveredSectors.Add(sectorIndex);
    }

    /// <summary>Write the per-byte majority value into <paramref name="dest"/>. Ties
    /// resolve to the earliest copy, so the result is deterministic.</summary>
    private static void MajorityVote(byte[][] images, int at, int size, Span<byte> dest)
    {
        int n = images.Length;
        Span<byte> vals = stackalloc byte[n];
        for (int i = 0; i < size; i++)
        {
            for (int k = 0; k < n; k++) vals[k] = images[k][at + i];

            byte best = vals[0];
            int bestCount = 0;
            for (int a = 0; a < n; a++)
            {
                int c = 0;
                for (int b = 0; b < n; b++) if (vals[b] == vals[a]) c++;
                if (c > bestCount) { bestCount = c; best = vals[a]; }   // '>' keeps the earliest on ties
            }
            dest[i] = best;
        }
    }

    /// <summary>
    /// Is this a data sector we can check, and does it pass its EDC?
    ///   true  — a data sector whose EDC validates (provably correct);
    ///   false — a data sector whose EDC fails (known bad);
    ///   null  — no EDC to check (CD-DA audio, Mode 2 Form 2, or non-raw sectors).
    /// </summary>
    internal static bool? Validate(ReadOnlySpan<byte> sector)
    {
        if (sector.Length != RawSectorSize) return null;   // EDC/ECC is defined on 2352-byte raw sectors
        if (!HasSync(sector)) return null;                 // audio: no sync, no EDC

        byte mode = sector[15];
        if (mode == 1)
            return EdcEcc.VerifyMode1(sector).EdcOk;
        if (mode == 2)
        {
            bool form2 = (sector[18] & 0x20) != 0;         // subheader submode bit 5
            if (form2) return null;                        // Form 2 EDC is optional — don't rely on it
            return EdcEcc.VerifyMode2Form1(sector).EdcOk;
        }
        return null;
    }

    /// <summary>The 12-byte CD sync mark: 00 FF×10 00.</summary>
    private static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0x00 || s[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }

    private static bool SpanEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceEqual(b);
}
