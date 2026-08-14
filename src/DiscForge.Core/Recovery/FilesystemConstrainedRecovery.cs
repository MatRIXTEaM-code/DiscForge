// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Recovery;

/// <summary>What the filesystem says a sector is.</summary>
public enum FsRole
{
    /// <summary>Outside any known structure — role could not be determined.</summary>
    Unknown,
    /// <summary>The 16-sector system area before the volume descriptors.</summary>
    System,
    /// <summary>Filesystem metadata: volume descriptors, path tables, directory extents.</summary>
    Metadata,
    /// <summary>A sector of a file's content.</summary>
    FileData,
    /// <summary>Unallocated space between/after files.</summary>
    FreeSpace,
}

/// <summary>How an erased sector fared under filesystem-constrained recovery.</summary>
public enum FcrOutcome
{
    /// <summary>Reconstructed under the disc's observed fill convention (self-validated).</summary>
    Recovered,
    /// <summary>Not reconstructed, but identified — what it is, and (for file data) which file and byte range.</summary>
    Bounded,
    /// <summary>The filesystem said nothing useful about it.</summary>
    Unknown,
}

/// <summary>One erased sector's disposition.</summary>
public sealed record FcrFinding(long Sector, FsRole Role, FcrOutcome Outcome, string Detail);

public sealed record FcrResult
{
    public required byte[] Image { get; init; }
    public required IReadOnlyList<FcrFinding> Findings { get; init; }
    public int Recovered => Findings.Count(f => f.Outcome == FcrOutcome.Recovered);
    public int Bounded => Findings.Count(f => f.Outcome == FcrOutcome.Bounded);
    public int Unresolved => Findings.Count(f => f.Outcome == FcrOutcome.Unknown);

    public string Summary =>
        $"{Findings.Count:N0} erased sector(s): {Recovered:N0} reconstructed (free-space fill convention), " +
        $"{Bounded:N0} identified but not reconstructable, {Unresolved:N0} unclassified.";
}

/// <summary>
/// fs-recover — use the FILESYSTEM to make sense of erased/unreadable sectors, and reconstruct the ones it
/// safely can. A raw multi-copy merge (C2/ECC) works on the bytes alone; this works on what the bytes MEAN.
/// It classifies every sector from the ISO 9660 structure — system area, metadata (descriptors, path tables,
/// directories), file content, or free space — then, for each erased sector: reconstructs only the free space
/// that is PROVABLY not file content — sectors at or beyond the PVD Volume Space Size, i.e. padding appended
/// after the declared volume — and only under the disc's own observed fill convention (validated against the
/// surviving free sectors, declined if they are not uniform). A gap WITHIN the volume — which could be a
/// secondary-namespace directory, an unlisted extent, or the tail of a partially-enumerated damaged volume —
/// is left Unknown, never zeroed, so a sector that is actually content can never be silently overwritten;
/// BOUNDS file content by naming the file and the exact byte range lost (so a targeted re-read or another copy
/// can finish it); and BOUNDS metadata as such. Nothing about file data is ever guessed. Read-only analysis
/// plus a conservative, self-validated reconstruction; it defeats no protection.
/// </summary>
public static class FilesystemConstrainedRecovery
{
    public const int SectorSize = 2048;

