// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscForge.Core.Preservation;

/// <summary>
/// A resume point for an in-progress sequential sector dump: how far a read got before it
/// stopped (interrupted, crashed, or the drive gave up), so `read-disc --resume` can pick up
/// from there instead of re-reading everything from LBA 0.
///
/// This is intentionally simple — a flat run of already-written sectors, not a sparse bitmap.
/// The read paths this supports (`DataDiscImager.ReadToIso`) are themselves strictly
/// sequential, so "everything before NextLba is on disk" is always true when the checkpoint
/// was written; a resume only needs the single number back.
///
/// <see cref="TotalSectors"/> and <see cref="BlockLengthBytes"/> are carried so a resume can
/// refuse to continue against the WRONG disc (different capacity) rather than silently
/// splicing two different images together.
/// </summary>
public sealed record DumpCheckpoint
{
    public required string Operation { get; init; }
    /// <summary>The next sector that has NOT yet been written — everything before it is
    /// already safely in the output file.</summary>
    public required uint NextLba { get; init; }
    public required uint TotalSectors { get; init; }
    public required int BlockLengthBytes { get; init; }

    public string? DriveVendor { get; init; }
    public string? DriveModel { get; init; }
    public int? RetryCount { get; init; }
    public bool? ContinueOnError { get; init; }
    public required DateTime UtcTimestamp { get; init; }

    /// <summary>True when this checkpoint is compatible with a disc of the given capacity —
    /// the guard against resuming one dump's checkpoint against a different disc.</summary>
    public bool MatchesCapacity(uint totalSectors, int blockLengthBytes)
        => TotalSectors == totalSectors && BlockLengthBytes == blockLengthBytes;
}

/// <summary>Reads and writes a <see cref="DumpCheckpoint"/> as a small JSON sidecar next to the
/// in-progress image (<c>{image}.resume.json</c>).</summary>
public static class DumpCheckpointRecorder
{
    public const string SidecarSuffix = ".resume.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SidecarPath(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        return imagePath + SidecarSuffix;
    }

    public static string ToJson(DumpCheckpoint cp) => JsonSerializer.Serialize(cp, Options);

    public static DumpCheckpoint FromJson(string json)
        => JsonSerializer.Deserialize<DumpCheckpoint>(json, Options)
           ?? throw new ArgumentException("Empty or invalid checkpoint.");

    /// <summary>Write (overwrite) the checkpoint. Called repeatedly during a dump — cheap, since
    /// the file is tiny — so the resume point is never far behind the actual write position.</summary>
    public static void WriteSidecar(DumpCheckpoint cp, string imagePath)
    {
        ArgumentNullException.ThrowIfNull(cp);
        File.WriteAllText(SidecarPath(imagePath), ToJson(cp));
    }

    public static DumpCheckpoint? ReadSidecar(string imagePath)
    {
        string path = SidecarPath(imagePath);
        return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
    }

    /// <summary>Remove the checkpoint — call this once a dump finishes, whether complete or
    /// deliberately given up on with --continue-on-error, since either way there is nothing
    /// left to resume.</summary>
    public static void DeleteSidecar(string imagePath)
    {
        string path = SidecarPath(imagePath);
        if (File.Exists(path)) File.Delete(path);
    }
}
