// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Licensing;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>The 30-day trial's pure evaluation: start, countdown, expiry, and tamper handling.</summary>
public class TrialTests
{
    private const string Machine = "MACHINE-A";
    private static readonly DateTime T0 = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void First_run_starts_a_30_day_trial()
    {
        var (status, stored) = Trial.Evaluate(new string?[] { null, null }, Machine, T0);
        Assert.Equal(TrialState.Active, status.State);
        Assert.Equal(30, status.DaysLeft);
        Assert.Equal(T0, Trial.Parse(stored, Machine)!.StartUtc);
    }

    [Fact]
    public void Counts_down_and_expires_after_30_days()
    {
        var (_, stored) = Trial.Evaluate(new string?[] { null }, Machine, T0);
        var (day10, s10) = Trial.Evaluate(new[] { stored }, Machine, T0.AddDays(10));
        Assert.Equal(20, day10.DaysLeft);
        var (lastDay, _) = Trial.Evaluate(new[] { s10 }, Machine, T0.AddDays(29.5));
        Assert.True(lastDay.CanRun);
        Assert.Equal(1, lastDay.DaysLeft);
        var (after, _) = Trial.Evaluate(new[] { s10 }, Machine, T0.AddDays(30));
        Assert.Equal(TrialState.Expired, after.State);
        Assert.False(after.CanRun);
    }

    [Fact]
    public void Deleting_one_copy_does_not_reset_the_trial()
    {
        var (_, stored) = Trial.Evaluate(new string?[] { null }, Machine, T0);
        var (status, _) = Trial.Evaluate(new[] { null, stored }, Machine, T0.AddDays(31));
        Assert.Equal(TrialState.Expired, status.State);
    }

    [Fact]
    public void The_earliest_start_wins_when_copies_disagree()
    {
        var early = Trial.Format(new Trial.Record(T0, T0), Machine);
        var late = Trial.Format(new Trial.Record(T0.AddDays(20), T0.AddDays(20)), Machine);
        var (status, _) = Trial.Evaluate(new[] { late, early }, Machine, T0.AddDays(25));
        Assert.Equal(5, status.DaysLeft);
    }

    [Fact]
    public void Edited_or_copied_records_are_rejected()
    {
        var good = Trial.Format(new Trial.Record(T0, T0), Machine);
        var edited = good.Replace(T0.Ticks.ToString(), T0.AddDays(60).Ticks.ToString());
        Assert.Null(Trial.Parse(edited, Machine));
        Assert.Null(Trial.Parse(good, "MACHINE-B"));   // copied from another PC

        var (status, stored) = Trial.Evaluate(new[] { edited }, Machine, T0.AddDays(1));
        Assert.False(status.CanRun);
        // And the replacement record keeps it that way rather than handing out a new trial.
        var (next, _) = Trial.Evaluate(new[] { stored }, Machine, T0.AddDays(2));
        Assert.False(next.CanRun);
    }

    [Fact]
    public void Setting_the_clock_back_is_not_a_way_to_extend_the_trial()
    {
        var (_, stored) = Trial.Evaluate(new string?[] { null }, Machine, T0);
        var (_, later) = Trial.Evaluate(new[] { stored }, Machine, T0.AddDays(29));
        var (rolledBack, _) = Trial.Evaluate(new[] { later }, Machine, T0.AddDays(5));
        Assert.Equal(TrialState.Tampered, rolledBack.State);
        // A day's drift (time zones, a slightly-off clock) is tolerated.
        var (drift, _) = Trial.Evaluate(new[] { later }, Machine, T0.AddDays(28));
        Assert.Equal(TrialState.Active, drift.State);
    }
}
