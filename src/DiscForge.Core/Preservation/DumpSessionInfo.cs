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
/// The "how", next to a hash manifest's "what": everything about the specific drive, engine and
/// settings that produced a dump or a burn — the exact tool version, the drive's vendor/model/firmware,
/// which read/write strategy was requested, retry counts, offset correction, speed, and so on.
///
/// A hash manifest proves a dump is byte-identical to what was recorded; it says nothing about HOW that
/// recording happened — which drive, what firmware, what retry policy, whether offset correction was
/// applied. That context routinely lives only in scrollback or a person's memory, and is the first thing
/// lost when a dump changes hands. ImgBurn has no equivalent at all: its log window is ephemeral and
/// per-session, never carried alongside the image it produced. This is deliberately the dumping-side
/// counterpart to <c>DiscForge.Core.Mmc.BurnCommandPlanner</c> (which previews a burn's exact commands
/// before they run) — this instead RECORDS what a real read or write actually used, after the fact.
///
/// Every field here is either already computed by DiscForge for its own purposes (drive capabilities
/// from <c>DriveCapabilities</c>, or a command's own flags) or supplied by the caller — nothing is
/// invented or inferred from hardware behavior this session can't observe. This is purely a metadata
/// carrier: it never touches drive I/O itself, so recording a session alongside a read or write is
/// always safe to add without risking the operation it describes.
/// </summary>
public sealed record DumpSessionInfo
{
    /// <summary>The command that produced this dump/burn, e.g. "read-disc", "burn-raw --engine spti".</summary>
    public required string Operation { get; init; }
    public required string ToolVersion { get; init; }
    public required DateTime UtcTimestamp { get; init; }

    public string? DriveVendor { get; init; }
    public string? DriveModel { get; init; }
    public string? DriveFirmware { get; init; }
    public string? DevicePath { get; init; }
    public string? MediaProfile { get; init; }

    /// <summary>Which backend actually executed the operation: "imapi2", "spti", "hdiutil",
    /// "growisofs+wodim", etc.</summary>
    public string? Engine { get; init; }
    public int? RetryCount { get; init; }
    public bool? ContinueOnError { get; init; }
    public int? ReadOffsetSamples { get; init; }
    public bool? C2Requested { get; init; }
    public bool? JitterCorrection { get; init; }
    public int? WriteSpeedMultiplier { get; init; }

    /// <summary>Any outcome worth recording that the fixed fields above don't cover, e.g.
    /// "3 sectors re-read", "offset auto-detected via AccurateRip".</summary>
    public string? Outcome { get; init; }

    /// <summary>Room for command-specific extras beyond the common fields above.</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }

    /// <summary>Flatten every non-null field to strings, in the shape
    /// <c>DumpLineageLog.Append(..., data: ...)</c> expects — so a session can be folded straight into
    /// an existing chain-of-custody lineage instead of only living in its own sidecar file.</summary>
    public IReadOnlyDictionary<string, string> ToLineageData()
    {
        var d = new Dictionary<string, string>
        {
            ["operation"] = Operation,
            ["toolVersion"] = ToolVersion,
            ["utc"] = UtcTimestamp.ToString("O"),
        };
        void Add(string key, object? value) { if (value is not null) d[key] = value.ToString()!; }
        Add("driveVendor", DriveVendor);
        Add("driveModel", DriveModel);
        Add("driveFirmware", DriveFirmware);
        Add("devicePath", DevicePath);
        Add("mediaProfile", MediaProfile);
        Add("engine", Engine);
        Add("retryCount", RetryCount);
        Add("continueOnError", ContinueOnError);
        Add("readOffsetSamples", ReadOffsetSamples);
        Add("c2Requested", C2Requested);
        Add("jitterCorrection", JitterCorrection);
        Add("writeSpeedMultiplier", WriteSpeedMultiplier);
        Add("outcome", Outcome);
        if (Extra is not null) foreach (var (k, v) in Extra) d[$"extra.{k}"] = v;
        return d;
    }
}

/// <summary>Reads and writes a <see cref="DumpSessionInfo"/> as a small JSON sidecar next to the image
/// it describes (<c>{image}.dumpsession.json</c>), and assembles one from a <c>DriveCapabilities</c> plus
/// a command's own settings.</summary>
public static class DumpSessionRecorder
{
    public const string SidecarSuffix = ".dumpsession.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The sidecar path for a given image/output path.</summary>
    public static string SidecarPath(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        return imagePath + SidecarSuffix;
    }

    public static string ToJson(DumpSessionInfo info) => JsonSerializer.Serialize(info, Options);

    public static DumpSessionInfo FromJson(string json)
        => JsonSerializer.Deserialize<DumpSessionInfo>(json, Options)
           ?? throw new ArgumentException("Empty or invalid dump-session record.");

    /// <summary>Write the sidecar JSON next to <paramref name="imagePath"/>. Never throws for a
    /// pre-existing sidecar — it is simply overwritten, since a session record describes the LATEST
    /// production of that exact file.</summary>
    public static void WriteSidecar(DumpSessionInfo info, string imagePath)
    {
        ArgumentNullException.ThrowIfNull(info);
        File.WriteAllText(SidecarPath(imagePath), ToJson(info));
    }

    public static DumpSessionInfo? ReadSidecar(string imagePath)
    {
        string path = SidecarPath(imagePath);
        return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
    }
}
