// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text;

namespace DiscForge.Core.Licensing;

/// <summary>The outcome of checking a licence key.</summary>
public enum LicenseState
{
    Valid,
    Missing,        // no key supplied
    Malformed,      // not a DiscForge key / undecodable
    BadSignature,   // signature does not verify against the vendor public key (forged/corrupt)
    Expired,        // past its expiry date
    WrongMachine,   // locked to a different machine
}

/// <summary>The contents of a licence.</summary>
public sealed record LicenseInfo
{
    public required string Name { get; init; }
    public required string Edition { get; init; }
    public required DateTime IssuedUtc { get; init; }
    /// <summary>Null = perpetual.</summary>
    public DateTime? ExpiresUtc { get; init; }
    /// <summary>Null/empty = valid on any machine; otherwise the bound machine id.</summary>
    public string? MachineId { get; init; }
}

/// <summary>The validated result: the state, the licence contents (when decodable), and a message.</summary>
public sealed record LicenseResult
{
    public required LicenseState State { get; init; }
    public LicenseInfo? Info { get; init; }
    public required string Message { get; init; }
    public bool IsValid => State == LicenseState.Valid;
}

/// <summary>
/// Public-key licence keys. The vendor holds an ECDSA (P-256) private key and signs a
/// licence's contents; the application embeds only the matching PUBLIC key and verifies.
/// Because the private key never ships, a key cannot be forged — the signature would
/// fail — which is the point of asymmetric licensing (an attacker can only patch the
/// check out, not mint valid keys). Keys are self-contained: the licensee, edition,
/// issue/expiry dates and optional machine lock all travel inside the signed blob.
///
/// This is a deterrent, not DRM: like any client-side check it can be bypassed by
/// someone determined. It stops casual copying and enables legitimate licensing.
/// </summary>
public static class License
{
    private const string Tag = "DFLIC1";
    private const char Sep = '\u001F';   // unit separator — never appears in a name/edition

