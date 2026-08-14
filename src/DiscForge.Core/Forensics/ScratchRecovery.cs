// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Forensics;

/// <summary>What can physically be done about a damaged region.</summary>
public enum RecoveryOutlook : byte
{
    Corrected,          // audio: within CIRC's correction — inaudible
    Concealed,          // audio: beyond correction but interpolation conceals it — usually inaudible
    Lost,               // audio: beyond concealment — an audible dropout unless re-read
    DataRecoverable,    // data: no concealment, but ECC / re-read / reconstruct applies
    Preserve,           // a deliberate pattern — do not "recover" it
}

/// <summary>The recovery verdict for one damaged region.</summary>
public sealed record LesionAdvisory(int StartLba, int EndLba, int Sectors, bool IsAudio,
                                    RecoveryOutlook Outlook, string Action)
{
    public override string ToString()
        => $"LBA {StartLba}–{EndLba} ({Sectors} sector(s), {(IsAudio ? "audio" : "data")}): {Outlook} — {Action}";
}

/// <summary>Per-region recovery outlook for a whole disc's damage.</summary>
public sealed record RecoveryReport
{
    public required IReadOnlyList<LesionAdvisory> Advisories { get; init; }

    public bool AnyLost => Advisories.Any(a => a.Outlook == RecoveryOutlook.Lost);

    public string Summary()
    {
        if (Advisories.Count == 0) return "No damaged regions to assess.";
        int lost = Advisories.Count(a => a.Outlook == RecoveryOutlook.Lost);
        int conceal = Advisories.Count(a => a.Outlook == RecoveryOutlook.Concealed);
        return $"{Advisories.Count} damaged region(s): " +
               $"{Advisories.Count(a => a.Outlook == RecoveryOutlook.Corrected)} corrected, " +
               $"{conceal} concealed, {lost} audibly lost, " +
               $"{Advisories.Count(a => a.Outlook == RecoveryOutlook.DataRecoverable)} data-recoverable.";
    }
}

/// <summary>
/// Scratch recovery outlook — turn the physical-layer models into a practical verdict for a real dump.
/// Given a damaged region, it says what actually happens to it: an audio track rides on CIRC, so a small
/// burst is corrected outright, a larger one is concealed by interpolation, and only a big one is an
/// audible loss (the <see cref="CircRecovery"/> oracle decides the tier); a data track has no
/// concealment — its per-sector RSPC ECC may repair a single read, otherwise it needs a re-read or a
/// cross-read reconstruct; and a deliberate pattern is left alone. It joins the error-shape classifier
/// (<see cref="ErrorPatternForensics"/>) to the correction models so "this scratch spans N sectors"
/// becomes "corrected / concealed / re-read". Advisory only; it recovers nothing itself.
/// </summary>
public static class ScratchRecovery
{
    /// <summary>Audio bytes per sector ÷ CIRC frame (24 bytes) = 98 frames of audio per sector.</summary>
    public const int FramesPerSector = 2352 / 24;
    /// <summary>Modelled concealment ceiling: beyond a few sectors of consecutive loss, interpolation can
    /// no longer hide it. Approximate — real concealment depends on the material.</summary>
    public const int ConcealFrames = 3 * FramesPerSector;

    /// <summary>Assess a burst measured in CIRC frames on an audio track.</summary>
    public static RecoveryOutlook AssessAudioFrames(int burstFrames)
    {
        if (CircRecovery.AnalyzeBurst(burstFrames).FullyCorrectable) return RecoveryOutlook.Corrected;
        return burstFrames <= ConcealFrames ? RecoveryOutlook.Concealed : RecoveryOutlook.Lost;
    }

    /// <summary>Assess one damaged region, given its shape and whether it is on an audio track.</summary>
    public static LesionAdvisory Assess(int startLba, int endLba, ErrorPatternKind kind, bool isAudio)
    {
        int sectors = Math.Max(1, endLba - startLba + 1);

        if (kind == ErrorPatternKind.DeliberatePattern)
            return new LesionAdvisory(startLba, endLba, sectors, isAudio, RecoveryOutlook.Preserve,
                "a deliberate pattern (likely protection) — preserve verbatim; do not repair.");

        if (!isAudio)
            return new LesionAdvisory(startLba, endLba, sectors, isAudio, RecoveryOutlook.DataRecoverable,
                sectors <= 2
                    ? "single-read RSPC ECC may repair it; otherwise re-read or reconstruct from a second copy."
                    : "too large for single-read ECC alone — re-read (ideally a second drive) and reconstruct.");

        int frames = sectors * FramesPerSector;
        var outlook = AssessAudioFrames(frames);
        string action = outlook switch
        {
            RecoveryOutlook.Corrected => "CIRC corrects it in the drive — inaudible; the rip is faithful.",
            RecoveryOutlook.Concealed => "beyond CIRC correction, but interpolation conceals it — usually inaudible; a clean re-read is still preferable.",
            _ => "beyond concealment — an audible dropout; re-read (ideally a second drive) to recover it.",
        };
        return new LesionAdvisory(startLba, endLba, sectors, isAudio, outlook, action);
    }

    /// <summary>Advise on every lesion in an error-pattern report; <paramref name="isAudioAt"/> tells
    /// whether a given LBA is on an audio track.</summary>
    public static RecoveryReport Advise(ErrorPatternReport pattern, Func<int, bool> isAudioAt)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(isAudioAt);
        var list = pattern.Lesions
            .Select(l => Assess(l.Start, l.End, l.Kind, isAudioAt((l.Start + l.End) / 2)))
            .ToList();
        return new RecoveryReport { Advisories = list };
    }

    public static string Render(RecoveryReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var a in r.Advisories) sb.AppendLine($"  {a}");
        return sb.ToString().TrimEnd();
    }
}
