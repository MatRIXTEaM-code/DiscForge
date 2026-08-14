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
using DiscForge.Core.Forensics;

namespace DiscForge.Core.Preservation;

/// <summary>One dumper's signed statement: "I independently dumped this disc and got this genome."
/// The disc identity is the offset-invariant part of the genome (layout + addressed data), so two
/// faithful dumps at different read offsets still attest the same identity.</summary>
public sealed class ConsensusAttestation
{
    /// <summary>Human label for the disc (title/version). Informational; identity is the hashes.</summary>
    public string DiscId { get; set; } = "";
    public string LayoutHash { get; set; } = "";
    public string DataHash { get; set; } = "";
    /// <summary>Hash of the offset-tolerant audio envelope — recorded, not part of the identity key.</summary>
    public string AudioEnvelopeHash { get; set; } = "";
    /// <summary>The dumper's public key (base64 SubjectPublicKeyInfo) — travels with the attestation.</summary>
    public string DumperPublicKey { get; set; } = "";
    public string Utc { get; set; } = "";
    public string Signature { get; set; } = "";
    public string SignatureAlgorithm { get; set; } = "";

    // Ledger chain fields (assigned on append).
    public int Seq { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";

    /// <summary>The canonical disc-identity key: a hash over the offset-invariant genome parts.</summary>
    [JsonIgnore] public string GenomeKey => ConsensusLog.GenomeKey(LayoutHash, DataHash);
}

/// <summary>An append-only, hash-linked ledger of independent dump attestations.</summary>
public sealed class ConsensusLedger
{
    public string Schema { get; set; } = ConsensusLog.SchemaId;
    public List<ConsensusAttestation> Attestations { get; set; } = new();
}

/// <summary>How much independent agreement a disc identity has.</summary>
public enum ConsensusLevel : byte
{
    /// <summary>One dumper — recorded but unverified.</summary>
    Single = 1,
    /// <summary>Two independent dumpers agree.</summary>
    Corroborated = 2,
    /// <summary>Three or more independent dumpers agree — treat as canonical.</summary>
    Consensus = 3,
}

/// <summary>The consensus standing of one disc identity across the ledger.</summary>
public sealed record ConsensusResult
{
    public required string GenomeKey { get; init; }
    public required string DiscId { get; init; }
    public required int IndependentDumpers { get; init; }
    public required int Attestations { get; init; }
    public required IReadOnlyList<string> DumperKeys { get; init; }
    public required ConsensusLevel Level { get; init; }
    /// <summary>Another identity carries the same DiscId with different hashes — a disputed dump.</summary>
    public required bool Disputed { get; init; }

