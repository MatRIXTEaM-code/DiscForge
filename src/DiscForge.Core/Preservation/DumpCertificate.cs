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

namespace DiscForge.Core.Preservation;

/// <summary>One extraction span as the certificate records it.</summary>
public sealed record CertifiedSpan(string Label, long StartLba, long EndLba, bool Audio, bool Boundary, string Grade);

/// <summary>
/// The Dump Certificate: a signed, machine-readable account of one dump event —
/// what was read (image identity, SHA-256, per-span layout and grades), what
/// read it (drive, firmware, settings, tool version), what the dump could not
/// prove (unreadable / boundary counts, audit grade), and a Merkle root over
/// the sectors. The root is the differentiator: with it, any single sector of
/// a 700 MB image can later be proven byte-identical to what the drive
/// delivered at dump time using a ~18-hash proof (<see cref="SectorMerkle"/>),
/// without rehashing or even possessing the rest of the file. Chain of custody
/// for preservation, in one JSON sidecar.
///
/// The signing follows <see cref="Recovery.MergeCertificate"/>'s house pattern:
/// ECDSA P-256 over a canonical newline-joined content string, public key
/// embedded, verification self-contained. Poetic justice note: this signer is
/// the licensing code's ECDSA, reborn under GPL with an honest job.
/// </summary>
public sealed record DumpCertificate
{
    public string FormatVersion => "dcert/1";
    /// <summary>Image file name (no path — certificates travel with the file).</summary>
    public required string Image { get; init; }
    public required long ImageBytes { get; init; }
    public required int SectorSize { get; init; }
    public required long SectorCount { get; init; }
    /// <summary>Dump-event time, ISO 8601 UTC, supplied by the caller.</summary>
    public required string CreatedUtc { get; init; }
    public required string ImageSha256 { get; init; }
    /// <summary>Merkle root over the sectors, lowercase hex. Leaf = SHA-256 of
    /// the raw sector; parent = SHA-256(left ‖ right); odd nodes promoted.</summary>
    public required string MerkleRoot { get; init; }
    public string MerkleAlgorithm => "SHA-256/sector-leaf/odd-promoted";

    public string? Drive { get; init; }
    public string? Firmware { get; init; }
    public string? Settings { get; init; }
    public string? ToolVersion { get; init; }
    public IReadOnlyList<CertifiedSpan> Spans { get; init; } = Array.Empty<CertifiedSpan>();
    public int UnreadableCount { get; init; }
    public int BoundaryCount { get; init; }
    /// <summary>The post-dump <see cref="Dumping.ExtractionAudit"/> verdict, when one ran.</summary>
    public string? AuditGrade { get; init; }
    public string? Note { get; init; }

    public string? Signature { get; init; }
    public string? PublicKey { get; init; }

    [JsonIgnore] public bool Signed => !string.IsNullOrEmpty(Signature);

    /// <summary>Compute hashes and geometry from an image and build the
    /// unsigned certificate; context fields are filled by the caller.</summary>
    public static DumpCertificate Create(Stream image, string imageName, string createdUtc, int sectorSize = 2352)
    {
        image.Position = 0;
        using var sha = SHA256.Create();
        var fileHash = sha.ComputeHash(image);
        var root = SectorMerkle.ComputeRoot(image, sectorSize, out long leaves);
        return new DumpCertificate
        {
            Image = imageName,
            ImageBytes = image.Length,
            SectorSize = sectorSize,
            SectorCount = leaves,
            CreatedUtc = createdUtc,
            ImageSha256 = System.Convert.ToHexString(fileHash).ToLowerInvariant(),
            MerkleRoot = System.Convert.ToHexString(root).ToLowerInvariant(),
        };
    }

    /// <summary>The canonical bytes a signature covers: identity, geometry,
    /// both hashes, the dump context and every span. Signing the Merkle root
    /// binds every individual sector; signing the file hash binds the whole.</summary>
    internal string SigningContent() =>
        string.Join("\n", new[]
        {
            FormatVersion, Image, ImageBytes.ToString(), SectorSize.ToString(), SectorCount.ToString(),
            CreatedUtc, ImageSha256, MerkleRoot, MerkleAlgorithm,
            Drive ?? "", Firmware ?? "", Settings ?? "", ToolVersion ?? "",
            UnreadableCount.ToString(), BoundaryCount.ToString(), AuditGrade ?? "", Note ?? "",
            string.Join(";", Spans.Select(s => $"{s.Label}|{s.StartLba}|{s.EndLba}|{s.Audio}|{s.Boundary}|{s.Grade}")),
        });

