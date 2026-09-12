// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text.Json;
using System.Text.Json.Serialization;
using DiscForge.Core.Reading;

namespace DiscForge.Core.Preservation;

/// <summary>
/// A resume checkpoint for a CD track rip (<c>dforge read-cdi --resume</c>), at TRACK granularity —
/// simpler than <see cref="DumpCheckpoint"/>'s sector-level resume for a flat cooked ISO, because a
/// CDI image writes its track-data region first and its descriptor/trailer only once every track is
/// known (see <c>CdiWriter.Write</c>): there is no mid-track byte offset to resume FROM in the final
/// file the way there is for a flat ISO. What CAN be resumed cheaply is whichever whole tracks
/// already read cleanly on a previous attempt — often most of a multi-track disc, when only one
/// stubborn track stalled the rip — so the caller captures each track to its own temp file first and
/// assembles the final CDI only once every track is in hand, reusing a temp file whose size still
/// matches what the track ought to be instead of re-reading it.
/// </summary>
public sealed record CdiRipCheckpoint
{
    /// <summary>A deterministic fingerprint of the read plan this checkpoint was made against — see
    /// <see cref="ComputePlanSignature"/>. A resume is refused outright if this doesn't match the
    /// freshly-read plan: that means a different disc (or a different raw/cooked choice) is in the
    /// drive now, and reusing temp files captured from something else would silently corrupt the
    /// image rather than merely waste a re-read.</summary>
    public required string PlanSignature { get; init; }

    /// <summary>Track numbers whose temp file already holds a complete, previously-successful
    /// capture.</summary>
    public required IReadOnlyList<int> CompletedTracks { get; init; }

    public DateTime UtcTimestamp { get; init; } = DateTime.UtcNow;
}

public static class CdiRipCheckpointRecorder
{
    public const string SidecarSuffix = ".ripstate.json";

    public static string SidecarPath(string outPath) => outPath + SidecarSuffix;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(CdiRipCheckpoint cp) => JsonSerializer.Serialize(cp, JsonOpts);

    public static CdiRipCheckpoint FromJson(string json) =>
        JsonSerializer.Deserialize<CdiRipCheckpoint>(json, JsonOpts)
        ?? throw new InvalidDataException("Not a CDI rip checkpoint.");

    public static void WriteSidecar(CdiRipCheckpoint cp, string outPath) =>
        File.WriteAllText(SidecarPath(outPath), ToJson(cp));

    /// <summary>Null if no checkpoint exists for this output path.</summary>
    public static CdiRipCheckpoint? ReadSidecar(string outPath)
    {
        string path = SidecarPath(outPath);
        return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
    }

    public static void DeleteSidecar(string outPath)
    {
        string path = SidecarPath(outPath);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// A deterministic, human-inspectable fingerprint of a read plan: every track's number, start
    /// LBA, sector count, CDI mode and sector size, in plan order. Two plans with the same signature
    /// address the drive identically, sector for sector — the only thing a resumed rip needs to know
    /// before trusting a previous attempt's temp files. Any change to the disc (different TOC),
    /// the raw/cooked choice, or a different sector-mode probe result changes the signature, so a
    /// mismatched resume is refused rather than silently mixing sectors from two different reads.
    /// </summary>
    public static string ComputePlanSignature(ReadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return string.Join("|", plan.Tracks.Select(t =>
            $"{t.Number}:{t.StartLba}:{t.LengthSectors}:{t.Mode}:{(int)t.SectorSize}"));
    }
}
