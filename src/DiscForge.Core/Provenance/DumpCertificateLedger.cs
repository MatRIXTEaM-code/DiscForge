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

namespace DiscForge.Core.Provenance;

/// <summary>
/// One independent submission attesting that a specific disc dumps to a specific bit-exact result. This is
/// deliberately NOT a full <see cref="DiscForge.Core.Recovery.MergeCertificate"/> or
/// <see cref="DiscForge.Core.Preservation.DumpLineage"/> — either can be referenced by hash from
/// <see cref="EvidenceSha256"/> for anyone who wants the full audit trail. A ledger entry is the compact claim
/// the ledger reasons about across strangers: "this disc (by fingerprint) dumps to this exact output (by
/// hash), and I — this key — attest it, at this time." The submitter's signature covers the claim alone (not
/// its position in any particular ledger), so the same signed claim verifies whether it lands in this ledger,
/// a fork of it, or none at all.
/// </summary>
public sealed record LedgerEntry
{
    /// <summary>Position in the ledger (0 = genesis). Assigned by <see cref="DumpCertificateLedgerLog.Append"/>.</summary>
    public int Seq { get; init; }

    /// <summary>The disc-genome <c>ShortId</c> (see <c>DiscForge.Core.Forensics.GenomeFingerprint</c>) — or any
    /// other stable, offset-invariant disc identity string — naming WHICH disc this submission is about.</summary>
    public required string DiscFingerprint { get; init; }

    /// <summary>SHA-256 of the exact dumped/merged image this submission attests to.</summary>
    public required string OutputSha256 { get; init; }

    /// <summary>Optional free-text identity for humans (title, region, catalogue id, ...). Never load-bearing
    /// for consensus — only <see cref="DiscFingerprint"/> and <see cref="OutputSha256"/> are.</summary>
    public string? Label { get; init; }

    /// <summary>Optional hash of a fuller certificate this claim is backed by (a MergeCertificate JSON, a
    /// DumpLineage head, ...) — lets a verifier pull the full evidence out-of-band without bloating the ledger
    /// with every dump's complete forensic record.</summary>
    public string? EvidenceSha256 { get; init; }

    public string? Detail { get; init; }
    public required string Utc { get; init; }

    /// <summary>Base64 SubjectPublicKeyInfo of the submitter's key. This is the identity a consensus count is
    /// over, so <see cref="DumpCertificateLedgerLog.Consensus"/> only ever counts DISTINCT keys — one
    /// submitter re-attesting the same claim twice never inflates the count.</summary>
    public required string SubmitterPublicKey { get; init; }

    /// <summary>Base64 ECDSA (P-256) signature by the submitter over this entry's claim content — everything
    /// above EXCEPT the ledger-chain fields below, which is what makes the claim portable across ledgers.</summary>
    public required string SubmitterSignature { get; init; }

    /// <summary>Hash of the previous entry ("" for the genesis entry) — the ledger-level chain link.</summary>
    public string PrevHash { get; init; } = "";

    /// <summary>Hash of this entry over every field above, including <see cref="PrevHash"/>. Makes the ledger
    /// itself append-only-safe: no entry can be edited, reordered or removed without breaking every hash that
    /// follows it, the same tamper-evidence <c>DumpLineage</c> gives one dump's history, extended here across
    /// many independent submitters and many different discs in one public log.</summary>
    public string Hash { get; init; } = "";
}

/// <summary>One distinct output hash within a disc's submissions, and the distinct submitter keys behind it.</summary>
public sealed record ConsensusGroup(string OutputSha256, IReadOnlyList<string> SubmitterPublicKeys)
{
    public int SubmitterCount => SubmitterPublicKeys.Count;
}

/// <summary>
/// How the ledger's independent submissions for one disc agree or disagree. This is the payoff of a public,
/// multi-submitter ledger over a single signed certificate: nobody has to trust DiscForge, or any one
/// submitter — they can see for themselves how many independent keys converged on the same bytes.
/// </summary>
public sealed record FingerprintConsensus(string DiscFingerprint, int TotalSubmissions,
    IReadOnlyList<ConsensusGroup> Groups)
{
    /// <summary>More than one output hash has independent submitters — a real disagreement worth
    /// investigating (a different revision/region/protection state, or a bad dump), not just noise.</summary>
    public bool Disputed => Groups.Count(g => g.SubmitterCount > 0) > 1;

    /// <summary>The group with the most distinct submitters, if any submissions exist for this disc.</summary>
    public ConsensusGroup? Leading => Groups.OrderByDescending(g => g.SubmitterCount).FirstOrDefault();

    public string Summary()
    {
        if (TotalSubmissions == 0) return $"{DiscFingerprint}: no submissions.";
        string lead = Leading is { } l
            ? $"{l.SubmitterCount} independent submitter(s) agree on {l.OutputSha256[..12]}…"
            : "no agreement";
        return Disputed
            ? $"{DiscFingerprint}: DISPUTED — {Groups.Count} distinct results across {TotalSubmissions} submission(s); {lead}."
            : $"{DiscFingerprint}: consensus — {lead} ({TotalSubmissions} submission(s) total).";
    }
}

