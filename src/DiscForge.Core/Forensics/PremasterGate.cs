// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Convert;
using DiscForge.Core.Cue;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Forensics;

/// <summary>One master-readiness finding, with a severity and where it applies.</summary>
public sealed record PremasterFinding(LintSeverity Severity, string Where, string Message);

/// <summary>The go/no-go verdict on whether an image is ready to cut a glass master from.</summary>
public sealed record PremasterReport
{
    public required IReadOnlyList<PremasterFinding> Findings { get; init; }
    /// <summary>Total addressed program length in sectors (content + gaps), when derivable.</summary>
    public int ProgramSectors { get; init; }
    public Msf Runtime => Msf.FromSectors(Math.Max(0, ProgramSectors));

    public int Errors => Findings.Count(f => f.Severity == LintSeverity.Error);
    public int Warnings => Findings.Count(f => f.Severity == LintSeverity.Warning);

    /// <summary>No errors — the image clears the gate and may be sent to master.</summary>
    public bool ReadyToMaster => Errors == 0;

    public string Summary() => ReadyToMaster
        ? $"Master-ready — {Runtime} program, {Warnings} advisory warning(s)."
        : $"NOT master-ready — {Errors} blocking error(s), {Warnings} warning(s).";
}

/// <summary>
/// premaster-check — the go/no-go gate a mastering engineer runs before cutting a glass master. It folds
/// the Red Book structural audit (<see cref="RedBookAudit"/>) together with the two things that stop a
/// press run: the program must fit inside a pressable CD (the 74-minute nominal / 80-minute maximum Red
/// Book capacity, beyond which no standard disc can be cut), and every data track must be physically
/// intact — a single sector failing its EDC/ECC on a data track is a defect that would ship on every
/// copy. It also flags the master-hygiene items a commercial release wants (a media catalogue number, an
/// ISRC per track) as advisories, never blockers. The result is one verdict: ready, or a list of exactly
/// what disqualifies it.
///
/// It deliberately does NOT emit a DDP (Disc Description Protocol) fileset — that binary layout is DCA's
/// proprietary format, outside DiscForge's clean-room boundary; this gate works entirely from open
/// formats (a cue sheet and the image) and open Red Book capacity limits. It validates and reports, and
/// changes nothing.
/// </summary>
public static class PremasterGate
{
    /// <summary>Standard 2-second lead-in pregap the presser prepends before track 1.</summary>
    public const int LeadInSectors = 150;
    /// <summary>74:00:00 nominal Red Book program capacity, in sectors (74·60·75).</summary>
    public const int Capacity74Min = 74 * 60 * 75;   // 333,000
    /// <summary>80:00:00 maximum standard Red Book capacity, in sectors (80·60·75).</summary>
    public const int Capacity80Min = 80 * 60 * 75;   // 360,000

    /// <summary>Gate a disc for mastering. <paramref name="model"/>, when supplied, enables the runtime
    /// capacity and data-integrity checks (and its per-track lengths feed the structural audit); with a
    /// cue sheet alone, only the structural and hygiene checks run.</summary>
    public static PremasterReport Check(CueSheet cue, DiscModel? model = null)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var f = new List<PremasterFinding>();

        // ---- structural conformance (Red Book) ------------------------------
        IReadOnlyList<int>? lengths = null;
        if (model != null && model.Tracks.Count == cue.Tracks.Count)
            lengths = model.Tracks.Select(t => t.SectorCount).ToList();

        var audit = RedBookAudit.Check(cue, lengths);
        foreach (var a in audit.Findings)
            f.Add(new(a.Severity, "structure/" + a.Where, a.Message));

        // ---- runtime capacity ----------------------------------------------
        int programSectors = 0;
        if (model != null)
        {
            foreach (var t in model.Tracks)
                programSectors += t.PregapSectors + t.SectorCount;

            if (programSectors > Capacity80Min)
                f.Add(new(LintSeverity.Error, "capacity",
                    $"program is {Msf.FromSectors(programSectors)} — beyond the 80:00 Red Book maximum; not pressable as a standard CD."));
            else if (programSectors > Capacity74Min)
                f.Add(new(LintSeverity.Warning, "capacity",
                    $"program is {Msf.FromSectors(programSectors)} — over the 74:00 nominal; requires 80-minute stock, confirm the plant supports it."));
        }
        else
        {
            f.Add(new(LintSeverity.Info, "capacity",
                "no image supplied — runtime and data-integrity not checked (structural audit only)."));
        }

        // ---- data-track physical integrity ---------------------------------
        if (model != null)
        {
            for (int i = 0; i < model.Tracks.Count; i++)
            {
                var t = model.Tracks[i];
                bool rawData = t.Type is CueTrackType.Mode1_2352 or CueTrackType.Mode2_2352;
                if (!rawData || t.SectorSize != 2352) continue;

                int bad = 0;
                int n = t.SectorCount;
                for (int s = 0; s < n; s++)
                {
                    var sec = t.Data.AsSpan(s * 2352, 2352);
                    bool? ok = ValidateData(sec, t.Type);
                    if (ok == false) bad++;
                }
                if (bad > 0)
                    f.Add(new(LintSeverity.Error, $"track {t.Number}",
                        $"{bad:N0} data sector(s) fail EDC/ECC — a physical defect that would press onto every copy; re-rip before mastering."));
            }
        }

        // ---- master hygiene (advisory) -------------------------------------
        if (string.IsNullOrEmpty(cue.Catalog))
            f.Add(new(LintSeverity.Info, "hygiene",
                "no media catalogue number (MCN) — recommended for a commercial release."));
        bool anyAudio = cue.Tracks.Any(t => t.Type == CueTrackType.Audio);
        int audioNoIsrc = cue.Tracks.Count(t => t.Type == CueTrackType.Audio && string.IsNullOrEmpty(t.Isrc));
        if (anyAudio && audioNoIsrc > 0)
            f.Add(new(LintSeverity.Info, "hygiene",
                $"{audioNoIsrc} audio track(s) have no ISRC — recommended for a commercial release."));

        return new PremasterReport { Findings = f, ProgramSectors = programSectors };
    }

    public static string Render(PremasterReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var x in r.Findings)
            sb.AppendLine($"  [{x.Severity}] {x.Where}: {x.Message}");
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    /// <summary>EDC verdict for a raw data sector: true valid, false failing, null not checkable.</summary>
    private static bool? ValidateData(ReadOnlySpan<byte> sector, CueTrackType type)
    {
        if (sector.Length != 2352 || !HasSync(sector)) return null;
        byte mode = sector[15];
        if (mode == 1) return EdcEcc.VerifyMode1(sector).EdcOk;
        if (mode == 2)
        {
            // Mode 2 Form 2 (sub-header bit 0x20) carries no EDC to check.
            if ((sector[18] & 0x20) != 0) return null;
            return EdcEcc.VerifyMode2Form1(sector).EdcOk;
        }
        return null;
    }

    private static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0x00 || s[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }
}
