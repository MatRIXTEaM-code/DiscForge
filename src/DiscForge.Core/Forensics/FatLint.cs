// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Fat;

namespace DiscForge.Core.Forensics;

/// <summary>The result of linting a FAT volume's structure.</summary>
public sealed record FatLintReport
{
    public required bool IsFat { get; init; }
    public required IReadOnlyList<LintFinding> Findings { get; init; }
    public FatType Type { get; init; }
    public int Errors => Findings.Count(f => f.Severity == LintSeverity.Error);
    public int Warnings => Findings.Count(f => f.Severity == LintSeverity.Warning);
    public bool Ok => Errors == 0;

    public string Summary()
    {
        if (!IsFat) return "FAT: not a FAT volume.";
        if (Findings.Count == 0) return $"FAT{TypeNum()}: clean — no structural issues.";
        return $"FAT{TypeNum()}: {Errors} error(s), {Warnings} warning(s).";
    }

    private string TypeNum() => Type switch { FatType.Fat12 => "12", FatType.Fat16 => "16", _ => "32" };
}

/// <summary>
/// fat-lint — a read-only structural checker for FAT12/16/32 volumes (floppy images, El Torito hard-disk
/// boot images, the FAT partition of a hybrid disc, memory-card FAT dumps). Where <see cref="FatReader"/>
/// walks the tree, this audits the plumbing beneath it: the BPB geometry and boot signature, that the
/// redundant FAT copies agree, that every file and directory's cluster chain is well-formed (no reference
/// to a free/bad/out-of-range cluster, no loop), that no two chains cross-link the same cluster, and that
/// no allocated cluster is orphaned (lost). It is the fsck-style integrity pass a preservation dump needs;
/// it reads and reports, and repairs nothing.
/// </summary>
public static class FatLint
{
    public static FatLintReport Check(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var f = new List<LintFinding>();

        if (!FatReader.IsFat(image))
            return new FatLintReport { IsFat = false, Findings = f };

        // ---- BPB geometry ----------------------------------------------------
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x0B));
        int spc = image[0x0D];
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x0E));
        int numFats = image[0x10];
        int rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x11));
        long totSec = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x13));
        if (totSec == 0) totSec = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x20));
        long fatSz = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x16));
        if (fatSz == 0) fatSz = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x24));

        if (bps is not (512 or 1024 or 2048 or 4096))
            f.Add(new(LintSeverity.Error, "BPB", $"bytes-per-sector is {bps}, expected 512/1024/2048/4096."));
        if (spc == 0 || (spc & (spc - 1)) != 0)
            f.Add(new(LintSeverity.Error, "BPB", $"sectors-per-cluster is {spc}, must be a power of two."));
        if (numFats < 1)
            f.Add(new(LintSeverity.Error, "BPB", "number of FATs is 0."));
        if (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x1FE)) != 0xAA55)
            f.Add(new(LintSeverity.Warning, "BPB", "boot signature 0x55AA is missing."));
        if (bps == 0 || spc == 0 || numFats < 1)
            return new FatLintReport { IsFat = true, Findings = f };

        int rootDirSectors = (rootEntries * 32 + bps - 1) / bps;
        long firstDataSector = reserved + (long)numFats * fatSz + rootDirSectors;
        long dataSectors = totSec - firstDataSector;
        long countOfClusters = dataSectors > 0 ? dataSectors / spc : 0;
        var type = countOfClusters < 4085 ? FatType.Fat12
                 : countOfClusters < 65525 ? FatType.Fat16 : FatType.Fat32;
        uint maxCluster = (uint)(countOfClusters + 1);   // clusters are numbered from 2
        long fatStart = (long)reserved * bps;
        long fatBytes = fatSz * bps;
        int clusterBytes = spc * bps;

        if (firstDataSector <= 0 || firstDataSector * bps > image.LongLength)
        {
            f.Add(new(LintSeverity.Error, "geometry", "the data region starts past the end of the image — the BPB is inconsistent."));
            return new FatLintReport { IsFat = true, Findings = f, Type = type };
        }

        // ---- redundant FAT copies must agree ---------------------------------
        for (int n = 1; n < numFats; n++)
        {
            long a = fatStart, b = fatStart + n * fatBytes;
            if (b + fatBytes > image.LongLength) { f.Add(new(LintSeverity.Warning, "FAT", $"FAT copy {n} lies past the end of the image.")); break; }
            if (!image.AsSpan((int)a, (int)fatBytes).SequenceEqual(image.AsSpan((int)b, (int)fatBytes)))
                f.Add(new(LintSeverity.Warning, "FAT", $"FAT copy {n} does not match FAT 0 — the copies have diverged."));
        }

        uint EocMin = type switch { FatType.Fat12 => 0xFF8, FatType.Fat16 => 0xFFF8, _ => 0x0FFFFFF8 };
        uint BadVal = type switch { FatType.Fat12 => 0xFF7, FatType.Fat16 => 0xFFF7, _ => 0x0FFFFFF7 };

        uint ReadFat(uint n)
        {
            if (type == FatType.Fat12)
            {
                long o = fatStart + n + (n / 2);
                if (o + 1 >= image.LongLength) return 0;
                int pair = image[o] | (image[o + 1] << 8);
                return (uint)((n & 1) == 1 ? pair >> 4 : pair & 0xFFF);
            }
            if (type == FatType.Fat16)
            {
                long o = fatStart + n * 2;
                return o + 1 < image.LongLength ? BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)o)) : 0u;
            }
            long o32 = fatStart + n * 4;
            return o32 + 3 < image.LongLength ? BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)o32)) & 0x0FFFFFFF : 0u;
        }

        // ---- walk every chain, detecting bad links, loops and cross-links ----
        FatVolume vol;
        try { vol = FatReader.Read(image); }
        catch (FatFormatException ex)
        {
            f.Add(new(LintSeverity.Error, "tree", $"the directory tree could not be read: {ex.Message}"));
            return new FatLintReport { IsFat = true, Findings = f, Type = type };
        }

        var owner = new Dictionary<uint, string>();
        foreach (var e in vol.Entries)
        {
            if (e.FirstCluster < 2) continue;   // empty file / root
            string who = e.Path.Length == 0 ? "/" : e.Path;
            uint c = e.FirstCluster;
            int steps = 0;
            long chainBytes = 0;
            var seen = new HashSet<uint>();
            while (true)
            {
                if (c < 2 || c > maxCluster)
                { f.Add(new(LintSeverity.Error, who, $"cluster chain references out-of-range cluster {c}.")); break; }
                if (!seen.Add(c))
                { f.Add(new(LintSeverity.Error, who, $"cluster chain loops at cluster {c}.")); break; }
                if (owner.TryGetValue(c, out var other) && other != who)
                { f.Add(new(LintSeverity.Error, who, $"cluster {c} is cross-linked with '{other}'.")); break; }
                owner[c] = who;
                chainBytes += clusterBytes;

                uint next = ReadFat(c);
                if (next == 0)
                { f.Add(new(LintSeverity.Error, who, $"cluster chain runs into a free cluster at {c} (truncated).")); break; }
                if (next == BadVal)
                { f.Add(new(LintSeverity.Error, who, $"cluster chain passes through a bad cluster ({c}).")); break; }
                if (next >= EocMin) break;   // proper end-of-chain
                if (++steps > countOfClusters + 2)
                { f.Add(new(LintSeverity.Error, who, "cluster chain does not terminate.")); break; }
                c = next;
            }

            if (!e.IsDirectory && e.Size > 0 && chainBytes > 0)
            {
                long expected = (e.Size + clusterBytes - 1) / clusterBytes * clusterBytes;
                if (chainBytes < expected)
                    f.Add(new(LintSeverity.Warning, who, $"chain holds {chainBytes:N0} bytes but the file is {e.Size:N0} — the chain is short."));
            }
        }

        // ---- lost clusters: allocated but owned by nobody ---------------------
        int lost = 0;
        for (uint c = 2; c <= maxCluster; c++)
        {
            uint v = ReadFat(c);
            if (v != 0 && v != BadVal && !owner.ContainsKey(c)) lost++;
        }
        if (lost > 0)
            f.Add(new(LintSeverity.Warning, "FAT", $"{lost:N0} allocated cluster(s) are not referenced by any file or directory (lost clusters)."));

        return new FatLintReport { IsFat = true, Findings = f, Type = type };
    }

    public static string Render(FatLintReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var x in r.Findings)
            sb.AppendLine($"  [{x.Severity}] {x.Where}: {x.Message}");
        return sb.ToString().TrimEnd();
    }
}
