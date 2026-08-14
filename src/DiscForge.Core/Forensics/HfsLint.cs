// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Hfs;

namespace DiscForge.Core.Forensics;

/// <summary>The result of linting an HFS volume's structure.</summary>
public sealed record HfsLintReport
{
    public required bool IsHfs { get; init; }
    public required IReadOnlyList<LintFinding> Findings { get; init; }
    public string VolumeName { get; init; } = "";
    public int Errors => Findings.Count(f => f.Severity == LintSeverity.Error);
    public int Warnings => Findings.Count(f => f.Severity == LintSeverity.Warning);
    public bool Ok => Errors == 0;

    public string Summary() =>
        !IsHfs ? "HFS: not an HFS volume."
        : Findings.Count == 0 ? $"HFS \"{VolumeName}\": clean — no structural issues."
        : $"HFS \"{VolumeName}\": {Errors} error(s), {Warnings} warning(s).";
}

/// <summary>
/// hfs-lint — a read-only structural checker for classic Apple HFS volumes (the Mac side of the hybrid CDs
/// so much retro Mac software shipped on), the fourth of DiscForge's filesystem linters alongside ISO 9660,
/// UDF and FAT. It validates the Master Directory Block signature and geometry, walks the catalog B-tree the
/// way a driver does and reports if it cannot, cross-checks the volume's recorded file and directory counts
/// against the tree it actually holds, and confirms every file's data- and resource-fork extents lie inside
/// the volume's allocation area. It reads and reports, and repairs nothing.
/// </summary>
public static class HfsLint
{
    private const int Mdb = 0x400;

    public static HfsLintReport Check(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var f = new List<LintFinding>();

        if (!HfsReader.IsHfs(image))
            return new HfsLintReport { IsHfs = false, Findings = f };

        // ---- Master Directory Block geometry ---------------------------------
        int nmAlBlks = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 18));
        long alBlkSize = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(Mdb + 20));
        int alBlSt = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 28));   // in 512-byte sectors
        long mdbFileCount = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(Mdb + 84));
        long mdbDirCount = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(Mdb + 88));

        if (alBlkSize <= 0 || alBlkSize % 512 != 0)
            f.Add(new(LintSeverity.Error, "MDB", $"allocation block size {alBlkSize} is not a positive multiple of 512."));
        if (nmAlBlks <= 0)
            f.Add(new(LintSeverity.Error, "MDB", "the volume declares zero allocation blocks."));

        long volumeEnd = (long)alBlSt * 512 + (long)nmAlBlks * (alBlkSize <= 0 ? 0 : alBlkSize);
        if (alBlkSize > 0 && nmAlBlks > 0 && volumeEnd > image.LongLength)
            f.Add(new(LintSeverity.Warning, "MDB",
                $"the volume's allocation area ends at {volumeEnd:N0} bytes but the image is only {image.LongLength:N0} — truncated."));

        // ---- catalog B-tree walkability --------------------------------------
        HfsVolume vol;
        try
        {
            vol = HfsReader.Read(image);
        }
        catch (HfsFormatException ex)
        {
            f.Add(new(LintSeverity.Error, "catalog", $"the catalog B-tree could not be walked: {ex.Message}"));
            return new HfsLintReport { IsHfs = true, Findings = f };
        }

        // ---- recorded counts vs the tree actually present --------------------
        int files = vol.Files.Count(), dirs = vol.Directories.Count();
        if (mdbFileCount != files)
            f.Add(new(LintSeverity.Warning, "counts",
                $"the MDB records {mdbFileCount} file(s) but the catalog holds {files}."));
        if (mdbDirCount != dirs)
            f.Add(new(LintSeverity.Warning, "counts",
                $"the MDB records {mdbDirCount} director(y/ies) but the catalog holds {dirs}."));

        // ---- fork extents must lie inside the volume -------------------------
        long firstByte = (long)vol.AllocBlockStartSector * 512;
        long lastByte = alBlkSize > 0 ? firstByte + (long)nmAlBlks * alBlkSize : image.LongLength;
        foreach (var e in vol.Files)
        {
            CheckExtents(e.Path, "data", e.DataExtents, e.DataSize, vol, firstByte, lastByte, image.LongLength, f);
            CheckExtents(e.Path, "resource", e.ResourceExtents, e.ResourceSize, vol, firstByte, lastByte, image.LongLength, f);
        }

        return new HfsLintReport { IsHfs = true, Findings = f, VolumeName = vol.VolumeName };
    }

    private static void CheckExtents(string path, string fork, IReadOnlyList<HfsExtent> extents, long size,
                                     HfsVolume vol, long firstByte, long lastByte, long imageLen, List<LintFinding> f)
    {
        if (size <= 0) return;
        long covered = 0;
        foreach (var ex in extents)
        {
            if (ex.BlockCount == 0) continue;
            long start = firstByte + (long)ex.StartBlock * vol.AllocBlockSize;
            long end = start + (long)ex.BlockCount * vol.AllocBlockSize;
            if (start < firstByte || end > lastByte || end > imageLen)
                f.Add(new(LintSeverity.Error, path,
                    $"{fork}-fork extent (block {ex.StartBlock}+{ex.BlockCount}) lies outside the volume's allocation area."));
            covered += (long)ex.BlockCount * vol.AllocBlockSize;
        }
        if (covered > 0 && covered < size)
            f.Add(new(LintSeverity.Info, path,
                $"{fork} fork is {size:N0} bytes but its catalog extents cover only {covered:N0} — it is fragmented into an extents-overflow file."));
    }

    public static string Render(HfsLintReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var x in r.Findings)
            sb.AppendLine($"  [{x.Severity}] {x.Where}: {x.Message}");
        return sb.ToString().TrimEnd();
    }
}
