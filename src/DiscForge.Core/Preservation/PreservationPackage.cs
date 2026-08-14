// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscForge.Core.Files;
using DiscForge.Core.Forensics;

namespace DiscForge.Core.Preservation;

/// <summary>One file in a preservation set, with its size and full hash set.</summary>
public sealed class PreservationEntry
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public string Crc32 { get; set; } = "";
    public string Md5 { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

/// <summary>One independent dump of the same disc, recorded to corroborate the set.
/// A dump is identified by its per-track CRC-32 list (and optionally the whole-image
/// CRC-32), which are container-independent — two drives that read the same disc
/// produce the same values regardless of the file format they were written to.</summary>
public sealed class DumpAttestation
{
    /// <summary>The drive that produced this dump, e.g. "LITE-ON DVDRW SHW-160P6S (P901)".</summary>
    public string? Drive { get; set; }
    /// <summary>How it was read, e.g. "raw CD read, jitter-corrected".</summary>
    public string? Method { get; set; }
    public string? CapturedUtc { get; set; }
    /// <summary>Whole-image (concatenated track data) CRC-32, if known.</summary>
    public string? ImageCrc32 { get; set; }
    /// <summary>Per-track CRC-32, in track order.</summary>
    public List<string> TrackCrc32 { get; set; } = new();

    /// <summary>How agreement was judged: "crc" (identical per-track CRCs) or "genome"
    /// (offset-invariant identity — the same disc even when a read-offset difference makes
    /// the raw CRCs differ).</summary>
    public string Basis { get; set; } = "crc";
    /// <summary>Genome layout hash (genome basis).</summary>
    public string? LayoutHash { get; set; }
    /// <summary>Genome data hash (genome basis).</summary>
    public string? DataHash { get; set; }
    /// <summary>Audio-envelope similarity to the reference at best alignment, 0–1 (genome basis).</summary>
    public double? AudioSimilarity { get; set; }
    /// <summary>Envelope shift (sectors) at that best alignment — the drives' read-offset gap (genome basis).</summary>
    public int? OffsetShift { get; set; }

    /// <summary>Set when this attestation matches the provenance reference.</summary>
    public bool Agrees { get; set; }
}

/// <summary>Cross-source verification: the record that a dump was confirmed by more
/// than one independent read (a second drive, a second copy), which is the strongest
/// evidence a dump is faithful short of a published DAT match.</summary>
public sealed class DumpProvenance
{
    public string Kind { get; set; } = "cross-source-verification";
    /// <summary>The reference per-track CRC-32 every attestation is checked against
    /// (established by the first attestation added).</summary>
    public List<string> ReferenceTrackCrc32 { get; set; } = new();
    public string? ReferenceImageCrc32 { get; set; }
    public List<DumpAttestation> Attestations { get; set; } = new();

    /// <summary>How many independent sources agree with the reference.</summary>
    [JsonIgnore] public int IndependentAgreements => Attestations.Count(a => a.Agrees);
    /// <summary>At least two independent sources, and every one of them agrees.</summary>
    [JsonIgnore] public bool Corroborated => Attestations.Count >= 2 && Attestations.All(a => a.Agrees);
}

/// <summary>The disc's copy-protection verdict, recorded as provenance — a detection result carried with
/// the dump so a reader knows what was found and how strongly, never a means to reproduce or defeat it.</summary>
public sealed class ProtectionRecord
{
    /// <summary>None / FilesystemOnly / PhysicalOnly / Corroborated.</summary>
    public string Standing { get; set; } = "";
    /// <summary>The protection scheme name(s) identified (e.g. "SafeDisc", "LibCrypt").</summary>
    public List<string> Schemes { get; set; } = new();
    /// <summary>Whether a physical on-disc signature backs the filesystem marks.</summary>
    public bool PhysicalSignature { get; set; }
    /// <summary>The evidence lines behind the verdict.</summary>
    public List<string> Evidence { get; set; } = new();
    /// <summary>Preservation guidance (e.g. "preserve the subchannel verbatim").</summary>
    public string? Guidance { get; set; }
}

/// <summary>
/// A self-describing manifest for a preservation set — the image, its cue, the
/// subchannel, the dump log, whatever the dump comprises — recording each file's
/// size and CRC-32 / MD5 / SHA-1 / SHA-256, plus optional descriptive provenance
/// (title, platform, notes, when it was made), an optional <see cref="Provenance"/>
/// record of independent cross-source verification, an optional <see cref="Protection"/>
/// detection verdict, and a <see cref="Digest"/> over the whole manifest so the
/// manifest itself is tamper-evident.
/// </summary>
public sealed class PreservationManifest
{
    public string Schema { get; set; } = PreservationPackage.SchemaId;
    public string Generator { get; set; } = "";
    public string? Title { get; set; }
    public string? Platform { get; set; }
    public string? Notes { get; set; }
    public string? CreatedUtc { get; set; }
    public List<PreservationEntry> Entries { get; set; } = new();

    /// <summary>Independent cross-source verification (e.g. two drives agreeing).</summary>
    public DumpProvenance? Provenance { get; set; }

    /// <summary>The cross-checked copy-protection detection verdict for the dump (detection only).</summary>
    public ProtectionRecord? Protection { get; set; }

    /// <summary>SHA-256 over the canonical manifest (every field above); lets a
    /// reader confirm the manifest hasn't been altered before trusting its hashes.</summary>
    public string? Digest { get; set; }
}

/// <summary>Per-file verdict when a set is checked against its manifest.</summary>
public sealed record PreservationEntryVerdict(string Path, bool Found, bool Match, string? Detail);

/// <summary>The result of verifying a preservation set on disk.</summary>
public sealed record PreservationVerifyResult(bool ManifestIntact, IReadOnlyList<PreservationEntryVerdict> Entries)
{
    public int Ok => Entries.Count(e => e.Match);
    public int Failed => Entries.Count(e => !e.Match);
    public int Missing => Entries.Count(e => !e.Found);

    /// <summary>Everything is present, every hash matches, and the manifest itself
    /// is intact — the set is a verified, faithful copy of what was recorded.</summary>
    public bool AllGood => ManifestIntact && Entries.All(e => e.Match);
}

/// <summary>
/// Builds and verifies <see cref="PreservationManifest"/>s — DiscForge's answer to
/// "here's a BIN/CUE, trust me". A preservation set carrying one of these can be
/// checked years later, on any machine, to prove it is byte-for-byte what was
/// dumped: every file's hashes are re-computed and compared, and the manifest's own
/// digest confirms the record wasn't edited. Pure integrity/provenance — it proves
/// a faithful copy, and defeats nothing.
/// </summary>
public static class PreservationPackage
{
    public const string SchemaId = "discforge-preservation/1";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Build a manifest for a set of files. Paths are stored by file name;
    /// verification resolves them relative to the manifest's own folder.</summary>
    public static PreservationManifest Build(
        IEnumerable<string> files, string generator,
        string? title = null, string? platform = null, string? notes = null, string? createdUtc = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var m = new PreservationManifest
        {
            Schema = SchemaId,
            Generator = generator ?? "",
            Title = title, Platform = platform, Notes = notes, CreatedUtc = createdUtc,
        };
        foreach (var f in files)
        {
            var cs = ImageChecksums.ComputeFile(f);
            m.Entries.Add(new PreservationEntry
            {
                Path = System.IO.Path.GetFileName(f),
                Length = cs.Length,
                Crc32 = cs.Crc32.ToLowerInvariant(),
                Md5 = cs.Md5.ToLowerInvariant(),
                Sha1 = cs.Sha1.ToLowerInvariant(),
                Sha256 = cs.Sha256.ToLowerInvariant(),
            });
        }
        m.Digest = ComputeDigest(m);
        return m;
    }

    /// <summary>
    /// Record an independent dump of the same disc as corroborating provenance, and
    /// refresh the manifest's digest so the addition stays tamper-evident. The first
    /// attestation establishes the reference per-track CRC-32; each one after it is
    /// compared to that reference and marked <see cref="DumpAttestation.Agrees"/>.
    /// Returns whether this attestation agrees with the reference.
    /// </summary>
    public static bool AddAttestation(PreservationManifest m, DumpAttestation attestation)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(attestation);

        m.Provenance ??= new DumpProvenance();
        var p = m.Provenance;

        attestation.TrackCrc32 = attestation.TrackCrc32.Select(NormCrc).Where(s => s.Length > 0).ToList();
        attestation.ImageCrc32 = NormOrNull(attestation.ImageCrc32);

        if (p.ReferenceTrackCrc32.Count == 0 && attestation.TrackCrc32.Count > 0)
        {
            p.ReferenceTrackCrc32 = new List<string>(attestation.TrackCrc32);
            p.ReferenceImageCrc32 ??= attestation.ImageCrc32;
        }

        attestation.Agrees =
            attestation.TrackCrc32.Count > 0
            && attestation.TrackCrc32.SequenceEqual(p.ReferenceTrackCrc32)
            && (p.ReferenceImageCrc32 is null || attestation.ImageCrc32 is null
                || string.Equals(p.ReferenceImageCrc32, attestation.ImageCrc32, StringComparison.OrdinalIgnoreCase));

        p.Attestations.Add(attestation);
        m.Digest = ComputeDigest(m);
        return attestation.Agrees;
    }

    /// <summary>
    /// Record cross-source verification by <b>genome</b> rather than CRC — for the case two
    /// drives read the same disc but at different CD-DA read offsets, so their raw CRCs differ
    /// even though the dump is faithful (a PlayStation disc with audio is the classic example).
    /// The offset-invariant genome settles it: identical layout and addressed data, and an audio
    /// envelope that matches under a small shift. Records the base dump and the other dump as two
    /// attestations and refreshes the digest. Returns the genome comparison.
    /// </summary>
    public static GenomeMatch AddGenomeCorroboration(
        PreservationManifest m,
        string? baseDrive, GenomeFingerprint baseGenome,
        string? otherDrive, GenomeFingerprint otherGenome,
        double audioThreshold = 0.97)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(baseGenome);
        ArgumentNullException.ThrowIfNull(otherGenome);

        var match = DiscGenome.Compare(baseGenome, otherGenome, audioThreshold);
        m.Provenance ??= new DumpProvenance { Kind = "genome-cross-source-verification" };
        var p = m.Provenance;

        p.Attestations.Add(new DumpAttestation
        {
            Drive = baseDrive,
            Basis = "genome",
            LayoutHash = baseGenome.LayoutHash,
            DataHash = baseGenome.DataHash,
            Agrees = true,   // the reference agrees with itself
        });
        p.Attestations.Add(new DumpAttestation
        {
            Drive = otherDrive,
            Basis = "genome",
            LayoutHash = otherGenome.LayoutHash,
            DataHash = otherGenome.DataHash,
            AudioSimilarity = match.AudioSimilarity,
            OffsetShift = match.BestShift,
            Agrees = match.SameDisc,
        });

        m.Digest = ComputeDigest(m);
        return match;
    }

    /// <summary>
    /// Record the disc's cross-checked protection verdict as provenance and refresh the digest so the
    /// addition stays tamper-evident. This is a detection result — what protection was found and how
    /// strongly corroborated — carried with the dump; it reproduces and defeats nothing.
    /// </summary>
    public static void SetProtection(PreservationManifest m, DiscForge.Core.Forensics.FusedProtection fused)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(fused);
        m.Protection = new ProtectionRecord
        {
            Standing = fused.Standing.ToString(),
            Schemes = fused.Schemes.ToList(),
            PhysicalSignature = fused.PhysicalSignature,
            Evidence = fused.Evidence.ToList(),
            Guidance = fused.Guidance,
        };
        m.Digest = ComputeDigest(m);
    }

    private static string NormCrc(string? h)
    {
        if (string.IsNullOrWhiteSpace(h)) return "";
        string s = h.Trim().ToLowerInvariant();
        if (s.StartsWith("0x")) s = s[2..];
        return s;
    }

    private static string? NormOrNull(string? h)
    {
        var s = NormCrc(h);
        return s.Length == 0 ? null : s;
    }

    public static string ToJson(PreservationManifest m) => JsonSerializer.Serialize(m, Pretty);

    public static PreservationManifest FromJson(string json)
        => JsonSerializer.Deserialize<PreservationManifest>(json, Pretty)
           ?? throw new ArgumentException("Empty or invalid preservation manifest.");

    /// <summary>SHA-256 of the canonical manifest with the digest field itself
    /// excluded — so it covers everything the manifest records but not itself.</summary>
    public static string ComputeDigest(PreservationManifest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        string? saved = m.Digest;
        m.Digest = null;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, Canonical));
            return System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally { m.Digest = saved; }
    }

    /// <summary>True when the manifest carries a digest that matches its content.</summary>
    public static bool DigestValid(PreservationManifest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m.Digest is { } d && string.Equals(d, ComputeDigest(m), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Re-hash every file the manifest lists (resolved under
    /// <paramref name="baseDir"/>) and report whether the set is intact.</summary>
    public static PreservationVerifyResult Verify(PreservationManifest m, string baseDir)
    {
        ArgumentNullException.ThrowIfNull(m);
        var verdicts = new List<PreservationEntryVerdict>();
        foreach (var e in m.Entries)
        {
            string path = System.IO.Path.Combine(baseDir, e.Path);
            if (!File.Exists(path))
            {
                verdicts.Add(new PreservationEntryVerdict(e.Path, Found: false, Match: false, "file missing"));
                continue;
            }
            var cs = ImageChecksums.ComputeFile(path);
            bool match = cs.Length == e.Length
                         && string.Equals(cs.Sha256, e.Sha256, StringComparison.OrdinalIgnoreCase);
            verdicts.Add(new PreservationEntryVerdict(
                e.Path, Found: true, Match: match, match ? null : "hash mismatch"));
        }
        return new PreservationVerifyResult(DigestValid(m), verdicts);
    }
}
