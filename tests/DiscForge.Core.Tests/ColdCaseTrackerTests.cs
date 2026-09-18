// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// ColdCaseTracker turns an INCOMPLETE dump into a time-aware retry reminder instead of a one-shot dead end.
/// These tests cover the actual decision logic: an entry isn't "due" until its not-before date has passed,
/// resolving a case removes it from the due list without deleting its history, and re-attempting a case that
/// isn't open (already resolved, or never added) is rejected rather than silently creating a phantom retry.
/// </summary>
public class ColdCaseTrackerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dforge-coldcase-" + Guid.NewGuid());
    private string RegistryPath => Path.Combine(_dir, "registry.json");

    public ColdCaseTrackerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void EntryWithPastNotBeforeIsDueImmediately()
    {
        var now = DateTime.UtcNow;
        ColdCaseTracker.Add(RegistryPath, "game.bin", "bad sectors on track 2", now, now.AddDays(-1));
        var due = ColdCaseTracker.Due(RegistryPath, now);
        Assert.Single(due);
        Assert.Equal("game.bin", due[0].Image);
    }

    [Fact]
    public void EntryWithFutureNotBeforeIsNotDueYet()
    {
        var now = DateTime.UtcNow;
        ColdCaseTracker.Add(RegistryPath, "game.bin", "lead-out overread failed", now, now.AddDays(30));
        Assert.Empty(ColdCaseTracker.Due(RegistryPath, now));
    }

    [Fact]
    public void EntryWithNoNotBeforeIsDueImmediately()
    {
        var now = DateTime.UtcNow;
        ColdCaseTracker.Add(RegistryPath, "game.bin", "unspecified damage", now);
        Assert.Single(ColdCaseTracker.Due(RegistryPath, now));
    }

    [Fact]
    public void ResolvingRemovesFromDueButKeepsHistory()
    {
        var now = DateTime.UtcNow;
        ColdCaseTracker.Add(RegistryPath, "game.bin", "bad sectors", now, now.AddDays(-1));
        ColdCaseTracker.RecordAttempt(RegistryPath, "game.bin", resolved: true, now, note: "re-read clean");

        Assert.Empty(ColdCaseTracker.Due(RegistryPath, now));
        var all = ColdCaseTracker.All(RegistryPath);
        var entry = Assert.Single(all);
        Assert.True(entry.Resolved);
        Assert.Equal(1, entry.Attempts);
        Assert.Equal("re-read clean", entry.Note);
    }

    [Fact]
    public void UnresolvedAttemptPushesNextRetryOut()
    {
        var now = DateTime.UtcNow;
        ColdCaseTracker.Add(RegistryPath, "game.bin", "bad sectors", now, now.AddDays(-1));
        ColdCaseTracker.RecordAttempt(RegistryPath, "game.bin", resolved: false, now, now.AddDays(14));

        Assert.Empty(ColdCaseTracker.Due(RegistryPath, now));
        Assert.Single(ColdCaseTracker.Due(RegistryPath, now.AddDays(15)));
        Assert.Equal(1, ColdCaseTracker.All(RegistryPath)[0].Attempts);
    }

    [Fact]
    public void AttemptingAnUntrackedImageThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ColdCaseTracker.RecordAttempt(RegistryPath, "never-added.bin", resolved: true, DateTime.UtcNow));
        Assert.Contains("never-added.bin", ex.Message);
    }

    [Fact]
    public void AttemptingAnAlreadyResolvedCaseThrowsRatherThanReopeningIt()
    {
        var now = DateTime.UtcNow;
        ColdCaseTracker.Add(RegistryPath, "game.bin", "bad sectors", now);
        ColdCaseTracker.RecordAttempt(RegistryPath, "game.bin", resolved: true, now);
        Assert.Throws<InvalidOperationException>(
            () => ColdCaseTracker.RecordAttempt(RegistryPath, "game.bin", resolved: true, now));
    }

    [Fact]
    public void ReAddingAnImageReplacesItsOpenEntryInsteadOfDuplicating()
    {
        var now = DateTime.UtcNow;
        ColdCaseTracker.Add(RegistryPath, "game.bin", "first reason", now);
        ColdCaseTracker.Add(RegistryPath, "game.bin", "second reason, re-flagged", now);

        var all = ColdCaseTracker.All(RegistryPath);
        var entry = Assert.Single(all);
        Assert.Equal("second reason, re-flagged", entry.Reason);
    }

    [Fact]
    public void RegistryPersistsAcrossLoadsOnDisk()
    {
        var now = DateTime.UtcNow;
        ColdCaseTracker.Add(RegistryPath, "game.bin", "bad sectors", now, now.AddDays(-1));
        Assert.True(File.Exists(RegistryPath));

        // A fresh call re-reads the file from disk rather than relying on any in-memory state.
        var due = ColdCaseTracker.Due(RegistryPath, now);
        Assert.Single(due);
    }
}
