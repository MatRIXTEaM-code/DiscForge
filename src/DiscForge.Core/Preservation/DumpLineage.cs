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

/// <summary>One step in a dump's documented history — a dump, a corroboration, an
/// ECC repair, a merge, a verification, a conversion, a note. Each event is
/// hash-linked to the one before it, so the log cannot be reordered, edited or have
/// entries removed without breaking the chain.</summary>
public sealed class LineageEvent
{
    /// <summary>Position in the chain (0 = genesis).</summary>
    public int Seq { get; set; }
    /// <summary>What happened: e.g. "dumped", "corroborated", "ecc-repaired",
    /// "merged", "verified", "converted", "sealed", "note".</summary>
    public string Type { get; set; } = "";
    /// <summary>Who or what did it: a drive, a tool, a person.</summary>
    public string? Actor { get; set; }
    /// <summary>Human-readable description.</summary>
    public string? Detail { get; set; }
    /// <summary>When (ISO-8601 UTC), supplied by the caller.</summary>
    public string? Utc { get; set; }
    /// <summary>Structured extras, e.g. drive=... crc=... dat=...</summary>
    public Dictionary<string, string>? Data { get; set; }
    /// <summary>Hash of the previous event ("" for the genesis event).</summary>
    public string PrevHash { get; set; } = "";
    /// <summary>Hash of THIS event over every field above including <see cref="PrevHash"/>.</summary>
    public string Hash { get; set; } = "";
}

/// <summary>An append-only, optionally signed record of everything that happened to a
/// dump. The events form a hash chain; a signature over the chain head proves both
/// that the history is intact and who attests to it.</summary>
public sealed class DumpLineage
{
    public string Schema { get; set; } = DumpLineageLog.SchemaId;
    public string? Subject { get; set; }   // what this lineage is about, e.g. the disc title
    public List<LineageEvent> Events { get; set; } = new();

    /// <summary>Base64 SubjectPublicKeyInfo of the key that signed the head.</summary>
    public string? PublicKey { get; set; }
    /// <summary>Base64 signature over the head event's hash.</summary>
    public string? Signature { get; set; }
    public string? SignatureAlgorithm { get; set; }

    [JsonIgnore] public bool Signed => Signature is { Length: > 0 } && PublicKey is { Length: > 0 };
    [JsonIgnore] public string? HeadHash => Events.Count == 0 ? null : Events[^1].Hash;
}

/// <summary>
/// Chain-of-custody for a dump: an append-only, hash-linked, signable lineage of how
/// it came to be — dumped on drive X on date Y, corroborated by drive Z, ECC-repaired
/// here, merged from these reads, sealed with this manifest digest. Each event carries
/// the hash of the one before it, so nothing can be inserted, reordered or removed
/// without breaking the chain; an ECDSA (NIST P-256) signature over the chain head
/// then makes the whole history tamper-evident and attributable.
///
/// This is provenance, not protection: it documents and proves the honest history of a
/// preservation, and defeats nothing.
/// </summary>
public static class DumpLineageLog
{
    public const string SchemaId = "discforge-lineage/1";
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

    /// <summary>Start a new lineage with a genesis event.</summary>
    public static DumpLineage Start(string? subject, string type, string? actor = null,
                                    string? detail = null, string? utc = null,
                                    IDictionary<string, string>? data = null)
    {
        var lin = new DumpLineage { Subject = subject };
        Append(lin, type, actor, detail, utc, data);
        return lin;
    }

    /// <summary>Append an event, hash-linking it to the current head. Any existing
    /// signature is cleared, because the head — the thing that was signed — has moved.</summary>
    public static LineageEvent Append(DumpLineage lineage, string type, string? actor = null,
                                      string? detail = null, string? utc = null,
                                      IDictionary<string, string>? data = null)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("An event needs a type.", nameof(type));

        var ev = new LineageEvent
        {
            Seq = lineage.Events.Count,
            Type = type,
            Actor = actor,
            Detail = detail,
            Utc = utc,
            Data = data is null ? null : new Dictionary<string, string>(data),
            PrevHash = lineage.Events.Count == 0 ? "" : lineage.Events[^1].Hash,
        };
        ev.Hash = ComputeEventHash(ev);
        lineage.Events.Add(ev);

        // The head changed, so a prior signature no longer covers it.
        lineage.Signature = null;
        lineage.PublicKey = null;
        lineage.SignatureAlgorithm = null;
        return ev;
    }

    /// <summary>Verify the hash chain: sequence numbers, prev-links and per-event
    /// hashes. True only if nothing has been edited, reordered or removed.</summary>
    public static bool VerifyChain(DumpLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        for (int i = 0; i < lineage.Events.Count; i++)
        {
            var ev = lineage.Events[i];
            if (ev.Seq != i) return false;
            string expectedPrev = i == 0 ? "" : lineage.Events[i - 1].Hash;
            if (!string.Equals(ev.PrevHash, expectedPrev, StringComparison.Ordinal)) return false;
            if (!string.Equals(ComputeEventHash(ev), ev.Hash, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>Sign the chain head with an ECDSA P-256 private key, embedding the
    /// public key so anyone can verify without prior key exchange.</summary>
    public static void Sign(DumpLineage lineage, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(privateKey);
        if (lineage.Events.Count == 0) throw new InvalidOperationException("Nothing to sign — the lineage is empty.");

        string head = lineage.Events[^1].Hash;
        byte[] sig = privateKey.SignData(Encoding.UTF8.GetBytes(head), HashAlgorithmName.SHA256);
        lineage.Signature = System.Convert.ToBase64String(sig);
        lineage.PublicKey = System.Convert.ToBase64String(privateKey.ExportSubjectPublicKeyInfo());
        lineage.SignatureAlgorithm = Algorithm;
    }

    /// <summary>Verify the embedded signature over the chain head — and, first, that
    /// the chain itself is intact (a signature over a broken chain proves nothing).</summary>
    public static bool VerifySignature(DumpLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        if (!lineage.Signed) return false;
        if (!VerifyChain(lineage)) return false;

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(System.Convert.FromBase64String(lineage.PublicKey!), out _);
            string head = lineage.Events[^1].Hash;
            return key.VerifyData(Encoding.UTF8.GetBytes(head),
                                  System.Convert.FromBase64String(lineage.Signature!),
                                  HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Create a fresh P-256 key pair, returned as base64 PKCS#8 (private) and
    /// base64 SubjectPublicKeyInfo (public).</summary>
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

    public static string ToJson(DumpLineage lineage) => JsonSerializer.Serialize(lineage, Pretty);

    public static DumpLineage FromJson(string json)
        => JsonSerializer.Deserialize<DumpLineage>(json, Pretty)
           ?? throw new ArgumentException("Empty or invalid lineage.");

    private static string ComputeEventHash(LineageEvent ev)
    {
        string saved = ev.Hash;
        ev.Hash = "";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ev, Canonical));
            return System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally { ev.Hash = saved; }
    }
}
