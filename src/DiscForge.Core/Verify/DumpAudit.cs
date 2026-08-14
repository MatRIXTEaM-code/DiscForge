// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Dat;
using DiscForge.Core.Forensics;
using DiscForge.Core.Preservation;
using DiscForge.Core.Raw;
using DiscForge.Core.Recovery;
using DiscForge.Core.Redump;

namespace DiscForge.Core.Verify;

/// <summary>The one-line answer.</summary>
public enum DumpQuality { Good, Suspect, Bad }

public enum CheckStatus { Pass, Warn, Fail, Info }

/// <summary>One named check, its status, and the specific tell behind it — the thing a wall of logs never says plainly.</summary>
public sealed record AuditCheck(string Name, CheckStatus Status, string Tell);

public sealed record DumpVerdict
{
    public required string Target { get; init; }
    public required DumpQuality Quality { get; init; }
    public required IReadOnlyList<AuditCheck> Checks { get; init; }

    public string Headline() => Quality switch
    {
        DumpQuality.Good => $"GOOD — {Target} looks like a trustworthy dump.",
        DumpQuality.Suspect => $"SUSPECT — {Target} may be fine, but something needs a look.",
        _ => $"BAD — {Target} is not a trustworthy dump.",
    };
}

/// <summary>
/// dump-audit — the plain-language answer to "is my dump actually good?". A dumper is handed a pile of .c2 /
/// .sub / _disc.txt logs and a plausible-looking image, and the single most common question in every
/// preservation forum is whether it can be trusted. This fuses the checks DiscForge already makes — structural
/// completeness, the unreadable-sector map, an EDC/ECC audit of the data sectors, the end-of-disc sectors that so
/// often hide a truncated read, pregap conformance, and (with a DAT) the Redump match — into ONE verdict with the
/// specific tell for each flag. Analysis only; it reads the dump and reports.
/// </summary>
public static class DumpAudit
{
    private const int RawSector = 2352;
    private const int SampleSectors = 400;

    public static DumpVerdict Audit(string path, DatFile? dat = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var checks = new List<AuditCheck>();
        string name = Path.GetFileName(path);
        bool isCue = Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase);

        // 1) Structure / completeness.
        CueSheet? sheet = null;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        if (isCue)
        {
            try
            {
                sheet = CueSheet.Parse(File.ReadAllText(path));
                var comp = DumpCompleteness.Check(path);
                if (!comp.AllBinsPresent)
                    checks.Add(new AuditCheck("structure", CheckStatus.Fail, "a track file referenced by the cue is missing."));
                else if (!comp.WholeSector)
                    checks.Add(new AuditCheck("structure", CheckStatus.Fail, "a track file is not a whole number of sectors — it is truncated or misaligned."));
                else
                    checks.Add(new AuditCheck("structure", CheckStatus.Pass, $"{comp.TrackCount} track(s), {comp.TotalSectors:N0} sectors; all bins present, whole-sector."));
            }
            catch (Exception ex) { checks.Add(new AuditCheck("structure", CheckStatus.Fail, "cue is unreadable: " + ex.Message)); }
        }
        else
        {
            checks.Add(new AuditCheck("structure", File.Exists(path) ? CheckStatus.Info : CheckStatus.Fail,
                File.Exists(path) ? "single image — cue/track structure not checked." : "file not found."));
        }

        // 2) Read completeness — the unreadable-sector map.
        var sidecar = BadSectorMap.SidecarPath(path);
        if (File.Exists(sidecar))
        {
            try
            {
                var bad = BadSectorMap.Load(sidecar);
                if (bad.DamagePresent)
                    checks.Add(new AuditCheck("read completeness", CheckStatus.Fail,
                        $"{bad.DamageCount} genuine unreadable sector(s) recorded — the dump is INCOMPLETE (zero-filled holes)."));
                else if (bad.Count > 0)
                    checks.Add(new AuditCheck("read completeness", CheckStatus.Warn,
                        $"{bad.Count} boundary hole(s) only — payload intact, but confirm they are pregap/run-out padding."));
                else
                    checks.Add(new AuditCheck("read completeness", CheckStatus.Pass, "no unreadable sectors recorded."));
            }
            catch { checks.Add(new AuditCheck("read completeness", CheckStatus.Info, "a bad-sector map is present but unreadable.")); }
        }
        else
            checks.Add(new AuditCheck("read completeness", CheckStatus.Info,
                "no bad-sector map beside the dump — no read holes recorded (not proof of a clean read)."));

        // 3) Data integrity — EDC/ECC audit of the raw data sectors.
        checks.Add(EdcAudit(sheet, dir, path, isCue));

        // 4) End-of-disc sectors — the classic hiding place for a truncated read.
        checks.Add(EndSectorCheck(sheet, dir, path, isCue));

        // 5) Pregap conformance.
        if (sheet is not null)
        {
            try
            {
                var pg = PregapConformance.Check(sheet);
                checks.Add(new AuditCheck("pregaps", pg.Conformant ? CheckStatus.Pass : CheckStatus.Warn,
                    pg.Conformant ? "pregaps follow PlayStation/Redump convention."
                                  : "pregaps deviate from convention — " + string.Join("; ", pg.Issues.Take(2))));
            }
            catch { /* not fatal */ }
        }