    /// <summary>Recover erased sectors of <paramref name="image"/> using a precomputed classification.</summary>
    public static FcrResult Recover(byte[] image, IReadOnlyCollection<long> erasedSectors,
                                    FsRole[] roles, string[] labels, int sectorSize = SectorSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(erasedSectors);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(labels);
        if (sectorSize <= 0) throw new ArgumentException("Sector size must be positive.", nameof(sectorSize));
        if (image.Length % sectorSize != 0)
            throw new ArgumentException("Image length is not a whole number of sectors.");
        long total = image.Length / sectorSize;
        if (roles.Length != total || labels.Length != total)
            throw new ArgumentException("Roles/labels must have one entry per sector.");

        var erased = new HashSet<long>(erasedSectors);

        // Infer the fill convention of each reconstructable class from its SURVIVING sectors: a class is
        // reconstructable only if every surviving sector of it is the same single repeated byte.
        byte? freeFill = UniformFill(image, sectorSize, total, roles, erased, FsRole.FreeSpace, out int freeSurvivors);

        var outp = (byte[])image.Clone();
        var findings = new List<FcrFinding>();

        foreach (long s in erased.OrderBy(x => x))
        {
            if (s < 0 || s >= total) { findings.Add(new FcrFinding(s, FsRole.Unknown, FcrOutcome.Unknown, "outside the image")); continue; }
            var role = roles[s];
            switch (role)
            {
                case FsRole.FreeSpace when freeFill is { } fv:
                    outp.AsSpan((int)(s * sectorSize), sectorSize).Fill(fv);
                    findings.Add(new FcrFinding(s, role, FcrOutcome.Recovered,
                        $"free space — reconstructed as 0x{fv:X2} (convention validated over {freeSurvivors} surviving free sector(s))"));
                    break;
                case FsRole.FreeSpace:
                    findings.Add(new FcrFinding(s, role, FcrOutcome.Bounded,
                        freeSurvivors == 0 ? "free space — no surviving free sector to validate a fill; not reconstructed"
                                           : "free space — surviving free sectors are not a uniform fill; not reconstructed"));
                    break;
                case FsRole.FileData:
                    findings.Add(new FcrFinding(s, role, FcrOutcome.Bounded, labels[s]));
                    break;
                case FsRole.Metadata:
                    findings.Add(new FcrFinding(s, role, FcrOutcome.Bounded,
                        string.IsNullOrEmpty(labels[s]) ? "filesystem metadata" : labels[s]));
                    break;
                case FsRole.System:
                    findings.Add(new FcrFinding(s, role, FcrOutcome.Bounded, "system area (pre-descriptor)"));
                    break;
                default:
                    findings.Add(new FcrFinding(s, role, FcrOutcome.Unknown, "not covered by the filesystem"));
                    break;
            }
        }

        return new FcrResult { Image = outp, Findings = findings };
    }

    /// <summary>Recover erased sectors of an ISO 9660 image, classifying it internally.</summary>
    public static FcrResult RecoverIso(byte[] image, IReadOnlyCollection<long> erasedSectors, int sectorSize = SectorSize)
    {
        var (roles, labels) = BuildIsoMap(image, sectorSize);
        return Recover(image, erasedSectors, roles, labels, sectorSize);
    }

    /// <summary>Classify every sector of an ISO 9660 image by filesystem role.</summary>
    public static (FsRole[] Roles, string[] Labels) BuildIsoMap(byte[] image, int sectorSize = SectorSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length % sectorSize != 0)
            throw new ArgumentException("Image length is not a whole number of sectors.");
        long total = image.Length / sectorSize;
        var roles = new FsRole[total];
        var labels = new string[total];
        Array.Fill(labels, "");

        for (long s = 0; s < Math.Min(16, total); s++) roles[s] = FsRole.System;
        for (long s = 16; s < total; s++) roles[s] = FsRole.FreeSpace;   // default; overlaid below