    /// <summary>Generate a fresh signing key pair. The vendor runs this once and keeps the private key secret.</summary>
    public static (byte[] PublicSpki, byte[] PrivatePkcs8) GenerateKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ec.ExportSubjectPublicKeyInfo(), ec.ExportPkcs8PrivateKey());
    }

    /// <summary>Sign a licence with the vendor private key, producing a shareable key string.</summary>
    public static string Issue(LicenseInfo info, ReadOnlySpan<byte> privatePkcs8)
    {
        ArgumentNullException.ThrowIfNull(info);
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(privatePkcs8, out _);
        byte[] payload = Encode(info);
        byte[] sig = ec.SignData(payload, HashAlgorithmName.SHA256);
        // payload and signature are base64url (which itself uses '-' and '_'), joined by '.'.
        return Base64Url(payload) + "." + Base64Url(sig);
    }

    /// <summary>
    /// Verify a key against the embedded public key and check its expiry and machine lock.
    /// Pass the current machine id to enforce a machine lock; pass null to skip that check
    /// (e.g. when merely inspecting a key).
    /// </summary>
    public static LicenseResult Validate(string? key, ReadOnlySpan<byte> publicSpki, string? currentMachineId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(key)) return Fail(LicenseState.Missing, "No licence key present.");

        string clean = Ungroup(key);
        int dot = clean.IndexOf('.');
        if (dot <= 0 || dot >= clean.Length - 1) return Fail(LicenseState.Malformed, "Not a DiscForge licence key.");

        byte[] payload, sig;
        try { payload = FromBase64Url(clean[..dot]); sig = FromBase64Url(clean[(dot + 1)..]); }
        catch { return Fail(LicenseState.Malformed, "Licence key is corrupt."); }

        using var ec = ECDsa.Create();
        try { ec.ImportSubjectPublicKeyInfo(publicSpki, out _); }
        catch { return Fail(LicenseState.Malformed, "The embedded public key is invalid."); }

        bool ok;
        try { ok = ec.VerifyData(payload, sig, HashAlgorithmName.SHA256); }
        catch { return Fail(LicenseState.BadSignature, "Licence signature could not be verified."); }
        if (!ok) return Fail(LicenseState.BadSignature, "Licence signature does not verify — the key is forged or corrupt.");

        if (!TryDecode(payload, out var info))
            return Fail(LicenseState.Malformed, "Licence contents are unreadable.");

        if (info.ExpiresUtc is { } exp && nowUtc.ToUniversalTime() > exp)
            return new LicenseResult { State = LicenseState.Expired, Info = info, Message = $"Licence expired on {exp:yyyy-MM-dd}." };

        if (!string.IsNullOrEmpty(info.MachineId) && !string.IsNullOrEmpty(currentMachineId) &&
            !string.Equals(info.MachineId, currentMachineId, StringComparison.OrdinalIgnoreCase))
            return new LicenseResult { State = LicenseState.WrongMachine, Info = info, Message = "This licence is locked to a different machine." };

        return new LicenseResult { State = LicenseState.Valid, Info = info, Message = $"Licensed to {info.Name}." };
    }

    // ---- payload codec ----

    private static byte[] Encode(LicenseInfo i)
    {
        if (i.Name.IndexOf(Sep) >= 0 || i.Edition.IndexOf(Sep) >= 0 || (i.MachineId?.IndexOf(Sep) ?? -1) >= 0)
            throw new ArgumentException("Licence fields may not contain the unit-separator character.");
        string s = string.Join(Sep, Tag, i.Name, i.Edition,
            i.IssuedUtc.ToUniversalTime().Ticks.ToString(),
            (i.ExpiresUtc?.ToUniversalTime().Ticks ?? 0L).ToString(),
            i.MachineId ?? "");
        return Encoding.UTF8.GetBytes(s);
    }

    private static bool TryDecode(byte[] payload, out LicenseInfo info)
    {
        info = null!;
        var parts = Encoding.UTF8.GetString(payload).Split(Sep);
        if (parts.Length != 6 || parts[0] != Tag) return false;
        if (!long.TryParse(parts[3], out long issued) || !long.TryParse(parts[4], out long expires)) return false;

        try
        {
            info = new LicenseInfo
            {
                Name = parts[1],
                Edition = parts[2],
                IssuedUtc = new DateTime(issued, DateTimeKind.Utc),
                ExpiresUtc = expires == 0 ? null : new DateTime(expires, DateTimeKind.Utc),
                MachineId = parts[5].Length == 0 ? null : parts[5],
            };
        }
        catch { return false; }
        return true;
    }

    // ---- helpers ----

    private static LicenseResult Fail(LicenseState state, string message) =>
        new() { State = state, Message = message };

    private static string Base64Url(byte[] data) =>
        System.Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        string b = s.Replace('-', '+').Replace('_', '/');
        switch (b.Length % 4) { case 2: b += "=="; break; case 3: b += "="; break; }
        return System.Convert.FromBase64String(b);
    }

    // Remove any whitespace a user introduced when pasting (line breaks, spaces). Dashes
    // and underscores are part of the base64url alphabet, so they are kept.
    private static string Ungroup(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (char c in key) if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }
}

/// <summary>
/// A stable, non-reversible machine fingerprint for optional machine-locking. The raw
/// source (e.g. the Windows MachineGuid) is hashed so the licence never carries anything
/// identifying about the machine beyond a short opaque id.
/// </summary>
public static class MachineId
{
    public static string FromRaw(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("DiscForge-machine|" + raw.Trim()));
        string hex = System.Convert.ToHexString(h, 0, 8);   // 64-bit id, 16 hex chars
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }
}

/// <summary>
/// The vendor public key the application trusts. REPLACE the placeholder below with your
/// own key — run <c>dforge license keygen</c> to create a key pair, keep the private key
/// secret, and paste the printed public key here. Until you do, no licence will validate
/// (the shipped app is "unlicensed" by default, which is the safe state).
/// </summary>
public static class LicenseConfig
{
    /// <summary>Base64 of the ECDSA P-256 SubjectPublicKeyInfo — the MaTRIX TeAm signing key.</summary>
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6fsZ0yEvIo9XT+qmTAgscCFDnL/Ncd05quF9HCIC71NO8YN4WadlwvSrKe/OV2WaV3EWD2OwdoScOlmCwZlfLw==";

    public static byte[] PublicSpki => System.Convert.FromBase64String(PublicKeyBase64);
}