        // 6) Redump match.
        if (dat is not null && isCue)
        {
            try
            {
                BadSectorMap? bad = File.Exists(sidecar) ? SafeLoad(sidecar) : null;
                var diff = RedumpDiffer.Diff(path, dat, null, bad);
                if (diff.Match)
                    checks.Add(new AuditCheck("Redump match", CheckStatus.Pass, $"matches {diff.Game} byte-for-byte."));
                else if (diff.Identified)
                    checks.Add(new AuditCheck("Redump match", CheckStatus.Fail, diff.Summary() + " — run redump-diff for the cause."));
                else
                    checks.Add(new AuditCheck("Redump match", CheckStatus.Warn, "not found in the DAT — unverified against Redump."));
            }
            catch (Exception ex) { checks.Add(new AuditCheck("Redump match", CheckStatus.Warn, "could not diff: " + ex.Message)); }
        }
        else
            checks.Add(new AuditCheck("Redump match", CheckStatus.Warn,
                "no DAT supplied — the dump is unverified against a known-good reference (pass --dat)."));

        var quality = checks.Any(c => c.Status == CheckStatus.Fail) ? DumpQuality.Bad
                    : checks.Any(c => c.Status == CheckStatus.Warn) ? DumpQuality.Suspect
                    : DumpQuality.Good;

        return new DumpVerdict { Target = name, Quality = quality, Checks = checks };
    }

    private static AuditCheck EdcAudit(CueSheet? sheet, string dir, string path, bool isCue)
    {
        var dataBins = new List<string>();
        if (isCue && sheet is not null)
        {
            string? last = null;
            foreach (var t in sheet.Tracks)
            {
                if (string.Equals(t.File, last, StringComparison.Ordinal)) continue;
                last = t.File;
                var (_, size) = CueSheet.TypeToToken(t.Type);
                if (t.Type is CueTrackType.Mode1_2352 or CueTrackType.Mode2_2352 && size == RawSector)
                {
                    var full = Path.Combine(dir, t.File);
                    if (File.Exists(full)) dataBins.Add(full);
                }
            }
        }
        else if (!isCue && Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            dataBins.Add(path);

        if (dataBins.Count == 0)
            return new AuditCheck("data integrity (EDC/ECC)", CheckStatus.Info,
                "no raw 2352-byte data track to check (audio-only, or cooked 2048/2336 sectors carry no EDC).");

        int checkedCount = 0, failed = 0, firstFail = -1;
        foreach (var bin in dataBins)
        {
            long len = new FileInfo(bin).Length;
            if (len % RawSector != 0) continue;
            int sectors = (int)(len / RawSector);
            int step = Math.Max(1, sectors / SampleSectors);
            using var fs = File.OpenRead(bin);
            var buf = new byte[RawSector];
            for (int s = 0; s < sectors; s += step)
            {
                fs.Seek((long)s * RawSector, SeekOrigin.Begin);
                fs.ReadExactly(buf, 0, RawSector);
                var v = DumpMerge.Validate(buf);
                if (v is null) continue;             // audio / Form 2 — no EDC
                checkedCount++;
                if (v == false) { failed++; if (firstFail < 0) firstFail = s; }
            }
        }

        if (checkedCount == 0)
            return new AuditCheck("data integrity (EDC/ECC)", CheckStatus.Info, "sampled sectors carry no EDC to check.");
        if (failed > 0)
            return new AuditCheck("data integrity (EDC/ECC)", CheckStatus.Fail,
                $"{failed:N0} of {checkedCount:N0} sampled data sectors FAIL their EDC (first near sector {firstFail:N0}) — corrupted or wrong-offset data.");
        return new AuditCheck("data integrity (EDC/ECC)", CheckStatus.Pass,
            $"all {checkedCount:N0} sampled data sectors pass EDC.");
    }

    private static AuditCheck EndSectorCheck(CueSheet? sheet, string dir, string path, bool isCue)
    {
        // The last data-track file; its final sectors are where a truncated/failed read most often hides.
        string? bin = null;
        if (isCue && sheet is not null)
        {
            string? last = null;
            foreach (var t in sheet.Tracks)
            {
                if (string.Equals(t.File, last, StringComparison.Ordinal)) continue;
                last = t.File;
                if (t.Type is CueTrackType.Mode1_2352 or CueTrackType.Mode2_2352)
                {
                    var full = Path.Combine(dir, t.File);
                    if (File.Exists(full)) bin = full;
                }
            }
        }
        else if (!isCue && Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            bin = path;

        if (bin is null)
            return new AuditCheck("end sectors", CheckStatus.Info, "no raw data track to inspect at the tail.");

        long len = new FileInfo(bin).Length;
        if (len % RawSector != 0 || len < RawSector)
            return new AuditCheck("end sectors", CheckStatus.Info, "tail not sector-aligned.");

        int sectors = (int)(len / RawSector);
        int look = Math.Min(3, sectors);
        using var fs = File.OpenRead(bin);
        var buf = new byte[RawSector];
        int zeroFilled = 0, edcFail = 0;
        for (int s = sectors - look; s < sectors; s++)
        {
            fs.Seek((long)s * RawSector, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, RawSector);
            if (Array.TrueForAll(buf, b => b == 0)) zeroFilled++;
            else if (DumpMerge.Validate(buf) == false) edcFail++;
        }
        if (zeroFilled == look)
            return new AuditCheck("end sectors", CheckStatus.Warn,
                $"the final {look} data sector(s) are all-zero — a common sign of a truncated or failed end-of-disc read.");
        if (edcFail > 0)
            return new AuditCheck("end sectors", CheckStatus.Warn,
                $"{edcFail} of the final {look} data sector(s) fail EDC — errors in the end sectors (a known dumping pitfall).");
        return new AuditCheck("end sectors", CheckStatus.Pass, "the end-of-disc sectors read cleanly.");
    }

    private static BadSectorMap? SafeLoad(string p) { try { return BadSectorMap.Load(p); } catch { return null; } }
}