    public string Summary()
    {
        string dispute = Disputed ? " — DISPUTED: another image claims the same title" : "";
        return $"{DiscId} [{GenomeKey[..12]}…]: {Level} ({IndependentDumpers} independent dumper(s)){dispute}.";
    }
}

/// <summary>
/// Federated preservation consensus — a decentralised, cryptographically-verifiable alternative to a
/// central preservation database. Every dumper signs an attestation binding a disc's offset-invariant
/// genome identity to their own public key; those attestations collect in an append-only, hash-linked
/// ledger anyone can verify. When several independent keys — people who never coordinated — attest the
/// exact same genome, that agreement is proof of the canonical image that no single authority has to be
/// trusted for. Trust becomes arithmetic: count the independent signatures. The ledger also surfaces
/// disputes (two different images claiming the same title) for a human to adjudicate. It records and
/// verifies claims about faithful dumps; it moves no protected content and defeats nothing.
/// </summary>
public static class ConsensusLog
{
    public const string SchemaId = "discforge-consensus/1";
    public const string Algorithm = "ECDSA-P256-SHA256";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Fresh P-256 key pair (base64 PKCS#8 private, base64 SPKI public).</summary>
    public static (string PrivateKeyBase64, string PublicKeyBase64) GenerateKey() => DumpLineageLog.GenerateKey();

    public static ECDsa LoadPrivateKey(string privateKeyBase64) => DumpLineageLog.LoadPrivateKey(privateKeyBase64);

    /// <summary>The canonical disc-identity key: SHA-256 over the offset-invariant genome parts.</summary>
    public static string GenomeKey(string layoutHash, string dataHash)
        => System.Convert.ToHexString(
               SHA256.HashData(Encoding.ASCII.GetBytes((layoutHash ?? "") + "\n" + (dataHash ?? ""))))
           .ToLowerInvariant();

    /// <summary>Create and sign an attestation for a genome with a dumper's private key.</summary>
    public static ConsensusAttestation CreateAttestation(string discId, GenomeFingerprint genome,
                                                         ECDsa privateKey, string utc)
    {
        ArgumentNullException.ThrowIfNull(genome);
        ArgumentNullException.ThrowIfNull(privateKey);

        var a = new ConsensusAttestation
        {
            DiscId = discId ?? "",
            LayoutHash = genome.LayoutHash,
            DataHash = genome.DataHash,
            AudioEnvelopeHash = System.Convert.ToHexString(SHA256.HashData(genome.AudioEnvelope)).ToLowerInvariant(),
            Utc = utc ?? "",
            DumperPublicKey = System.Convert.ToBase64String(privateKey.ExportSubjectPublicKeyInfo()),
            SignatureAlgorithm = Algorithm,
        };
        byte[] sig = privateKey.SignData(Encoding.UTF8.GetBytes(SigningContent(a)), HashAlgorithmName.SHA256);
        a.Signature = System.Convert.ToBase64String(sig);
        return a;
    }

    /// <summary>Verify an attestation's signature with its embedded public key.</summary>
    public static bool VerifyAttestation(ConsensusAttestation a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (string.IsNullOrEmpty(a.Signature) || string.IsNullOrEmpty(a.DumperPublicKey)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(System.Convert.FromBase64String(a.DumperPublicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(SigningContent(a)),
                                  System.Convert.FromBase64String(a.Signature),
                                  HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static ConsensusLedger NewLedger() => new();

    /// <summary>Append an attestation, linking it into the ledger's hash chain.</summary>
    public static void Append(ConsensusLedger ledger, ConsensusAttestation a)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(a);
        a.Seq = ledger.Attestations.Count;
        a.PrevHash = ledger.Attestations.Count == 0 ? "" : ledger.Attestations[^1].Hash;
        a.Hash = ChainHash(a);
        ledger.Attestations.Add(a);
    }

    /// <summary>Verify the ledger: the hash chain (no insertion/removal/reorder) and every signature.</summary>
    public static bool VerifyLedger(ConsensusLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        string prev = "";
        for (int i = 0; i < ledger.Attestations.Count; i++)
        {
            var a = ledger.Attestations[i];
            if (a.Seq != i || a.PrevHash != prev) return false;
            if (a.Hash != ChainHash(a)) return false;
            if (!VerifyAttestation(a)) return false;
            prev = a.Hash;
        }
        return true;
    }

    /// <summary>Tally consensus per disc identity: count DISTINCT dumper keys attesting each genome,
    /// and flag identities that share a DiscId with a divergent identity (a dispute).</summary>
    public static IReadOnlyList<ConsensusResult> Tally(ConsensusLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var valid = ledger.Attestations.Where(VerifyAttestation).ToList();

        // How many distinct identities each DiscId label maps to (for dispute detection).
        var idsPerLabel = valid
            .GroupBy(a => a.DiscId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(a => a.GenomeKey).Distinct().Count(),
                          StringComparer.OrdinalIgnoreCase);

        var results = new List<ConsensusResult>();
        foreach (var g in valid.GroupBy(a => a.GenomeKey))
        {
            var keys = g.Select(a => a.DumperPublicKey).Distinct().ToList();
            int independent = keys.Count;
            var level = independent >= 3 ? ConsensusLevel.Consensus
                      : independent == 2 ? ConsensusLevel.Corroborated
                      : ConsensusLevel.Single;
            string discId = g.Select(a => a.DiscId).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";

            results.Add(new ConsensusResult
            {
                GenomeKey = g.Key,
                DiscId = discId,
                IndependentDumpers = independent,
                Attestations = g.Count(),
                DumperKeys = keys.Select(k => ShortKey(k)).ToList(),
                Level = level,
                Disputed = !string.IsNullOrEmpty(discId) && idsPerLabel.GetValueOrDefault(discId) > 1,
            });
        }

        return results
            .OrderByDescending(r => r.IndependentDumpers)
            .ThenBy(r => r.DiscId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Render(IReadOnlyList<ConsensusResult> results, bool ledgerValid)
    {
        var sb = new StringBuilder();
        int canonical = results.Count(r => r.Level == ConsensusLevel.Consensus);
        int disputed = results.Count(r => r.Disputed);
        sb.AppendLine($"Ledger {(ledgerValid ? "VERIFIED" : "INVALID")} — {results.Count} disc identity(ies), " +
                      $"{canonical} at consensus, {disputed} disputed.");
        foreach (var r in results)
        {
            sb.AppendLine($"  {r.Summary()}");
            sb.AppendLine($"      dumpers: {string.Join(", ", r.DumperKeys)}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string ToJson(ConsensusLedger ledger) => JsonSerializer.Serialize(ledger, Pretty);

    public static ConsensusLedger FromJson(string json)
        => JsonSerializer.Deserialize<ConsensusLedger>(json, Pretty)
           ?? throw new ArgumentException("Empty or invalid consensus ledger.");

    // ---- internals ----------------------------------------------------------

    // What each dumper signs — the identity binding, independent of ledger position.
    private static string SigningContent(ConsensusAttestation a) =>
        string.Join('\n', a.DiscId, a.LayoutHash, a.DataHash, a.AudioEnvelopeHash, a.DumperPublicKey, a.Utc);

    private static string ChainHash(ConsensusAttestation a)
    {
        string content = string.Join('\n', a.Seq.ToString(), a.PrevHash, a.Signature, SigningContent(a));
        return System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static string ShortKey(string base64Spki)
    {
        // A short, stable id for a public key: first 12 hex of its SHA-256.
        try
        {
            var h = SHA256.HashData(System.Convert.FromBase64String(base64Spki));
            return System.Convert.ToHexString(h)[..12].ToLowerInvariant();
        }
        catch { return "????????????"; }
    }
}