/// <summary>An append-only, hash-linked, multi-submitter ledger of dump-certificate claims.</summary>
public sealed class DumpCertificateLedger
{
    public string Schema { get; set; } = DumpCertificateLedgerLog.SchemaId;
    public List<LedgerEntry> Entries { get; set; } = new();

    [JsonIgnore] public string? HeadHash => Entries.Count == 0 ? null : Entries[^1].Hash;
}

/// <summary>
/// A public, append-only, hash-chained ledger of independently signed dump-certificate claims — the
/// cross-submitter counterpart to <c>MergeCertificate</c> (one merge's audited reconstruction) and
/// <c>DumpLineage</c> (one dump's chain of custody). Neither of those can show that strangers, working
/// independently, converged on the same bytes; this ledger exists to make that convergence — or its absence —
/// checkable by anyone, without trusting DiscForge or any single submitter.
///
/// Two independent layers of tamper-evidence: each entry is signed by its submitter over the claim alone (so
/// it is portable and self-verifying, like Redump's per-dumper crediting made cryptographic), and every entry
/// is additionally hash-chained to the one before it (so nobody — including whoever hosts the ledger file —
/// can quietly edit, reorder or drop a past entry without breaking the chain for everyone downstream).
///
/// This is provenance and consensus, not authority: the ledger does not decide which dump is "correct" — it
/// makes the disagreement, or the agreement, visible and auditable.
/// </summary>
public static class DumpCertificateLedgerLog
{
    public const string SchemaId = "discforge-dump-ledger/1";
    public const string Algorithm = "ECDSA-P256-SHA256";

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

    /// <summary>Create a fresh P-256 key pair, returned as base64 PKCS#8 (private) and base64
    /// SubjectPublicKeyInfo (public) — one per submitter identity.</summary>
    public static (string PrivateKeyBase64, string PublicKeyBase64) GenerateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (System.Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
                System.Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Load a base64 PKCS#8 private key produced by <see cref="GenerateKey"/>.</summary>
    public static ECDsa LoadPrivateKey(string privateKeyBase64)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(System.Convert.FromBase64String(privateKeyBase64), out _);
        return key;
    }

    /// <summary>The canonical bytes a submitter's signature covers: the claim content only, never the
    /// ledger-chain fields (Seq/PrevHash/Hash) — so a submitter signs their claim once and it stays valid no
    /// matter which ledger, or which position in it, the claim later lands in.</summary>
    private static string SubmitterSigningContent(string discFingerprint, string outputSha256, string? label,
        string? evidenceSha256, string? detail, string utc) =>
        string.Join("\n", new[] { discFingerprint, outputSha256, label ?? "", evidenceSha256 ?? "", detail ?? "", utc });

    /// <summary>The exact UTF-8 bytes <see cref="VerifySubmitterSignature"/> feeds to ECDSA (SHA-256 over
    /// <see cref="SubmitterSigningContent"/>'s content) — exposed publicly so a caller whose runtime lacks a
    /// usable <see cref="ECDsa"/> (browser-wasm has none in .NET 8; see DiscForge.Wasm) can verify the same
    /// signature through a different backend, e.g. the browser's own Web Crypto <c>SubtleCrypto</c>, and get
    /// an identical answer. <see cref="VerifySubmitterSignature"/> is defined in terms of this method, not
    /// the reverse, so the two can never silently diverge.</summary>
    public static byte[] GetSubmitterSigningBytes(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string content = SubmitterSigningContent(entry.DiscFingerprint, entry.OutputSha256, entry.Label,
            entry.EvidenceSha256, entry.Detail, entry.Utc);
        return Encoding.UTF8.GetBytes(content);
    }

