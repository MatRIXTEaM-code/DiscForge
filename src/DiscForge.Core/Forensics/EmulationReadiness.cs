// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Cue;

namespace DiscForge.Core.Forensics;

/// <summary>How ready a dump is to run in an emulator.</summary>
public enum EmuReadiness { Ready, ReadyWithCaveats, NotReady }

/// <summary>The weight of a single readiness finding.</summary>
public enum EmuSeverity { Ok, Note, Warning, Blocker }

/// <summary>One aspect of emulation readiness.</summary>
public sealed record EmuFinding(string Aspect, EmuSeverity Severity, string Detail);

/// <summary>The emulation-readiness verdict for a dump.</summary>
public sealed record EmuReadinessReport
{
    public required EmuReadiness Grade { get; init; }
    public required IReadOnlyList<EmuFinding> Findings { get; init; }

    public IEnumerable<EmuFinding> Blockers => Findings.Where(f => f.Severity == EmuSeverity.Blocker);
    public IEnumerable<EmuFinding> Warnings => Findings.Where(f => f.Severity == EmuSeverity.Warning);
    public IEnumerable<EmuFinding> Notes => Findings.Where(f => f.Severity == EmuSeverity.Note);

    public string Summary => Grade switch
    {
        EmuReadiness.Ready => "READY — this dump has what an emulator needs to run.",
        EmuReadiness.ReadyWithCaveats =>
            $"READY WITH CAVEATS — runnable, but {Warnings.Count()} thing(s) an accurate emulator may want are off.",
        _ => $"NOT READY — {Blockers.Count()} blocker(s) would stop an emulator loading this dump.",
    };
}

/// <summary>
/// emu-ready — report whether a disc dump carries what an emulator needs to actually run it, not just
/// whether the bytes are physically whole (that is <see cref="DumpCompleteness"/>'s job). It reconciles the
/// cue's track layout with the on-disk files and calls out the things that decide emulation: every referenced
/// track present and whole-sector; a bootable data track and whether it is raw (2352) or cooked (2048); CD-DA
/// audio tracks and their pregaps; and the subchannel a LibCrypt/SBI-protected title needs. It produces a
/// graded, human-readable readiness verdict. Read-only; it changes nothing and defeats no protection — it only
/// *notes* when protection data an emulator needs is absent.
/// </summary>
public static class EmulationReadiness
{
    /// <summary>Analyze a bin/cue on disk (loads the cue and reconciles it with the files).</summary>
    public static EmuReadinessReport Analyze(string cuePath)
    {
        ArgumentNullException.ThrowIfNull(cuePath);
        if (!File.Exists(cuePath)) throw new FileNotFoundException("Cue sheet not found.", cuePath);
        var cue = CueSheet.Parse(File.ReadAllText(cuePath));
        var completeness = DumpCompleteness.Check(cuePath);
        return Analyze(cue, completeness);
    }

    /// <summary>The pure core: grade readiness from a parsed cue and a completeness result. Testable
    /// without touching disk.</summary>
    public static EmuReadinessReport Analyze(CueSheet cue, DumpCompletenessResult completeness)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(completeness);
        var findings = new List<EmuFinding>();

        // 1. Files present and whole-sector — the hard prerequisite. A missing bin or a
        //    partial-sector file blocks loading in any emulator.
        if (!completeness.AllBinsPresent)
            findings.Add(new EmuFinding("files", EmuSeverity.Blocker,
                "a data file the cue references is missing — the emulator cannot mount the disc."));
        else if (!completeness.WholeSector)
            findings.Add(new EmuFinding("files", EmuSeverity.Blocker,
                "a data file is not a whole number of sectors — truncated, so tracks won't line up."));
        else
            findings.Add(new EmuFinding("files", EmuSeverity.Ok,
                $"all {completeness.BinFiles.Count} referenced file(s) present, whole-sector."));

