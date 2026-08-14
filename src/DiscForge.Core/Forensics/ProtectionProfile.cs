// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Files;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Forensics;

/// <summary>One class of protection evidence and whether the current capture can physically hold it.</summary>
public sealed record ProtectionFacet(string Name, bool Preservable, string Detail);

/// <summary>A detected scheme, plus whether <i>this</i> dump actually preserves the mark it leaves.</summary>
public sealed record ProtectionProfileScheme
{
    public required string Scheme { get; init; }
    public required string Evidence { get; init; }
    public required string Guidance { get; init; }
    public required IReadOnlyList<long> SignificantLbas { get; init; }
    /// <summary>True when the capture mode holds the facet this scheme's signature lives in.</summary>
    public required bool FullyCapturable { get; init; }
    public required string CaptureNote { get; init; }
}

/// <summary>
/// A disc's unified clean-room protection profile: which schemes are fingerprinted, where their physical
/// signatures sit, and — the part no single detector answers — whether the capture mode in hand can actually
/// preserve each. A cooked ISO cannot hold a LibCrypt subchannel or a weak-sector body no matter how carefully
/// it is hashed; this profile says so, and tells the dumper the capture that would.
/// </summary>
public sealed record ProtectionProfile
{
    public required string ImageKind { get; init; }
    public required long TotalSectors { get; init; }
    public required bool HasSubchannel { get; init; }
    public required bool RawSectors { get; init; }
    public required IReadOnlyList<ProtectionProfileScheme> Schemes { get; init; }
    public required IReadOnlyList<ProtectionFacet> CaptureCompleteness { get; init; }

    public bool AnyProtection => Schemes.Count > 0;
    /// <summary>True when every detected scheme's signature is actually held by this capture.</summary>
    public bool FullyPreserved => Schemes.All(s => s.FullyCapturable);

    public string Summary()
    {
        if (!AnyProtection)
            return $"No known protection fingerprint. This {ImageKind} capture holds: " +
                   $"{string.Join(", ", CaptureCompleteness.Where(f => f.Preservable).Select(f => f.Name))}.";
        var names = string.Join(", ", Schemes.Select(s => s.Scheme));
        return FullyPreserved
            ? $"Protection fingerprint fully preserved by this {ImageKind} capture: {names}."
            : $"Protection detected but UNDER-CAPTURED by this {ImageKind} image: {names}. " +
              "See the per-scheme capture notes — a RAW+subchannel recapture would preserve the missing marks.";
    }
}

/// <summary>
/// protection-profile — build a disc's unified protection profile. It runs the existing fingerprint scan
/// (SafeDisc / SecuROM / LibCrypt / Laserlock / ProtectCD and weak-sector patterns), then adds the assessment
/// no single command gives: keyed to the capture's real image kind and whether it carries subchannel, it works
/// out which protection facets this dump can actually preserve — filesystem markers, the raw sector body, the
/// subchannel Q, and the physical/angular timing — and flags any detected scheme whose signature the capture
/// cannot hold. Pure clean-room characterisation: it names, locates and assesses; it removes and bypasses
/// nothing.
/// </summary>
public static class ProtectionProfiler
{
    public static ProtectionProfile Build(SectorAccess access, IReadOnlyList<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(access);
        var report = ProtectionScanner.Scan(access, fileNames ?? Array.Empty<string>());

        bool raw = access.Kind is SectorAccess.ImageKind.Bin2352 or SectorAccess.ImageKind.RawDao;
        bool hasSub = DetectSubchannel(access);

        var facets = new List<ProtectionFacet>
        {
            new("filesystem/executable markers", true,
                "captured in any data-track image (marker files, wrapped-EXE signatures)"),
            new("raw sector body (weak/twin/dummy sectors, EDC anomalies)", raw,
                raw ? "held — this image stores full 2352-byte sectors"
                    : $"NOT held — a {access.Kind} image stores cooked user data only; recapture as RAW (2352)"),
            new("subchannel Q (LibCrypt, SecuROM subchannel)", hasSub,
                hasSub ? "held — this capture carries subchannel"
                       : "NOT held — no subchannel in this capture; recapture with a .sub sidecar"),
            new("physical/angular timing (ring / data-position)", false,
                "never held by a sector image — capture separately with a DPM timing scan (see the dpm command)"),
        };

        var schemes = report.Findings
            .Where(f => f.Scheme != ProtectionScanner.Scheme.None)
            .Select(f =>
            {
                var (facet, capturable) = RequiredFacet(f.Scheme, raw, hasSub);
                return new ProtectionProfileScheme
                {
                    Scheme = f.Scheme.ToString(),
                    Evidence = f.Evidence,
                    Guidance = f.Guidance,
                    SignificantLbas = f.SignificantLbas,
                    FullyCapturable = capturable,
                    CaptureNote = capturable
                        ? $"its {facet} signature is preserved by this capture"
                        : $"its signature lives in the {facet}, which this {access.Kind} image does not hold — recapture accordingly",
                };
            })
            .ToList();

        return new ProtectionProfile
        {
            ImageKind = access.Kind.ToString(),
            TotalSectors = access.TotalSectors,
            HasSubchannel = hasSub,
            RawSectors = raw,
            Schemes = schemes,
            CaptureCompleteness = facets,
        };
    }

    private static (string Facet, bool Capturable) RequiredFacet(ProtectionScanner.Scheme scheme, bool raw, bool hasSub) =>
        scheme switch
        {
            ProtectionScanner.Scheme.LibCrypt or ProtectionScanner.Scheme.SecuROM
                => ("subchannel Q", hasSub),
            ProtectionScanner.Scheme.SafeDisc or ProtectionScanner.Scheme.SafeDiscLike
                or ProtectionScanner.Scheme.Laserlock or ProtectionScanner.Scheme.Unknown
                => ("raw sector body", raw),
            ProtectionScanner.Scheme.ProtectCd
                => ("filesystem markers", true),
            _ => ("filesystem markers", true),
        };

    /// <summary>Sample a few sectors and report whether the capture carries subchannel data.</summary>
    private static bool DetectSubchannel(SectorAccess access)
    {
        long total = access.TotalSectors;
        if (total <= 0) return false;
        foreach (long idx in new[] { 0L, total / 2, total - 1 })
        {
            try
            {
                var s = access.Read(idx);
                if (s.Subcode is { Length: > 0 }) return true;
            }
            catch { /* keep sampling */ }
        }
        return false;
    }
}
