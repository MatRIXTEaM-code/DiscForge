// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Files;

namespace DiscForge.Core.Raw;

/// <summary>
/// Recognises the common optical copy-protection schemes by their public,
/// documented fingerprints, so a faithful backup knows what it must preserve.
///
/// This is detection, NOT circumvention. DiscForge never bypasses, strips, or
/// defeats protection. It identifies a scheme the way CloneCD's read profiles
/// do — "this disc looks like SafeDisc; read its weak sectors as-is and keep
/// the subchannel verbatim" — so that a 1:1 image of an *unprotected-by-you*
/// or personally-owned disc reproduces the original faithfully rather than
/// silently "repairing" the very features that make it authentic.
///
/// Every signature used here is drawn from published descriptions of the
/// schemes' on-disc structure (file names, volume descriptors, subchannel
/// behaviour). None of it decrypts anything or enables playing a protected
/// disc without the original.
/// </summary>
public static class ProtectionScanner
{
    public enum Scheme
    {
        None,
        LibCrypt,      // Sony PlayStation (PSX) — corrupt subchannel Q fingerprint
        SafeDisc,      // Macrovision — 00000001.TMP / weak sectors
        SecuROM,       // Sony DADC — subchannel data + *.dat / *.sic markers
        SafeDiscLike,  // weak-sector pattern without the marker file
        Laserlock,     // Laserlock — LASERLOK dir / dummy sectors
        ProtectCd,     // ProtectCD — characteristic dummy files
        Unknown,       // anomalies present, scheme not identified
    }

    public sealed record Finding
    {
        public required Scheme Scheme { get; init; }
        public required string Evidence { get; init; }
        public required string Guidance { get; init; }
        /// <summary>Sectors the backup should treat as significant (weak /
        /// intentionally-corrupt / dummy), so it doesn't normalise them.</summary>
        public IReadOnlyList<long> SignificantLbas { get; init; } = Array.Empty<long>();
    }

    public sealed record Report
    {
        public required IReadOnlyList<Finding> Findings { get; init; }
        public bool AnyProtection => Findings.Count > 0 &&
            Findings.Any(f => f.Scheme != Scheme.None);
        public string Summary => AnyProtection
            ? string.Join("; ", Findings.Select(f => $"{f.Scheme}: {f.Evidence}"))
            : "No known protection fingerprint detected.";
    }

    // File-system markers, from published scheme descriptions. Presence of the
    // marker is a strong hint; we still cross-check structural signs.
    private static readonly (string Name, Scheme Scheme, string Note)[] MarkerFiles =
    {
        ("00000001.TMP", Scheme.SafeDisc,  "SafeDisc marker file present"),
        ("00000002.TMP", Scheme.SafeDisc,  "SafeDisc marker file present"),
        ("CLCD16.DLL",   Scheme.SafeDisc,  "SafeDisc loader DLL present"),
        ("CLCD32.DLL",   Scheme.SafeDisc,  "SafeDisc loader DLL present"),
        ("DPLAYERX.DLL", Scheme.SafeDisc,  "SafeDisc player DLL present"),
        ("SINTF32.DLL",  Scheme.SecuROM,   "SecuROM interface DLL present"),
        ("SINTF16.DLL",  Scheme.SecuROM,   "SecuROM interface DLL present"),
        ("SINTFNT.DLL",  Scheme.SecuROM,   "SecuROM interface DLL present"),
        ("CMS16.DLL",    Scheme.SecuROM,   "SecuROM (older) DLL present"),
        ("CMS_95.DLL",   Scheme.SecuROM,   "SecuROM (older) DLL present"),
        ("LASERLOK.IN",  Scheme.Laserlock, "Laserlock directory present"),
        ("LASERLOK.O10", Scheme.Laserlock, "Laserlock dummy file present"),
        ("LASERLOK.O11", Scheme.Laserlock, "Laserlock dummy file present"),
    };

    /// <summary>
    /// Scan an image for protection fingerprints. Reads a sample of sectors and
    /// the subchannel; does not require reading the whole disc.
    /// </summary>
    public static Report Scan(ISectorSource source, IReadOnlyList<string> fileNames)
    {
        var findings = new List<Finding>();

        // 1) File-system markers (cheap, from the caller's directory listing).
        var upper = fileNames.Select(f => f.ToUpperInvariant()).ToHashSet();
        var byScheme = new Dictionary<Scheme, List<string>>();
        foreach (var (name, scheme, note) in MarkerFiles)
        {
            if (upper.Contains(name) || upper.Any(f => f.EndsWith("\\" + name) || f.EndsWith("/" + name)))
                (byScheme.TryGetValue(scheme, out var l) ? l : byScheme[scheme] = new()).Add(note);
        }
        foreach (var (scheme, notes) in byScheme)
        {
            findings.Add(new Finding
            {
                Scheme = scheme,
                Evidence = string.Join(", ", notes.Distinct()),
                Guidance = GuidanceFor(scheme),
            });
        }

        // 2) Subchannel-based schemes (LibCrypt): reuse the existing analyser
        //    over whatever subcode the source can supply.
        var libcrypt = ScanSubchannel(source);
        if (libcrypt is not null) findings.Add(libcrypt);

        // 3) Structural weak-sector scan: SafeDisc/SecuROM deliberately place
        //    sectors that fail their EDC/ECC in a recognisable band. We sample
        //    and look for a *cluster* of EDC failures in Mode 1/Mode 2 Form 1,
        //    which — combined or not with a marker — signals weak sectors.
        var weak = ScanWeakSectors(source);
        if (weak is not null) findings.Add(weak);

        return new Report { Findings = findings };
    }

