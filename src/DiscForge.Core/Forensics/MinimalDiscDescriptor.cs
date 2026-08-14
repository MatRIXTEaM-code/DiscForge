// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>One instruction in a disc's minimal descriptor.</summary>
public enum MddOpKind
{
    /// <summary>A run of sectors all filled with one byte value (padding, blanked regions).</summary>
    Fill,
    /// <summary>A brand-new unique sector — the only kind that costs real bytes.</summary>
    Unique,
    /// <summary>A sector byte-identical to an earlier unique one — a back-reference, not bytes.</summary>
    Duplicate,
}

/// <summary>One op of the descriptor. Fill uses <see cref="FillValue"/>+<see cref="SectorRun"/>;
/// Unique/Duplicate use <see cref="LiteralIndex"/> into the literal store (run = 1).</summary>
public sealed record MddOp(MddOpKind Kind, byte FillValue, long SectorRun, int LiteralIndex);

/// <summary>The irreducible-content report plus the descriptor program that reproduces the image.</summary>
public sealed record MddResult
{
    public required int SectorSize { get; init; }
    public required long TotalSectors { get; init; }
    public long TotalBytes => TotalSectors * SectorSize;

    public required long UniqueSectors { get; init; }
    public required long DuplicateSectors { get; init; }
    public required long FillSectors { get; init; }
    /// <summary>Distinct fill values seen (e.g. 0x00 padding, 0xFF blank), most common first.</summary>
    public required IReadOnlyList<(byte Value, long Sectors)> FillBreakdown { get; init; }

    /// <summary>The irreducible content: bytes of genuinely unique sectors.</summary>
    public long UniqueBytes => UniqueSectors * SectorSize;
    /// <summary>Approximate size of the minimal descriptor: the unique bytes plus a small per-op table.</summary>
    public required long MinimalBytes { get; init; }
    /// <summary>Fraction of the image that is NOT irreducible (dedup + fill), 0..1.</summary>
    public double ReductionRatio => TotalBytes == 0 ? 0 : 1.0 - (double)MinimalBytes / TotalBytes;

    /// <summary>The descriptor program (Fill/Unique/Duplicate ops in disc order).</summary>
    public required IReadOnlyList<MddOp> Ops { get; init; }
    /// <summary>The unique sector bodies, indexed by <see cref="MddOp.LiteralIndex"/>.</summary>
    public required IReadOnlyList<byte[]> Literals { get; init; }

    public string Summary() =>
        $"{TotalSectors:N0} sector(s) × {SectorSize} B = {TotalBytes:N0} B → minimal {MinimalBytes:N0} B " +
        $"({ReductionRatio * 100:F1}% is fill/duplicate). " +
        $"{UniqueSectors:N0} unique, {DuplicateSectors:N0} duplicate, {FillSectors:N0} fill.";
}

/// <summary>
/// min-descriptor — the smallest complete, self-verifying description of a disc image. It factors the image
/// into three kinds of matter: constant-fill runs (padding/blanked regions, storable as a value + a count),
/// duplicate sectors (byte-identical to one already seen — a back-reference, not a copy), and the genuinely
/// UNIQUE sectors that are the disc's irreducible content. The result is both a report ("how much is actually
/// here, versus fill and repetition") and a descriptor program that reconstructs the image exactly — proven by
/// <see cref="Reconstruct"/> returning the original bytes. Unlike gzip this is structural and exact (no entropy
/// coding, no loss), so the "unique bytes" figure is an honest information floor for the format as dumped.
/// Read-only analysis; it changes nothing on disc.
/// </summary>
public static class MinimalDiscDescriptor
{
    private const int OpOverheadBytes = 10;   // rough table cost per op (kind + value/ref + run)

    public static MddResult Analyze(ReadOnlySpan<byte> image, int sectorSize = 2048)
    {
        if (sectorSize <= 0) throw new ArgumentException("Sector size must be positive.", nameof(sectorSize));
        if (image.Length % sectorSize != 0)
            throw new ArgumentException($"Image length {image.Length:N0} is not a whole number of {sectorSize}-byte sectors.");

        long totalSectors = image.Length / sectorSize;
        var ops = new List<MddOp>();
        var literals = new List<byte[]>();
        // hash → indices of literals with that hash (verify bytes on hit, so correctness never rests on the hash).
        var seen = new Dictionary<ulong, List<int>>();
        var fillCounts = new SortedDictionary<byte, long>();

        long unique = 0, duplicate = 0, fill = 0;

        for (long s = 0; s < totalSectors;)
        {
            var sector = image.Slice((int)(s * sectorSize), sectorSize);

            if (IsConstant(sector, out byte val))
            {
                // Coalesce consecutive fill sectors of the SAME value into one run.
                long run = 1;
                while (s + run < totalSectors)
                {
                    var next = image.Slice((int)((s + run) * sectorSize), sectorSize);
                    if (!IsConstant(next, out byte v2) || v2 != val) break;
                    run++;
                }
                ops.Add(new MddOp(MddOpKind.Fill, val, run, -1));
                fillCounts[val] = fillCounts.GetValueOrDefault(val) + run;
                fill += run;
                s += run;
                continue;
            }

            ulong h = Fnv1a(sector);
            int match = -1;
            if (seen.TryGetValue(h, out var candidates))
                foreach (int idx in candidates)
                    if (sector.SequenceEqual(literals[idx])) { match = idx; break; }

            if (match >= 0)
            {
                ops.Add(new MddOp(MddOpKind.Duplicate, 0, 1, match));
                duplicate++;
            }
            else
            {
                int idx = literals.Count;
                literals.Add(sector.ToArray());
                (seen.TryGetValue(h, out var list) ? list : seen[h] = new List<int>()).Add(idx);
                ops.Add(new MddOp(MddOpKind.Unique, 0, 1, idx));
                unique++;
            }
            s++;
        }

        long minimalBytes = unique * sectorSize + (long)ops.Count * OpOverheadBytes;
        var breakdown = fillCounts.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();

        return new MddResult
        {
            SectorSize = sectorSize,
            TotalSectors = totalSectors,
            UniqueSectors = unique,
            DuplicateSectors = duplicate,
            FillSectors = fill,
            FillBreakdown = breakdown,
            MinimalBytes = minimalBytes,
            Ops = ops,
            Literals = literals,
        };
    }

    /// <summary>Replay a descriptor back into the exact image it came from — the proof it is complete.</summary>
    public static byte[] Reconstruct(MddResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var outp = new byte[r.TotalBytes];
        long pos = 0;
        foreach (var op in r.Ops)
        {
            switch (op.Kind)
            {
                case MddOpKind.Fill:
                    long n = op.SectorRun * r.SectorSize;
                    outp.AsSpan((int)pos, (int)n).Fill(op.FillValue);
                    pos += n;
                    break;
                case MddOpKind.Unique:
                case MddOpKind.Duplicate:
                    r.Literals[op.LiteralIndex].CopyTo(outp, (int)pos);
                    pos += r.SectorSize;
                    break;
            }
        }
        return outp;
    }

    public static string Render(MddResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder(r.Summary());
        foreach (var (value, sectors) in r.FillBreakdown.Take(6))
            sb.Append($"\n  fill 0x{value:X2}: {sectors:N0} sector(s)");
        return sb.ToString();
    }

    private static bool IsConstant(ReadOnlySpan<byte> s, out byte value)
    {
        value = s[0];
        for (int i = 1; i < s.Length; i++) if (s[i] != value) return false;
        return true;
    }

    private static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        ulong h = 1469598103934665603UL;
        foreach (byte b in data) { h ^= b; h *= 1099511628211UL; }
        return h;
    }
}