    /// <summary>Build and sign a new claim (not yet appended to any ledger).</summary>
    public static LedgerEntry CreateSubmission(string discFingerprint, string outputSha256, ECDsa submitterKey,
        string? label = null, string? evidenceSha256 = null, string? detail = null, string? utc = null)
    {
        if (string.IsNullOrWhiteSpace(discFingerprint)) throw new ArgumentException("A disc fingerprint is required.", nameof(discFingerprint));
        if (string.IsNullOrWhiteSpace(outputSha256)) throw new ArgumentException("An output hash is required.", nameof(outputSha256));
        ArgumentNullException.ThrowIfNull(submitterKey);
        utc ??= DateTime.UtcNow.ToString("o");

        string content = SubmitterSigningContent(discFingerprint, outputSha256, label, evidenceSha256, detail, utc);
        byte[] sig = submitterKey.SignData(Encoding.UTF8.GetBytes(content), HashAlgorithmName.SHA256);
        return new LedgerEntry
        {
            DiscFingerprint = discFingerprint,
            OutputSha256 = outputSha256,
            Label = label,
            EvidenceSha256 = evidenceSha256,
            Detail = detail,
            Utc = utc,
            SubmitterPublicKey = System.Convert.ToBase64String(submitterKey.ExportSubjectPublicKeyInfo()),
            SubmitterSignature = System.Convert.ToBase64String(sig),
        };
    }

    /// <summary>Verify a claim's own signature, independent of any ledger membership.</summary>
    public static bool VerifySubmitterSignature(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(System.Convert.FromBase64String(entry.SubmitterPublicKey), out _);
            return key.VerifyData(GetSubmitterSigningBytes(entry),
                System.Convert.FromBase64String(entry.SubmitterSignature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }

    /// <summary>Append an already-signed submission to the ledger, hash-linking it to the current head. Refuses
    /// — rather than silently accepting — a submission whose own signature does not verify: a public ledger
    /// must never carry a claim nobody actually attested to.</summary>
    public static LedgerEntry Append(DumpCertificateLedger ledger, LedgerEntry submission)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(submission);
        if (!VerifySubmitterSignature(submission))
            throw new InvalidOperationException(
                "This submission's own signature does not verify — refusing to add an unattestable claim to the ledger.");

        var entry = submission with
        {
            Seq = ledger.Entries.Count,
            PrevHash = ledger.Entries.Count == 0 ? "" : ledger.Entries[^1].Hash,
            Hash = "",
        };
        entry = entry with { Hash = ComputeEntryHash(entry) };
        ledger.Entries.Add(entry);
        return entry;
    }

    /// <summary>Verify the ledger's own chain: sequence numbers, prev-links and per-entry hashes. True only if
    /// nothing has been edited, reordered, inserted or removed since the entries were appended.</summary>
    public static bool VerifyChain(DumpCertificateLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        for (int i = 0; i < ledger.Entries.Count; i++)
        {
            var e = ledger.Entries[i];
            if (e.Seq != i) return false;
            string expectedPrev = i == 0 ? "" : ledger.Entries[i - 1].Hash;
            if (!string.Equals(e.PrevHash, expectedPrev, StringComparison.Ordinal)) return false;
            if (!string.Equals(ComputeEntryHash(e), e.Hash, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>Verify every entry's own submitter signature — independent of chain integrity, this confirms
    /// each claim really was attested by the key it names.</summary>
    public static bool VerifyAllSubmitterSignatures(DumpCertificateLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return ledger.Entries.All(VerifySubmitterSignature);
    }

    /// <summary>How independent submitters agree or disagree about one disc: every distinct output hash,
    /// grouped by DISTINCT submitter public key, ranked by submitter count.</summary>
    public static FingerprintConsensus Consensus(DumpCertificateLedger ledger, string discFingerprint)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(discFingerprint);
        var matches = ledger.Entries
            .Where(e => string.Equals(e.DiscFingerprint, discFingerprint, StringComparison.Ordinal))
            .ToList();
        var groups = matches
            .GroupBy(e => e.OutputSha256, StringComparer.Ordinal)
            .Select(g => new ConsensusGroup(g.Key, g.Select(e => e.SubmitterPublicKey).Distinct(StringComparer.Ordinal).ToList()))
            .OrderByDescending(g => g.SubmitterCount)
            .ToList();
        return new FingerprintConsensus(discFingerprint, matches.Count, groups);
    }

    public static string ToJson(DumpCertificateLedger ledger) => JsonSerializer.Serialize(ledger, Pretty);

    public static DumpCertificateLedger FromJson(string json) =>
        JsonSerializer.Deserialize<DumpCertificateLedger>(json, Pretty)
        ?? throw new ArgumentException("Empty or invalid ledger.");

    private static string ComputeEntryHash(LedgerEntry e)
    {
        var forHash = e with { Hash = "" };
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(forHash, Canonical));
        return System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