    private static Finding? ScanSubchannel(ISectorSource source)
    {
        // Walk the source's sectors, collecting subcode where present, and run
        // the LibCrypt fingerprint test. Cheap: we only need the Q frames.
        var invalid = new List<long>();
        long frames = 0;
        long limit = Math.Min(source.TotalSectors, 20_000);
        Span<byte> q = stackalloc byte[12];
        for (long i = 0; i < limit; i++)
        {
            SectorAccess.SectorData s;
            try { s = source.Read(i); } catch { break; }
            if (s.Subcode is null || s.Subcode.Length < RawSubchannel.FrameSize) continue;
            frames++;
            RawSubchannel.ExtractQ(s.Subcode, q);
            if (!RawSubchannel.QCrcValid(q)) invalid.Add(s.Lba);
        }
        if (frames == 0) return null;

        // LibCrypt signature: a small, non-zero count of corrupt Q frames in an
        // otherwise-valid stream (the real schemes corrupt ~16 in two bands).
        if (invalid.Count is > 0 and <= 64)
        {
            return new Finding
            {
                Scheme = Scheme.LibCrypt,
                Evidence = $"{invalid.Count} intentionally-corrupt subchannel Q frame(s)",
                Guidance = "Copy with subchannel-faithful (verbatim) mode so the "
                         + "fingerprint is preserved; do not let the drive/tool "
                         + "'repair' the Q subchannel.",
                SignificantLbas = invalid,
            };
        }
        return null;
    }

    private static Finding? ScanWeakSectors(ISectorSource source)
    {
        // Sample Mode 1 / Mode 2 Form 1 sectors and count EDC mismatches. A
        // cluster (not scattered singletons — those are read errors) is the
        // weak-sector fingerprint used by SafeDisc-family schemes.
        var edcFails = new List<long>();
        long limit = Math.Min(source.TotalSectors, 30_000);
        long step = Math.Max(1, limit / 6_000);   // sample up to ~6k sectors
        for (long i = 0; i < limit; i += step)
        {
            SectorAccess.SectorData s;
            try { s = source.Read(i); } catch { break; }
            if (s.Stored.Length < 2352) continue;
            // Only Mode 1 sectors carry the EDC/ECC we can verify. The sync +
            // mode byte tells us; VerifyMode1 returns whether EDC holds.
            if (s.Stored[15] != 0x01) continue;   // mode byte; 01 = Mode 1
            var (edcOk, _) = EdcEcc.VerifyMode1(s.Stored);
            if (edcOk) continue;
            edcFails.Add(s.Lba);
        }

        // Require a cluster: several failures within a short LBA span.
        if (edcFails.Count >= 4 && SpanOf(edcFails) <= 2_000)
        {
            return new Finding
            {
                Scheme = Scheme.SafeDiscLike,
                Evidence = $"{edcFails.Count} clustered weak sectors (EDC intentionally invalid)",
                Guidance = "These sectors are meant to be unreadable/!EDC by design. "
                         + "Image in RAW mode and preserve them as read; do not "
                         + "regenerate EDC/ECC or drop the sectors.",
                SignificantLbas = edcFails,
            };
        }
        return null;
    }

    private static long SpanOf(IReadOnlyList<long> lbas)
    {
        long min = long.MaxValue, max = long.MinValue;
        foreach (var l in lbas) { if (l < min) min = l; if (l > max) max = l; }
        return max - min;
    }

    private static string GuidanceFor(Scheme s) => s switch
    {
        Scheme.SafeDisc =>
            "SafeDisc places weak sectors and a marker file. For a faithful "
          + "backup of a disc you own, image in RAW mode so the weak sectors "
          + "are preserved as-is; DiscForge does not defeat the protection.",
        Scheme.SecuROM =>
            "SecuROM stores data in the subchannel and uses marker DLLs. Preserve "
          + "the subchannel verbatim; DiscForge images but does not bypass it.",
        Scheme.Laserlock =>
            "Laserlock uses a dummy directory and unreadable sectors. A RAW image "
          + "preserves the structure; DiscForge does not circumvent it.",
        _ => "Preserve the disc structure verbatim in a RAW image.",
    };
}
