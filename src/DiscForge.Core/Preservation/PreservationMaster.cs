// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Files;
using DiscForge.Core.Forensics;
using DiscForge.Core.Identify;

namespace DiscForge.Core.Preservation;

/// <summary>Fixity for one member file of a preservation master: sizes, the four hashes, and the shift-tolerant Merkle root.</summary>
public sealed record MasterFileEntry
{
    public required string Name { get; init; }
    public required long Length { get; init; }
    public required string Crc32 { get; init; }
    public required string Md5 { get; init; }
    public required string Sha1 { get; init; }
    public required string Sha256 { get; init; }
    public required string MerkleRoot { get; init; }
}

/// <summary>The unreadable-sector account folded into a master: the counts, a few coalesced runs, and per-file
/// positions when the dump is a split image. Its presence is what stops the master calling a holed dump "complete".</summary>
public sealed record MasterBadSectors
{
    public required int Total { get; init; }
    /// <summary>Unreadable sectors that are genuine damage (not a boundary/pregap hole).</summary>
    public required int Damage { get; init; }
    public required int Boundary { get; init; }
    /// <summary>Coalesced runs (capped), each rendered as "start" or "start-end (×count)".</summary>
    public required IReadOnlyList<string> Runs { get; init; }
    public IReadOnlyList<TrackBadSectors>? ByTrack { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// The DiscForge Preservation Master (DPM) — one self-describing record fusing what were separate commands:
/// the identity of the disc, per-file fixity (CRC32/MD5/SHA-1/SHA-256 plus the FastCDC Merkle root), the
/// completeness certificate for a bin/cue, the clean-room protection profile, and the map of sectors that could
/// not be read. It is the single authoritative account of a dump, and the sidecar it serialises to is a superset
/// of the open metadata other suites emit.
/// </summary>
public sealed record PreservationMaster
{
    /// <summary>The on-disk format tag of the master sidecar.</summary>
    public string FormatVersion => "dpm/1";
    public required string PrimaryImage { get; init; }
    public required string Identity { get; init; }
    public required IReadOnlyList<MasterFileEntry> Files { get; init; }
    public string? CompletenessSummary { get; init; }
    public bool? Complete { get; init; }
    public ProtectionProfile? Protection { get; init; }
    /// <summary>The unreadable-sector map, when a <c>.badsectors.json</c> sidecar accompanied the dump. Null when
    /// none was present (which is NOT proof of a clean read — only proof that no map was recorded).</summary>
    public MasterBadSectors? BadSectors { get; init; }

    public string Summary()
    {
        string comp = Complete is null ? "" : Complete.Value ? ", complete" : ", INCOMPLETE";
        string prot = Protection is null ? ""
            : Protection.AnyProtection
                ? Protection.FullyPreserved ? ", protection preserved" : ", protection UNDER-CAPTURED"
                : ", no protection";
        string bad = BadSectors is null ? ""
            : BadSectors.Total == 0 ? ""
            : BadSectors.Damage > 0 ? $", {BadSectors.Total} unreadable sector(s)"
                                    : $", {BadSectors.Total} boundary hole(s)";
        return $"{Identity} — {Files.Count} file(s){comp}{prot}{bad}.";
    }
}

/// <summary>
/// preserve-master — build and verify the DPM. <c>Build</c> fuses the existing fixity, completeness and
/// protection machinery into one master for an image (or a bin/cue set); <c>VerifyFile</c> recomputes a
/// member's fixity so a stored master can be proven byte-for-byte later. Fixity and provenance only — it stores
/// what was dumped and proves things about it; it strips and defeats nothing.
/// </summary>
public static class PreservationMasterBuilder
{
    /// <summary>Build a master for an image or a .cue set. Protection profiling is best-effort (skipped if the image can't be opened as sectors).</summary>
    public static PreservationMaster Build(string imagePath)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        string dir = Path.GetDirectoryName(Path.GetFullPath(imagePath)) ?? ".";
        var members = MemberFiles(imagePath);

        var entries = new List<MasterFileEntry>();
        foreach (var rel in members)
        {
            string full = Path.IsPathRooted(rel) ? rel : Path.Combine(dir, rel);
            if (!File.Exists(full)) continue;
            entries.Add(EntryFor(Path.GetFileName(full), full));
        }
        if (entries.Count == 0)
            throw new FileNotFoundException($"No readable member files were found for '{imagePath}'.");

        // Identity from the primary image's leading bytes.
        string identity = "Unknown";
        try
        {
            using var fs = File.OpenRead(imagePath);
            var head = new byte[Math.Min(fs.Length, 65536)];
            fs.ReadExactly(head, 0, head.Length);
            var id = FormatIdentifier.Identify(head);
            identity = id.Recognised ? $"{id.Name} ({id.Category})" : "Unknown";
        }
        catch { /* identity is best-effort */ }

        // Completeness certificate for a cue.
        string? compSummary = null; bool? complete = null;
        if (Path.GetExtension(imagePath).Equals(".cue", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var c = DumpCompleteness.Check(imagePath);
                compSummary = DumpCompleteness.Render(c);
                complete = c.Complete;
            }
            catch { /* best-effort */ }
        }

        // Clean-room protection profile (best-effort).
        ProtectionProfile? protection = null;
        try
        {
            using var access = SectorAccess.Open(imagePath);
            protection = ProtectionProfiler.Build(access, Array.Empty<string>());
        }
        catch { /* not all images open as sectors */ }

        // Unreadable-sector map: a "<image>.badsectors.json" sidecar written at capture and carried through
        // conversion. When it reports genuine damage, the dump is NOT complete however clean the hashes look —
        // a zero-filled hole hashes fine, so completeness must come from this map, not the checksums.
        MasterBadSectors? badSectors = null;
        var sidecar = BadSectorMap.SidecarPath(imagePath);
        if (File.Exists(sidecar))
        {
            try
            {
                var map = BadSectorMap.Load(sidecar);
                badSectors = new MasterBadSectors
                {
                    Total = map.Count,
                    Damage = map.DamageCount,
                    Boundary = map.BoundaryCount,
                    Runs = map.Runs().Take(64).Select(r => r.ToString()).ToList(),
                    ByTrack = map.ByTrack,
                    Note = map.Note,
                };
                if (map.DamagePresent)
                {
                    complete = false;
                    string add = $"{map.DamageCount:N0} unreadable sector(s) in {map.Runs().Count:N0} run(s) — " +
                                 "the dump is INCOMPLETE; those sectors are zero-filled and hash as if they were data.";
                    compSummary = compSummary is null ? add : compSummary + "\n  · " + add;
                }
            }
            catch { /* a malformed sidecar must not sink the whole master */ }
        }

        return new PreservationMaster
        {
            PrimaryImage = Path.GetFileName(imagePath),
            Identity = identity,
            Files = entries,
            CompletenessSummary = compSummary,
            Complete = complete,
            Protection = protection,
            BadSectors = badSectors,
        };
    }

    /// <summary>Recompute a member file's fixity and compare it against the stored entry.</summary>
    public static (bool Ok, IReadOnlyList<string> Diffs) VerifyFile(MasterFileEntry expected, string baseDir)
    {
        ArgumentNullException.ThrowIfNull(expected);
        string full = Path.Combine(baseDir, expected.Name);
        var diffs = new List<string>();
        if (!File.Exists(full))
            return (false, new[] { $"{expected.Name}: missing" });

        var got = EntryFor(expected.Name, full);
        if (got.Length != expected.Length) diffs.Add($"{expected.Name}: length {got.Length} vs {expected.Length}");
        if (!Eq(got.Sha256, expected.Sha256)) diffs.Add($"{expected.Name}: SHA-256 mismatch");
        if (!Eq(got.Crc32, expected.Crc32)) diffs.Add($"{expected.Name}: CRC-32 mismatch");
        if (!Eq(got.MerkleRoot, expected.MerkleRoot)) diffs.Add($"{expected.Name}: Merkle root mismatch");
        return (diffs.Count == 0, diffs);
    }

    private static MasterFileEntry EntryFor(string name, string fullPath)
    {
        var sums = ImageChecksums.ComputeFile(fullPath);
        var manifest = ContentChunking.BuildManifestFromFile(fullPath);
        return new MasterFileEntry
        {
            Name = name,
            Length = sums.Length,
            Crc32 = sums.Crc32.ToLowerInvariant(),
            Md5 = sums.Md5.ToLowerInvariant(),
            Sha1 = sums.Sha1.ToLowerInvariant(),
            Sha256 = sums.Sha256.ToLowerInvariant(),
            MerkleRoot = manifest.RootHex,
        };
    }

    /// <summary>The member files of an image: for a cue, the cue itself plus each distinct track file; else the image alone.</summary>
    private static List<string> MemberFiles(string imagePath)
    {
        var list = new List<string>();
        if (Path.GetExtension(imagePath).Equals(".cue", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(Path.GetFileName(imagePath));
            try
            {
                var cue = CueSheet.Parse(File.ReadAllText(imagePath));
                foreach (var f in cue.Tracks.Select(t => t.File).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct())
                    list.Add(f);
            }
            catch { /* fall back to just the cue */ }
        }
        else
        {
            list.Add(Path.GetFileName(imagePath));
        }
        return list;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
