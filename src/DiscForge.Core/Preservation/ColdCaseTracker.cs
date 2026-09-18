// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscForge.Core.Preservation;

/// <summary>One incomplete dump being tracked for a future re-attempt.</summary>
public sealed record ColdCaseEntry
{
    /// <summary>The image path this entry is about, as given when added — the tracker doesn't require
    /// the file to still exist (a disc can be re-read into a fresh file next time).</summary>
    public required string Image { get; init; }
    public required string Reason { get; init; }
    public required string FirstFlaggedUtc { get; init; }
    /// <summary>Don't bother suggesting a retry before this date — no point re-reading a dying drive
    /// again tomorrow, or nagging about a disc that needs cleaning first.</summary>
    public string? NotBeforeUtc { get; init; }
    public int Attempts { get; init; }
    public string? LastAttemptUtc { get; init; }
    public string? Note { get; init; }
    public bool Resolved { get; init; }

    public string Summary()
    {
        if (Resolved) return $"{Image}: RESOLVED after {Attempts} attempt(s) — {Reason}";
        string due = NotBeforeUtc is { Length: > 0 } nb ? $", not before {nb}" : "";
        return $"{Image}: {Reason} (flagged {FirstFlaggedUtc}{due}, {Attempts} attempt(s) so far)";
    }
}

/// <summary>
/// A dump that comes back INCOMPLETE (holes recorded in a <see cref="BadSectorMap"/>) doesn't have to be a
/// one-shot failure — cleaning the disc, trying a different drive, or just a different day can change the
/// outcome, but nothing today reminds anyone to actually try again. This is a plain, JSON-backed registry of
/// "incomplete dumps worth revisiting," queryable for what's due, so a cold case doesn't just get forgotten.
/// It tracks nothing about HOW to re-read a disc (that's every other reading command's job) — only WHETHER
/// and WHEN someone should be reminded to.
/// </summary>
public static class ColdCaseTracker
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Registry
    {
        public string FormatVersion => "coldcase/1";
        public List<ColdCaseEntry> Entries { get; init; } = [];
    }

    private static Registry LoadRegistry(string path)
    {
        if (!File.Exists(path)) return new Registry();
        var reg = JsonSerializer.Deserialize<Registry>(File.ReadAllText(path), JsonOpts);
        return reg ?? new Registry();
    }

    private static void SaveRegistry(string path, Registry reg) =>
        File.WriteAllText(path, JsonSerializer.Serialize(reg, JsonOpts));

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>Add a new cold case, or replace an existing unresolved one for the same image path (adding
    /// again after another failed attempt is a normal flow, not an error).</summary>
    public static IReadOnlyList<ColdCaseEntry> Add(string registryPath, string image, string reason,
        DateTime nowUtc, DateTime? notBeforeUtc = null, string? note = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(image);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        var reg = LoadRegistry(registryPath);
        reg.Entries.RemoveAll(e => e.Image == image && !e.Resolved);
        reg.Entries.Add(new ColdCaseEntry
        {
            Image = image,
            Reason = reason,
            FirstFlaggedUtc = Iso(nowUtc),
            NotBeforeUtc = notBeforeUtc is { } nb ? Iso(nb) : null,
            Attempts = 0,
            Note = note,
        });
        SaveRegistry(registryPath, reg);
        return reg.Entries;
    }

    /// <summary>Record a retry attempt. Resolved=true closes the case out (the re-read succeeded);
    /// otherwise it bumps the attempt count and can push the next retry further out.</summary>
    public static IReadOnlyList<ColdCaseEntry> RecordAttempt(string registryPath, string image, bool resolved,
        DateTime nowUtc, DateTime? nextNotBeforeUtc = null, string? note = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(image);
        var reg = LoadRegistry(registryPath);
        int idx = reg.Entries.FindIndex(e => e.Image == image && !e.Resolved);
        if (idx < 0)
            throw new InvalidOperationException($"No open cold case for '{image}' in this registry.");

        var e = reg.Entries[idx];
        reg.Entries[idx] = e with
        {
            Attempts = e.Attempts + 1,
            LastAttemptUtc = Iso(nowUtc),
            Resolved = resolved,
            NotBeforeUtc = resolved ? e.NotBeforeUtc : (nextNotBeforeUtc is { } nb ? Iso(nb) : e.NotBeforeUtc),
            Note = note ?? e.Note,
        };
        SaveRegistry(registryPath, reg);
        return reg.Entries;
    }

    /// <summary>Every open (unresolved) case whose NotBeforeUtc has passed (or has none set at all).</summary>
    public static IReadOnlyList<ColdCaseEntry> Due(string registryPath, DateTime asOfUtc)
    {
        var reg = LoadRegistry(registryPath);
        return reg.Entries
            .Where(e => !e.Resolved && (e.NotBeforeUtc is null || DateTime.Parse(e.NotBeforeUtc,
                        null, System.Globalization.DateTimeStyles.RoundtripKind) <= asOfUtc))
            .ToList();
    }

    public static IReadOnlyList<ColdCaseEntry> All(string registryPath) => LoadRegistry(registryPath).Entries;
}
