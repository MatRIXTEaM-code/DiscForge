// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace DiscForge.Core.PlayStation;

/// <summary>How a page's error-correcting code came out.</summary>
public enum Ps2EccStatus { Clean, Corrected, Failed }

/// <summary>A page whose stored ECC did not match its data.</summary>
public sealed record Ps2EccPageFinding(int Page, int Chunk, Ps2EccStatus Status, string Detail);

/// <summary>The result of checking a PS2 memory card's per-page Hamming ECC.</summary>
public sealed record Ps2EccReport
{
    public required bool HasEcc { get; init; }
    public required int TotalPages { get; init; }
    public required int CleanPages { get; init; }
    public required int CorrectedPages { get; init; }
    public required int FailedPages { get; init; }
    public required IReadOnlyList<Ps2EccPageFinding> Findings { get; init; }

    public Ps2EccStatus Status =>
        !HasEcc ? Ps2EccStatus.Clean
        : FailedPages > 0 ? Ps2EccStatus.Failed
        : CorrectedPages > 0 ? Ps2EccStatus.Corrected
        : Ps2EccStatus.Clean;

    public string Summary()
    {
        if (!HasEcc)
            return "PS2 card ECC: this dump has no ECC spare area (512-byte pages) — nothing to check.";
        var sb = new StringBuilder();
        string verdict = Status switch
        {
            Ps2EccStatus.Clean => "CLEAN — every page's ECC matches its data.",
            Ps2EccStatus.Corrected => $"CORRECTABLE — {CorrectedPages} page(s) hold single-bit errors that ECC can repair.",
            _ => $"CORRUPT — {FailedPages} page(s) have errors ECC cannot correct (2+ bits).",
        };
        sb.AppendLine($"PS2 card ECC: {verdict}");
        sb.AppendLine($"  {TotalPages:N0} pages: {CleanPages:N0} clean, {CorrectedPages:N0} correctable, {FailedPages:N0} uncorrectable.");
        foreach (var f in Findings.Take(100))
            sb.AppendLine($"  [{f.Status}] page {f.Page}, chunk {f.Chunk}: {f.Detail}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// ps2mc-ecc — verify (and optionally repair) the per-page Hamming ECC of a PlayStation 2 memory-card dump.
/// A "with-ECC" card image stores, in each 528-byte physical page, 512 data bytes plus a 16-byte spare whose
/// first twelve bytes hold a 3-byte Hamming code for each of the page's four 128-byte chunks. That code
/// detects any error and corrects any single-bit flip — exactly what guards a preserved save against silent
/// bit-rot or a bad transfer. DiscForge already reads the card's files; this checks the physical integrity
/// underneath them. Verification is read-only; repair writes a corrected copy and never touches the input.
/// The Hamming scheme is implemented from its definition and cross-checked against an independent reference.
/// </summary>
public static class Ps2CardEcc
{
    private const int PageData = 512;
    private const int PagePhysEcc = 528;
    private const int Chunk = 128;

    private static readonly byte[] ParityTable = new byte[256];
    private static readonly byte[] ColumnParityMasks = new byte[256];

    static Ps2CardEcc()
    {
        // Bit parity of each byte value.
        for (int b = 0; b < 256; b++) ParityTable[b] = (byte)(BitOperations.PopCount((uint)b) & 1);

        // Column-parity contribution of each byte: bit i is the parity of the byte
        // masked by the i-th column mask (the standard PS2/Hamming column layout).
        int[] cpmasks = { 0x55, 0x33, 0x0F, 0x00, 0xAA, 0xCC, 0xF0 };
        for (int b = 0; b < 256; b++)
        {
            int mask = 0;
            for (int i = 0; i < cpmasks.Length; i++)
                mask |= ParityTable[b & cpmasks[i]] << i;
            ColumnParityMasks[b] = (byte)mask;
        }
    }

    /// <summary>Compute the 3-byte Hamming code for one 128-byte chunk.</summary>
    public static void Calculate(ReadOnlySpan<byte> chunk, Span<byte> ecc3)
    {
        int columnParity = 0x77, lineParity0 = 0x7F, lineParity1 = 0x7F;
        for (int i = 0; i < Chunk; i++)
        {
            byte b = chunk[i];
            columnParity ^= ColumnParityMasks[b];
            if (ParityTable[b] != 0)
            {
                lineParity0 ^= ~i;
                lineParity1 ^= i;
            }
        }
        ecc3[0] = (byte)columnParity;
        ecc3[1] = (byte)(lineParity0 & 0x7F);
        ecc3[2] = (byte)(lineParity1 & 0x7F);
    }

    /// <summary>
    /// Check one 128-byte chunk against its stored 3-byte code, correcting a single-bit error in place
    /// (in the data, or in the code itself). Returns whether it was clean, corrected, or uncorrectable.
    /// </summary>
    public static Ps2EccStatus Check(Span<byte> chunk, Span<byte> ecc3)
    {
        Span<byte> computed = stackalloc byte[3];
        Calculate(chunk, computed);
        if (computed[0] == ecc3[0] && computed[1] == ecc3[1] && computed[2] == ecc3[2])
            return Ps2EccStatus.Clean;

        int cpDiff = (computed[0] ^ ecc3[0]) & 0x77;
        int lp0Diff = (computed[1] ^ ecc3[1]) & 0x7F;
        int lp1Diff = (computed[2] ^ ecc3[2]) & 0x7F;
        int lpComp = lp0Diff ^ lp1Diff;
        int cpComp = (cpDiff >> 4) ^ (cpDiff & 0x07);

        if (lpComp == 0x7F && cpComp == 0x07)
        {
            // Single-bit error in the data: byte lp1Diff, bit (cpDiff >> 4).
            chunk[lp1Diff] ^= (byte)(1 << (cpDiff >> 4));
            return Ps2EccStatus.Corrected;
        }
        if ((cpDiff == 0 && lp0Diff == 0 && lp1Diff == 0) ||
            BitOperations.PopCount((uint)lpComp) + BitOperations.PopCount((uint)cpComp) == 1)
        {
            // Single-bit error in the stored code (or a stray unused bit): trust the data.
            ecc3[0] = computed[0]; ecc3[1] = computed[1]; ecc3[2] = computed[2];
            return Ps2EccStatus.Corrected;
        }
        return Ps2EccStatus.Failed;   // two or more bit errors — uncorrectable
    }

    /// <summary>Verify the whole card (read-only).</summary>
    public static Ps2EccReport Verify(byte[] card) => Run(card, repair: false).Report;

    /// <summary>Verify and repair the whole card; returns the report and a corrected copy.</summary>
    public static (Ps2EccReport Report, byte[] Repaired) Repair(byte[] card)
    {
        var (report, fixedCard) = Run(card, repair: true);
        return (report, fixedCard);
    }

    private static (Ps2EccReport Report, byte[] Card) Run(byte[] card, bool repair)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!Ps2MemoryCard.IsPs2MemoryCard(card))
            throw new Ps2McFormatException("Missing the \"Sony PS2 Memory Card Format\" signature.");

        int pageLen = BinaryPrimitives.ReadUInt16LittleEndian(card.AsSpan(0x28));
        int pagesPerCluster = BinaryPrimitives.ReadUInt16LittleEndian(card.AsSpan(0x2A));
        uint clustersPerCard = BinaryPrimitives.ReadUInt32LittleEndian(card.AsSpan(0x30));
        long totalPages = (long)clustersPerCard * pagesPerCluster;

        bool hasEcc = pageLen == PageData && totalPages > 0 && card.LongLength >= totalPages * PagePhysEcc;
        if (!hasEcc)
            return (new Ps2EccReport
            {
                HasEcc = false, TotalPages = 0, CleanPages = 0, CorrectedPages = 0, FailedPages = 0,
                Findings = Array.Empty<Ps2EccPageFinding>(),
            }, card);

        var work = repair ? (byte[])card.Clone() : card;
        var findings = new List<Ps2EccPageFinding>();
        int clean = 0, corrected = 0, failed = 0;

        // Scratch buffers reused across every chunk so a read-only Verify never mutates the input.
        Span<byte> chunk = stackalloc byte[Chunk];
        Span<byte> ecc = stackalloc byte[3];

        for (int p = 0; p < totalPages; p++)
        {
            int baseOff = p * PagePhysEcc;
            if (baseOff + PagePhysEcc > work.Length) break;
            var pageStatus = Ps2EccStatus.Clean;

            for (int c = 0; c < 4; c++)
            {
                work.AsSpan(baseOff + c * Chunk, Chunk).CopyTo(chunk);
                work.AsSpan(baseOff + PageData + c * 3, 3).CopyTo(ecc);

                var st = Check(chunk, ecc);
                if (st == Ps2EccStatus.Corrected)
                {
                    findings.Add(new Ps2EccPageFinding(p, c, st, "single-bit error corrected"));
                    if (pageStatus != Ps2EccStatus.Failed) pageStatus = Ps2EccStatus.Corrected;
                    if (repair)
                    {
                        chunk.CopyTo(work.AsSpan(baseOff + c * Chunk, Chunk));
                        ecc.CopyTo(work.AsSpan(baseOff + PageData + c * 3, 3));
                    }
                }
                else if (st == Ps2EccStatus.Failed)
                {
                    findings.Add(new Ps2EccPageFinding(p, c, st, "2+ bit errors — uncorrectable"));
                    pageStatus = Ps2EccStatus.Failed;
                }
            }

            if (pageStatus == Ps2EccStatus.Clean) clean++;
            else if (pageStatus == Ps2EccStatus.Corrected) corrected++;
            else failed++;
        }

        var report = new Ps2EccReport
        {
            HasEcc = true, TotalPages = (int)totalPages,
            CleanPages = clean, CorrectedPages = corrected, FailedPages = failed,
            Findings = findings,
        };
        return (report, work);
    }
}