        // 2. A bootable data track, and whether it is raw (2352) or cooked (2048).
        var dataTracks = cue.Tracks.Where(t => t.Type != CueTrackType.Audio).ToList();
        var audioTracks = cue.Tracks.Where(t => t.Type == CueTrackType.Audio).ToList();
        if (dataTracks.Count == 0)
        {
            findings.Add(new EmuFinding("boot", EmuSeverity.Note,
                "no data track — this is an audio CD (plays, but there is no program for an emulator to boot)."));
        }
        else
        {
            bool anyCooked = dataTracks.Any(t => t.Type is CueTrackType.Mode1_2048 or CueTrackType.Mode2_2336);
            if (anyCooked)
                findings.Add(new EmuFinding("data-mode", EmuSeverity.Warning,
                    "a data track is cooked (2048/2336) — it boots, but a raw 2352 dump preserves sync/EDC/ECC " +
                    "and the Mode-2 subheader an accurate emulator (and any subchannel/protection check) relies on."));
            else
                findings.Add(new EmuFinding("data-mode", EmuSeverity.Ok,
                    "data track is raw 2352 — full sector fidelity."));
        }

        // 3. CD-DA audio tracks and their pregaps. Games with Redbook audio need the audio tracks;
        //    an audio track that follows a data track normally opens with a 2-second pregap.
        if (audioTracks.Count > 0)
        {
            findings.Add(new EmuFinding("audio", EmuSeverity.Ok,
                $"{audioTracks.Count} CD-DA audio track(s) present — Redbook audio will play."));

            bool missingPregap = audioTracks.Any(t =>
                t.Pregap is null && t.Indices.All(i => i.Number != 0));
            // Only meaningful when audio follows data (mixed-mode); a pure audio CD's track 1 needs none.
            if (missingPregap && dataTracks.Count > 0)
                findings.Add(new EmuFinding("pregap", EmuSeverity.Note,
                    "an audio track has neither a PREGAP nor an INDEX 00 gap — if the master had a pause, its " +
                    "absence can shift audio timing in a strict emulator."));
        }

        // 4. Subchannel — LibCrypt/SBI-protected titles (some PS1, Saturn) need it. We can't detect
        //    protection here, so this is an honest note, not a verdict.
        if (!completeness.SubchannelPresent)
            findings.Add(new EmuFinding("subchannel", EmuSeverity.Note,
                "no subchannel (.sub) sidecar — most games need none, but a LibCrypt/subchannel-protected title " +
                "will fail its check without it (an SBI or .sub alongside the image supplies it)."));
        else if (!completeness.SubchannelMatches)
            findings.Add(new EmuFinding("subchannel", EmuSeverity.Warning,
                "the subchannel sidecar's length does not match the data — it may not align with the disc."));
        else
            findings.Add(new EmuFinding("subchannel", EmuSeverity.Ok,
                "subchannel present and covers every sector — LibCrypt-class protection is preserved."));

        // 5. Multi-bin layout — most emulators handle a multi-file cue, some prefer one bin.
        if (completeness.BinFiles.Count > 1)
            findings.Add(new EmuFinding("layout", EmuSeverity.Note,
                $"{completeness.BinFiles.Count} separate bin files — modern emulators read a multi-file cue fine; " +
                "a few older ones want a single bin (a merge is lossless)."));

        var grade = findings.Any(f => f.Severity == EmuSeverity.Blocker) ? EmuReadiness.NotReady
                  : findings.Any(f => f.Severity == EmuSeverity.Warning) ? EmuReadiness.ReadyWithCaveats
                  : EmuReadiness.Ready;

        return new EmuReadinessReport { Grade = grade, Findings = findings };
    }

    public static string Render(EmuReadinessReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder(r.Summary);
        foreach (var f in r.Findings.OrderByDescending(f => f.Severity))
        {
            string mark = f.Severity switch
            {
                EmuSeverity.Blocker => "✗", EmuSeverity.Warning => "!", EmuSeverity.Note => "·", _ => "✓",
            };
            sb.Append($"\n  {mark} [{f.Aspect}] {f.Detail}");
        }
        return sb.ToString();
    }
}