        // The metadata block: sector 16 up to the first allocated data. Covers the volume descriptors and
        // the path tables, which sit before the first file/directory extent on a mastered disc.
        long pvd = 16;
        uint rootExtent = 0, rootSize = 0, volumeSpaceSize = 0;
        if ((pvd + 1) * sectorSize <= image.Length)
        {
            var d = image.AsSpan((int)(pvd * sectorSize), sectorSize);
            // PVD "Volume Space Size" (logical blocks in the volume) — bytes 80..83 LE. Sectors at or
            // beyond it are OUTSIDE the filesystem entirely, so they cannot be file content.
            volumeSpaceSize = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(80, 4));
            rootExtent = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(156 + 2, 4));
            rootSize = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(156 + 10, 4));
        }

        IsoDirectory? iso = null;
        try { iso = IsoReader.Read(new MemoryStream(image, false)); } catch { /* classify what we can */ }

        long firstData = rootExtent > 16 ? rootExtent : total;
        if (iso is not null)
            foreach (var e in iso.Entries)
                if (e.Extent >= 16 && e.Extent < firstData) firstData = e.Extent;
        for (long s = 16; s < Math.Min(firstData, total); s++) Mark(roles, labels, s, FsRole.Metadata, "volume descriptors / path tables");

        // Root directory + every directory extent = metadata; every file extent = file data.
        MarkRange(roles, labels, rootExtent, Sectors(rootSize), total, FsRole.Metadata, "root directory");
        if (iso is not null)
            foreach (var e in iso.Entries)
            {
                if (e.Extent < 16) continue;
                if (e.IsDirectory)
                    MarkRange(roles, labels, e.Extent, e.SectorCount, total, FsRole.Metadata, $"directory {e.Path}");
                else
                    for (uint k = 0; k < e.SectorCount; k++)
                    {
                        long s = e.Extent + k;
                        if (s < 0 || s >= total) break;
                        long off = (long)k * sectorSize;
                        long end = Math.Min(off + sectorSize, e.Size);
                        Mark(roles, labels, s, FsRole.FileData, $"{e.Path} bytes {off:N0}..{end:N0}");
                    }
            }

        // Reconstruction is limited to sectors that are PROVABLY not file content: those at or beyond
        // the PVD Volume Space Size (image padding appended after the declared volume). Anything WITHIN
        // the volume that the classifier didn't place — a mid-image gap, or the tail of the volume the
        // reader may not have fully enumerated (a damaged disc, a secondary namespace, an unlisted
        // extent) — is left Unknown, never zeroed and never used to infer the fill. This is what keeps a
        // sector that is actually content from being silently overwritten, per "provably correct or declined".
        // A missing/implausible Volume Space Size falls back to `total`, so nothing is reconstructable.
        long volEnd = volumeSpaceSize is > 0 && volumeSpaceSize <= total ? volumeSpaceSize : total;
        for (long s = 16; s < total; s++)
            if (roles[s] == FsRole.FreeSpace && s < volEnd)
            {
                roles[s] = FsRole.Unknown;
                labels[s] = "unclassified gap within the volume (possible unlisted metadata/data — not reconstructed)";
            }

        return (roles, labels);
    }

    public static string Render(FcrResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder(r.Summary);
        foreach (var f in r.Findings.Take(30))
            sb.Append($"\n  sector {f.Sector,8}  [{f.Outcome}] {f.Role}: {f.Detail}");
        if (r.Findings.Count > 30) sb.Append($"\n  … and {r.Findings.Count - 30:N0} more");
        return sb.ToString();
    }

    // ---- helpers -----------------------------------------------------------

    private static byte? UniformFill(byte[] image, int sectorSize, long total, FsRole[] roles,
                                     HashSet<long> erased, FsRole role, out int survivors)
    {
        survivors = 0;
        byte? fill = null;
        for (long s = 0; s < total; s++)
        {
            if (roles[s] != role || erased.Contains(s)) continue;
            var sector = image.AsSpan((int)(s * sectorSize), sectorSize);
            byte v = sector[0];
            for (int i = 1; i < sector.Length; i++) if (sector[i] != v) return null;   // not a uniform sector
            if (fill is null) fill = v;
            else if (fill.Value != v) return null;                                       // survivors disagree
            survivors++;
        }
        return survivors > 0 ? fill : null;
    }

    private static void Mark(FsRole[] roles, string[] labels, long s, FsRole role, string label)
    {
        if (s < 0 || s >= roles.Length) return;
        roles[s] = role;
        labels[s] = label;
    }

    private static void MarkRange(FsRole[] roles, string[] labels, long start, long count, long total, FsRole role, string label)
    {
        for (long s = start; s < start + count && s < total; s++)
            if (s >= 16) Mark(roles, labels, s, role, label);
    }

    private static long Sectors(long bytes) => (bytes + SectorSize - 1) / SectorSize;
}
