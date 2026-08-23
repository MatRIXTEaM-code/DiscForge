// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscForge.Core.Devices;

/// <summary>One observed behaviour of a physical drive, timestamped and sourced.
/// <see cref="Value"/> carries the numeric payload for categories that have one
/// (an offset in samples, an LBA reached).</summary>
public sealed record DriveObservation(string WhenUtc, string Category, string Detail,
                                      long? Value = null, string? Source = null);

/// <summary>
/// The institutional memory a session's terminal scrollback used to eat: a
/// per-drive dossier that ACCUMULATES observed behaviour across operations.
/// <see cref="DriveKnowledgeBase"/> is the community's reference data — the
/// seed, fixed at compile time; the dossier is what THIS drive actually did on
/// THIS bench: the mute signature it pulled, the C2 pointers that cried wolf,
/// the offset a real AccurateRip confirmation pinned, how deep its overread
/// reaches. Observations distil into counters and facts; the facts become
/// warnings the tooling can show BEFORE the next dump repeats a hard lesson.
///
/// Categories with distilled meaning: "mute" (audio-as-data zero-fill with
/// SUCCESS status), "c2-first-sector" (C2 flags opening a span), "offset"
/// (Value = confirmed samples), "leadout-overread" / "leadin-reach" (Value =
/// LBA reached), anything else is a free note.
/// </summary>
public sealed record DriveDossier
{
    public string FormatVersion => "ddossier/1";
    public required string Vendor { get; init; }
    public required string Model { get; init; }
    /// <summary>Last firmware seen — a change resets nothing (history is history)
    /// but is itself worth an observation.</summary>
    public string? Firmware { get; init; }
    public IReadOnlyList<DriveObservation> Observations { get; init; } = Array.Empty<DriveObservation>();

    // ---- distilled facts ---------------------------------------------------
    public int MuteSignatureCount { get; init; }
    public int C2FirstSectorFlags { get; init; }
    public int? ConfirmedOffsetSamples { get; init; }
    public long? MaxLeadOutOverreadLba { get; init; }
    public long? MinLeadInLba { get; init; }

    public const string CategoryMute = "mute";
    public const string CategoryC2FirstSector = "c2-first-sector";
    public const string CategoryOffset = "offset";
    public const string CategoryLeadOutOverread = "leadout-overread";
    public const string CategoryLeadInReach = "leadin-reach";

    /// <summary>Append an observation and update the distilled facts it feeds.</summary>
    public DriveDossier Observe(DriveObservation obs)
    {
        ArgumentNullException.ThrowIfNull(obs);
        var next = this with { Observations = Observations.Append(obs).ToList() };
        return obs.Category switch
        {
            CategoryMute => next with { MuteSignatureCount = MuteSignatureCount + 1 },
            CategoryC2FirstSector => next with { C2FirstSectorFlags = C2FirstSectorFlags + 1 },
            CategoryOffset when obs.Value is { } v => next with { ConfirmedOffsetSamples = (int)v },
            CategoryLeadOutOverread when obs.Value is { } v =>
                next with { MaxLeadOutOverreadLba = Math.Max(MaxLeadOutOverreadLba ?? long.MinValue, v) },
            CategoryLeadInReach when obs.Value is { } v =>
                next with { MinLeadInLba = Math.Min(MinLeadInLba ?? long.MaxValue, v) },
            _ => next,
        };
    }

    /// <summary>The lessons, as warnings the next operation should see.</summary>
    public IReadOnlyList<string> Warnings(DriveReference? seed = null)
    {
        var w = new List<string>();
        if (MuteSignatureCount > 0)
            w.Add($"OBSERVED {MuteSignatureCount}×: zero-mutes audio-read-as-data with SUCCESS status — " +
                  "never accept a raw data read that lost sync (keep the sync gate on).");
        if (C2FirstSectorFlags >= 3)
            w.Add($"OBSERVED {C2FirstSectorFlags}×: C2 flags the first sector of read spans — " +
                  "opening-sector C2 from this drive is noise; consider --no-c2 and trust EDC + sync instead.");
        if (ConfirmedOffsetSamples is { } off && seed?.ReadOffsetSamples is { } refOff && off != refOff)
            w.Add($"confirmed offset {off:+#;-#;0} disagrees with the reference {refOff:+#;-#;0} — " +
                  "trust the confirmation (this drive, this bench), and re-run detect-offset to be sure.");
        return w;
    }

    public string Render(DriveReference? seed = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Vendor} {Model}{(Firmware is not null ? $" (fw {Firmware})" : "")} — local dossier");
        sb.AppendLine($"  observations : {Observations.Count}");
        if (ConfirmedOffsetSamples is { } off) sb.AppendLine($"  offset       : {off:+#;-#;0} samples (CONFIRMED on this bench)");
        else if (seed?.ReadOffsetSamples is { } r) sb.AppendLine($"  offset       : {r:+#;-#;0} samples (reference only — unconfirmed here)");
        if (MuteSignatureCount > 0) sb.AppendLine($"  mute events  : {MuteSignatureCount}");
        if (C2FirstSectorFlags > 0) sb.AppendLine($"  C2 first-sector flags: {C2FirstSectorFlags}");
        if (MaxLeadOutOverreadLba is { } lo) sb.AppendLine($"  overread     : lead-out to LBA {lo:N0}");
        if (MinLeadInLba is { } li) sb.AppendLine($"  lead-in reach: LBA {li:N0}");
        foreach (var warn in Warnings(seed)) sb.AppendLine($"  ! {warn}");
        foreach (var o in Observations.TakeLast(8))
            sb.AppendLine($"    {o.WhenUtc}  [{o.Category}] {o.Detail}{(o.Source is not null ? $"  ({o.Source})" : "")}");
        if (Observations.Count > 8) sb.AppendLine($"    … {Observations.Count - 8} earlier observation(s) in the file");
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static DriveDossier Load(string path) =>
        JsonSerializer.Deserialize<DriveDossier>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidDataException($"'{path}' is not a drive dossier.");
}

/// <summary>Where dossiers live and how they are found.</summary>
public static class DriveDossierStore
{
    /// <summary>Per-user default: &lt;ApplicationData&gt;/DiscForge/drives.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "DiscForge", "drives");

    public static string PathFor(string directory, string vendor, string model)
    {
        string name = DriveKnowledgeBase.Normalize($"{vendor} {model}").Replace(' ', '_');
        if (name.Length == 0) name = "unknown";
        return Path.Combine(directory, name + ".json");
    }

    /// <summary>Load the drive's dossier, or start a fresh one. A dossier that
    /// fails to parse is NOT silently replaced — corruption should be seen.</summary>
    public static DriveDossier LoadOrNew(string directory, string vendor, string model, string? firmware = null)
    {
        string path = PathFor(directory, vendor, model);
        if (File.Exists(path))
        {
            var d = DriveDossier.Load(path);
            return firmware is not null && d.Firmware != firmware ? d with { Firmware = firmware } : d;
        }
        return new DriveDossier { Vendor = vendor, Model = model, Firmware = firmware };
    }

    public static void Save(string directory, DriveDossier dossier) =>
        dossier.Save(PathFor(directory, dossier.Vendor, dossier.Model));
}
