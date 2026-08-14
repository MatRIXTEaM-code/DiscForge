// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;

namespace DiscForge.Core.Recovery;

/// <summary>How a single sector was resolved during reconstruction — from most to
/// least confident. The byte values are stable and double as the per-sector health
/// code the heat-map renders.</summary>
public enum SectorProvenance : byte
{
    /// <summary>Every copy agreed and the sector is valid (or has no EDC to fail).</summary>
    Agreed = 0,
    /// <summary>Copies disagreed; one copy passed its EDC and was used verbatim.</summary>
    EdcVerifiedCopy = 1,
    /// <summary>A copy was repaired from its own Reed-Solomon parity until its EDC passed.</summary>
    EccRepairedCopy = 2,
    /// <summary>A per-byte majority vote produced a sector that then passed its EDC.</summary>
    VoteVerified = 3,
    /// <summary>The voted sector was then ECC-repaired until its EDC passed.</summary>
    VoteEccRepaired = 4,
    /// <summary>No EDC to confirm (CD-DA audio, Mode 2 Form 2): the vote is best-effort.</summary>
    VoteBestEffort = 5,
    /// <summary>A data sector that never validated by any route — genuinely lost here.</summary>
    Unrecovered = 6,
}

/// <summary>The outcome of a reconstruction, with per-route tallies, the total bytes
/// ECC repaired, and a per-sector provenance map.</summary>
public sealed class ReconstructionReport
{
    public required int SourceCount { get; init; }
    public required int SectorCount { get; init; }
    public int Agreed { get; set; }
    public int EdcVerifiedCopy { get; set; }
    public int EccRepairedCopy { get; set; }
    public int VoteVerified { get; set; }
    public int VoteEccRepaired { get; set; }
    public int VoteBestEffort { get; set; }
    public int Unrecovered { get; set; }
    public long EccBytesCorrected { get; set; }

    /// <summary>One <see cref="SectorProvenance"/> code per sector, in order — a
    /// complete, replayable account of how every byte of the image came to be.</summary>
    public byte[] PerSector { get; init; } = Array.Empty<byte>();

    public List<int> UnrecoveredSectors { get; } = new();

    public bool FullyRecovered => Unrecovered == 0;
    /// <summary>Sectors that disagreed or were broken and were healed here.</summary>
    public int Repaired => EdcVerifiedCopy + EccRepairedCopy + VoteVerified + VoteEccRepaired;

    public string Summary() =>
        $"{SectorCount:N0} sector(s) from {SourceCount} cop{(SourceCount == 1 ? "y" : "ies")}: " +
        $"{Agreed:N0} agreed, {EdcVerifiedCopy:N0} EDC-verified, {EccRepairedCopy:N0} ECC-repaired, " +
        $"{VoteVerified:N0} vote-verified, {VoteEccRepaired:N0} vote+ECC, {VoteBestEffort:N0} best-effort, " +
        $"{Unrecovered:N0} unrecovered ({EccBytesCorrected:N0} byte(s) ECC-corrected).";
}

public sealed record ReconstructionResult(byte[] Image, ReconstructionReport Report);

/// <summary>
/// The unified reconstruction pipeline: turn one or more imperfect rips of the same
/// disc into the best possible image, and account for <b>every sector</b>. It layers
/// the whole recovery toolbox into a single decision per sector, in order of
/// confidence — agreement, then a copy that passes EDC, then single-read
/// Reed-Solomon ECC repair of a copy, then a majority vote, then ECC repair of the
/// vote — and records which rung each sector landed on in a per-sector provenance
/// map. That map is the disc's recovery story: what was pristine, what was healed
/// and how, and what could not be saved.
///
/// It supersedes a plain merge by adding the ECC rungs (a single scratched read can
/// often be repaired from its own parity, with or without a second copy) and by
/// keeping the provenance the merge threw away. Pure reconstruction of the owner's
/// own data; it defeats nothing.
/// </summary>
public static class DumpReconstruct
{
    public const int RawSectorSize = 2352;
    private const int MaxListed = 4096;

    public static ReconstructionResult Reconstruct(IReadOnlyList<byte[]> images, bool useEcc = true)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
            throw new ArgumentException("Provide at least one image to reconstruct from.", nameof(images));

        int len = images[0].Length;
        for (int i = 1; i < images.Count; i++)
            if (images[i].Length != len)
                throw new ArgumentException(
                    $"All copies must be the same length; copy 1 is {len:N0} bytes, copy {i + 1} is {images[i].Length:N0}.");
        if (len == 0 || len % RawSectorSize != 0)
            throw new ArgumentException($"Image length {len:N0} is not a whole number of {RawSectorSize}-byte raw sectors.");

        int sectors = len / RawSectorSize;
        var perSector = new byte[sectors];
        var report = new ReconstructionReport
        {
            SourceCount = images.Count,
            SectorCount = sectors,
            PerSector = perSector,
        };
        var outp = new byte[len];

        for (int s = 0; s < sectors; s++)
        {
            var p = ResolveSector(images, s * RawSectorSize, outp, report, useEcc);
            perSector[s] = (byte)p;
            Tally(report, p);
            if (p == SectorProvenance.Unrecovered && report.UnrecoveredSectors.Count < MaxListed)
                report.UnrecoveredSectors.Add(s);
        }

