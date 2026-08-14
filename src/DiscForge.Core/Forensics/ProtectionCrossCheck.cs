// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>How the independent protection signals line up.</summary>
public enum ProtectionStanding : byte
{
    /// <summary>No protection indicators from any source.</summary>
    None = 0,
    /// <summary>Loader files/strings present, but no physical on-disc signature was observed.</summary>
    FilesystemOnly = 1,
    /// <summary>A deliberate on-disc signature (error band / twin sectors) with no known scheme.</summary>
    PhysicalOnly = 2,
    /// <summary>Filesystem scheme and a physical on-disc signature agree — the strongest verdict.</summary>
    Corroborated = 3,
}

/// <summary>The fused protection verdict for a disc, drawn from every available signal.</summary>
public sealed record FusedProtection
{
    public required ProtectionStanding Standing { get; init; }
    public required IReadOnlyList<string> Schemes { get; init; }
    public required bool PhysicalSignature { get; init; }
    public required IReadOnlyList<string> Evidence { get; init; }
    public required string Guidance { get; init; }

    public bool AnyProtection => Standing != ProtectionStanding.None;

    public string Summary() => Standing switch
    {
        ProtectionStanding.Corroborated =>
            $"Confirmed on-disc protection: {string.Join(", ", Schemes)} — filesystem marks corroborated by a physical signature.",
        ProtectionStanding.FilesystemOnly =>
            $"Protection files present ({string.Join(", ", Schemes)}) but no physical on-disc signature observed.",
        ProtectionStanding.PhysicalOnly =>
            "A deliberate on-disc protection signature with no recognised scheme — possible unknown or console protection.",
        _ => "No protection indicators.",
    };
}

/// <summary>
/// Protection cross-check — fuse the independent protection signals into one verdict none of them can
/// reach alone. The filesystem catalog spots a scheme by the loader files and executable strings it
/// drops; the error-pattern analyser spots a deliberate bad-sector band by its regular shape; the
/// twin-sector scan spots address tricks written into the headers. Any one can mislead — a cracked
/// disc keeps the loader files but loses the bad sectors; an unknown console scheme leaves a deliberate
/// pattern but matches no catalog entry. When the filesystem marks and a physical on-disc signature
/// <i>agree</i>, that is the strongest evidence short of a published DAT that the protection is real and
/// intact on this disc — and exactly what a faithful backup must preserve verbatim. It also flags the
/// mismatches (files but no signature; a signature but no scheme) so nothing is quietly assumed.
/// Detection and reconciliation only; it defeats nothing.
/// </summary>
public static class ProtectionCrossCheck
{
    public static FusedProtection Fuse(ProtectionReport? catalog, ErrorPatternReport? errors, TwinSectorReport? twins)
    {
        var schemes = new List<string>();
        var evidence = new List<string>();

        if (catalog is not null)
            foreach (var d in catalog.Detections.Where(d => d.Confidence >= ProtectionConfidence.Likely))
            {
                schemes.Add(d.Version is { Length: > 0 } ? $"{d.Scheme} {d.Version}" : d.Scheme);
                string ev = d.Evidence.Count > 0 ? d.Evidence[0].Detail : "marker";
                evidence.Add($"filesystem: {d.Scheme} ({ev})");
            }

        bool errorBand = errors is not null &&
                         errors.Verdict is ErrorPatternKind.DeliberatePattern or ErrorPatternKind.Mixed;
        if (errorBand) evidence.Add("error pattern: a deliberate bad-sector band (not physical damage)");

        bool addressTricks = twins is { LooksProtected: true };
        if (addressTricks)
            evidence.Add($"headers: {twins!.TwinSectors} twin + {twins.MisaddressedSectors} re-addressed sector(s)");

        bool hasFs = schemes.Count > 0;
        bool physical = errorBand || addressTricks;

        var standing = (hasFs, physical) switch
        {
            (true, true) => ProtectionStanding.Corroborated,
            (true, false) => ProtectionStanding.FilesystemOnly,
            (false, true) => ProtectionStanding.PhysicalOnly,
            _ => ProtectionStanding.None,
        };

        return new FusedProtection
        {
            Standing = standing,
            Schemes = schemes,
            PhysicalSignature = physical,
            Evidence = evidence,
            Guidance = GuidanceFor(standing),
        };
    }

    public static string Render(FusedProtection f)
    {
        var sb = new StringBuilder();
        sb.AppendLine(f.Summary());
        foreach (var e in f.Evidence) sb.AppendLine($"  - {e}");
        if (f.AnyProtection) sb.AppendLine($"  guidance: {f.Guidance}");
        return sb.ToString().TrimEnd();
    }

    private static string GuidanceFor(ProtectionStanding s) => s switch
    {
        ProtectionStanding.Corroborated =>
            "Preserve verbatim — image RAW with subchannel; do not repair the bad sectors or normalise the addresses. " +
            "Record the scheme as preservation metadata.",
        ProtectionStanding.FilesystemOnly =>
            "The loader is present but its physical protection wasn't seen — this may be a reproduction or a partial dump. " +
            "Preserve as-is and note that the on-disc signature was absent.",
        ProtectionStanding.PhysicalOnly =>
            "Preserve the pattern verbatim — it is the protection at work even though no known scheme was matched. " +
            "Do not 'repair' the affected sectors.",
        _ => "No special handling needed.",
    };
}
