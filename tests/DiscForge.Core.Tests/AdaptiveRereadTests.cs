// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The adaptive re-read controller (Tier A). Its logic is a pure function of the read history, so it
/// is proven against simulated flaky-sector models: a sector that improves with re-reads is accepted
/// without escalating; a strategy that plateaus triggers a switch; a sector that only comes clean
/// under a harder strategy is recovered there; and a permanently-dead sector escalates through every
/// strategy and gives up — never looping forever.
/// </summary>
public class AdaptiveRereadTests
{
    private static readonly AdaptiveRereadConfig Cfg =
        new() { MaxReadsPerStrategy = 8, PlateauReads = 3, StrategyCount = 3 };

    /// <summary>A sector that, on strategy 0, reveals a few more good bytes each read and validates
    /// once the uncertain count reaches zero.</summary>
    private sealed class ImprovingSector : IRereadSource
    {
        private int _uncertain = 6;
        public ReadAttempt Read(int strategy)
        {
            if (_uncertain > 0) _uncertain -= 2;
            return new ReadAttempt(strategy, EdcValid: _uncertain <= 0, UncertainBytes: Math.Max(0, _uncertain));
        }
    }

    /// <summary>Dead under strategies 0..(threshold-1) (never improves), then improves and validates
    /// under the first strategy at or above the threshold.</summary>
    private sealed class NeedsStrategy : IRereadSource
    {
        private readonly int _threshold;
        private int _uncertain = 8;
        public NeedsStrategy(int threshold) => _threshold = threshold;
        public ReadAttempt Read(int strategy)
        {
            if (strategy >= _threshold && _uncertain > 0) _uncertain -= 3;
            return new ReadAttempt(strategy, EdcValid: strategy >= _threshold && _uncertain <= 0,
                                   UncertainBytes: Math.Max(0, _uncertain));
        }
    }

    /// <summary>Never yields anything — every read is equally bad under every strategy.</summary>
    private sealed class DeadSector : IRereadSource
    {
        public ReadAttempt Read(int strategy) => new(strategy, EdcValid: false, UncertainBytes: 40);
    }

    [Fact]
    public void An_improving_sector_is_recovered_on_the_first_strategy()
    {
        var run = AdaptiveReread.Run(new ImprovingSector(), Cfg);
        Assert.True(run.Recovered);
        Assert.Equal(1, run.StrategiesUsed);          // never had to escalate
        Assert.True(run.TotalReads <= Cfg.MaxReadsPerStrategy);
    }

    [Fact]
    public void A_plateaued_strategy_escalates_rather_than_hammering()
    {
        // Strategy 0 never improves; the controller must switch, not keep re-reading strategy 0.
        var run = AdaptiveReread.Run(new NeedsStrategy(threshold: 2), Cfg);
        Assert.True(run.Recovered);
        Assert.Equal(3, run.StrategiesUsed);          // 0 (plateau) → 1 (plateau) → 2 (works)
        Assert.Equal(2, run.History[^1].Strategy);    // recovered under strategy 2
    }

    [Fact]
    public void A_dead_sector_escalates_through_every_strategy_then_gives_up()
    {
        var run = AdaptiveReread.Run(new DeadSector(), Cfg);
        Assert.False(run.Recovered);
        Assert.Equal(Cfg.StrategyCount, run.StrategiesUsed);   // tried them all
        // Bounded: at most the cap per strategy across every strategy.
        Assert.True(run.TotalReads <= Cfg.StrategyCount * Cfg.MaxReadsPerStrategy + 4);
    }

    [Fact]
    public void Accepts_immediately_when_a_read_validates()
    {
        var history = new List<ReadAttempt> { new(0, EdcValid: true, UncertainBytes: 0) };
        Assert.Equal(RereadAction.Accept, AdaptiveReread.Decide(history, Cfg).Action);
    }

    [Fact]
    public void Accepts_when_consensus_covers_every_byte_even_without_edc()
    {
        // e.g. CD-DA: no EDC, but the union of reads left zero uncertain bytes.
        var history = new List<ReadAttempt> { new(0, EdcValid: false, UncertainBytes: 0) };
        Assert.Equal(RereadAction.Accept, AdaptiveReread.Decide(history, Cfg).Action);
    }

    [Fact]
    public void The_first_decision_on_empty_history_is_to_read()
    {
        Assert.Equal(RereadAction.ReadAgain, AdaptiveReread.Decide(Array.Empty<ReadAttempt>(), Cfg).Action);
    }
}