        return new ReconstructionResult(outp, report);
    }

    private static SectorProvenance ResolveSector(IReadOnlyList<byte[]> images, int at, byte[] outp,
                                                  ReconstructionReport report, bool useEcc)
    {
        var dest = outp.AsSpan(at, RawSectorSize);

        // 1) All copies identical.
        bool allSame = true;
        for (int k = 1; k < images.Count && allSame; k++)
            allSame = images[0].AsSpan(at, RawSectorSize).SequenceEqual(images[k].AsSpan(at, RawSectorSize));
        if (allSame)
        {
            var first = images[0].AsSpan(at, RawSectorSize);
            bool? valid = Validate(first);
            if (valid != false) { first.CopyTo(dest); return SectorProvenance.Agreed; }
            // All copies share the same broken data sector — a second read cannot help,
            // but its own ECC still might.
            if (useEcc && TryEcc(first, dest, out int fixedBytes)) { report.EccBytesCorrected += fixedBytes; return SectorProvenance.EccRepairedCopy; }
            first.CopyTo(dest);
            return SectorProvenance.Unrecovered;
        }

        // 2) A single copy that validates on its own.
        for (int k = 0; k < images.Count; k++)
        {
            var cand = images[k].AsSpan(at, RawSectorSize);
            if (Validate(cand) == true) { cand.CopyTo(dest); return SectorProvenance.EdcVerifiedCopy; }
        }

        // 3) ECC-repair an individual copy (single-read parity) until one validates.
        if (useEcc)
            for (int k = 0; k < images.Count; k++)
            {
                if (TryEcc(images[k].AsSpan(at, RawSectorSize), dest, out int fixedBytes))
                {
                    report.EccBytesCorrected += fixedBytes;
                    return SectorProvenance.EccRepairedCopy;
                }
            }

        // 4) Per-byte majority vote.
        MajorityVote(images, at, dest);
        bool? voteValid = Validate(dest);
        if (voteValid == true) return SectorProvenance.VoteVerified;
        if (voteValid == null) return SectorProvenance.VoteBestEffort;

        // 5) ECC-repair the voted sector.
        if (useEcc && TryEcc(dest, dest, out int vfixed)) { report.EccBytesCorrected += vfixed; return SectorProvenance.VoteEccRepaired; }

        return SectorProvenance.Unrecovered;   // dest already holds the voted best-effort bytes
    }

    /// <summary>Try to repair a raw data sector from its own RSPC parity, writing the
    /// corrected bytes to <paramref name="dest"/> on success.</summary>
    private static bool TryEcc(ReadOnlySpan<byte> sector, Span<byte> dest, out int bytesCorrected)
    {
        bytesCorrected = 0;
        if (sector.Length != RawSectorSize || !HasSync(sector)) return false;
        byte mode = sector[15];
        var work = sector.ToArray();
        EccCorrectionResult r;
        if (mode == 1)
            r = EccCorrector.CorrectMode1(work, Array.Empty<int>());
        else if (mode == 2 && (sector[18] & 0x20) == 0)
            r = EccCorrector.CorrectMode2Form1(work, Array.Empty<int>());
        else
            return false;

        if (!r.Success) return false;
        bytesCorrected = r.BytesCorrected;
        work.CopyTo(dest);
        return true;
    }

    private static void MajorityVote(IReadOnlyList<byte[]> images, int at, Span<byte> dest)
    {
        int n = images.Count;
        Span<byte> vals = stackalloc byte[n];
        for (int i = 0; i < RawSectorSize; i++)
        {
            for (int k = 0; k < n; k++) vals[k] = images[k][at + i];
            byte best = vals[0]; int bestCount = 0;
            for (int a = 0; a < n; a++)
            {
                int c = 0;
                for (int b = 0; b < n; b++) if (vals[b] == vals[a]) c++;
                if (c > bestCount) { bestCount = c; best = vals[a]; }
            }
            dest[i] = best;
        }
    }

    private static bool? Validate(ReadOnlySpan<byte> sector)
    {
        if (sector.Length != RawSectorSize || !HasSync(sector)) return null;
        byte mode = sector[15];
        if (mode == 1) return EdcEcc.VerifyMode1(sector).EdcOk;
        if (mode == 2)
        {
            if ((sector[18] & 0x20) != 0) return null;   // Form 2 EDC is optional
            return EdcEcc.VerifyMode2Form1(sector).EdcOk;
        }
        return null;
    }

    private static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0x00 || s[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }

    private static void Tally(ReconstructionReport r, SectorProvenance p)
    {
        switch (p)
        {
            case SectorProvenance.Agreed: r.Agreed++; break;
            case SectorProvenance.EdcVerifiedCopy: r.EdcVerifiedCopy++; break;
            case SectorProvenance.EccRepairedCopy: r.EccRepairedCopy++; break;
            case SectorProvenance.VoteVerified: r.VoteVerified++; break;
            case SectorProvenance.VoteEccRepaired: r.VoteEccRepaired++; break;
            case SectorProvenance.VoteBestEffort: r.VoteBestEffort++; break;
            case SectorProvenance.Unrecovered: r.Unrecovered++; break;
        }
    }
}