    public DumpCertificate Sign(ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        byte[] sig = privateKey.SignData(Encoding.UTF8.GetBytes(SigningContent()), HashAlgorithmName.SHA256);
        return this with
        {
            Signature = System.Convert.ToBase64String(sig),
            PublicKey = System.Convert.ToBase64String(privateKey.ExportSubjectPublicKeyInfo()),
        };
    }

    /// <summary>Verify the embedded signature against the embedded public key
    /// (or a pinned external one, when the caller does not trust embedding).</summary>
    public bool VerifySignature(byte[]? pinnedPublicSpki = null)
    {
        if (string.IsNullOrEmpty(Signature)) return false;
        byte[] spki;
        if (pinnedPublicSpki is not null) spki = pinnedPublicSpki;
        else if (!string.IsNullOrEmpty(PublicKey)) spki = System.Convert.FromBase64String(PublicKey);
        else return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(SigningContent()),
                                  System.Convert.FromBase64String(Signature), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    /// <summary>Recompute both hashes from the image and compare. The slow,
    /// total check — for one sector, use a <see cref="SectorProof"/>.</summary>
    public bool VerifyImage(Stream image)
    {
        image.Position = 0;
        using var sha = SHA256.Create();
        string fileHash = System.Convert.ToHexString(sha.ComputeHash(image)).ToLowerInvariant();
        if (!string.Equals(fileHash, ImageSha256, StringComparison.OrdinalIgnoreCase)) return false;
        var root = SectorMerkle.ComputeRoot(image, SectorSize, out long leaves);
        return leaves == SectorCount &&
               string.Equals(System.Convert.ToHexString(root).ToLowerInvariant(), MerkleRoot,
                             StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    public static DumpCertificate Load(string path) =>
        JsonSerializer.Deserialize<DumpCertificate>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidDataException($"'{path}' is not a dump certificate.");

    /// <summary>The conventional sidecar path: <c>&lt;image&gt;.dcert.json</c>.</summary>
    public static string SidecarPath(string imagePath) => imagePath + ".dcert.json";
}

/// <summary>
/// A portable proof that ONE sector belongs to a certified dump: the sector's
/// LBA-in-file, its hash, and the Merkle audit path to the certificate's root.
/// Verification needs only the sector's 2352 bytes, this proof, and the
/// certificate — not the image.
/// </summary>
public sealed record SectorProof
{
    public sealed record Step(string Hash, bool SiblingIsLeft);

    public string FormatVersion => "dproof/1";
    public required string Image { get; init; }
    /// <summary>File-sector index (LBA for dumps starting at track 1).</summary>
    public required long Sector { get; init; }
    public required string SectorSha256 { get; init; }
    public required string MerkleRoot { get; init; }
    public required IReadOnlyList<Step> Path { get; init; }

    public static SectorProof Create(byte[][] leaves, long sector, string imageName)
    {
        var path = SectorMerkle.Prove(leaves, sector);
        return new SectorProof
        {
            Image = imageName,
            Sector = sector,
            SectorSha256 = System.Convert.ToHexString(leaves[sector]).ToLowerInvariant(),
            MerkleRoot = System.Convert.ToHexString(SectorMerkle.Root(leaves)).ToLowerInvariant(),
            Path = path.Select(p => new Step(System.Convert.ToHexString(p.Hash).ToLowerInvariant(),
                                             p.SiblingIsLeft)).ToList(),
        };
    }

    /// <summary>Check <paramref name="sectorBytes"/> against this proof and a
    /// certificate's root.</summary>
    public bool Verify(ReadOnlySpan<byte> sectorBytes, string certificateMerkleRoot)
    {
        if (!string.Equals(MerkleRoot, certificateMerkleRoot, StringComparison.OrdinalIgnoreCase))
            return false;
        var steps = Path.Select(s => new SectorMerkle.PathStep(
            System.Convert.FromHexString(s.Hash), s.SiblingIsLeft)).ToList();
        return SectorMerkle.VerifySector(sectorBytes, steps,
            System.Convert.FromHexString(certificateMerkleRoot));
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    public static SectorProof Load(string path) =>
        JsonSerializer.Deserialize<SectorProof>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidDataException($"'{path}' is not a sector proof.");
}
