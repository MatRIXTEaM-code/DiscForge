// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Recovery;

/// <summary>What the controller decides to do next for a stubborn sector.</summary>
public enum RereadAction
{
    /// <summary>The sector is recovered (a read validated, or consensus now covers every byte).</summary>
    Accept,
    /// <summary>Read it again with the current strategy — progress is still being made.</summary>
    ReadAgain,
    /// <summary>The current strategy has stalled; move to the next one (slower speed, C2, etc.).</summary>
    SwitchStrategy,
    /// <summary>Every strategy is exhausted and the sector still won't come clean.</summary>
    GiveUp,
}

/// <summary>The outcome of one read attempt of a sector under a given strategy.</summary>
/// <param name="Strategy">Which strategy index produced this read (0 = first/default).</param>
/// <param name="EdcValid">True when this read validated on its own.</param>
/// <param name="UncertainBytes">Bytes still not vouched for after merging every read so far
/// (0 with a data sector means consensus already covers the whole sector).</param>
public sealed record ReadAttempt(int Strategy, bool EdcValid, int UncertainBytes);

/// <summary>Tuning for the controller — a policy, not magic numbers scattered in code.</summary>
public sealed record AdaptiveRereadConfig
{
    /// <summary>Hard cap on reads within a single strategy before it must escalate.</summary>
    public int MaxReadsPerStrategy { get; init; } = 8;
    /// <summary>How many consecutive reads without a new best (fewer uncertain bytes) counts as a
    /// plateau — the signal to switch strategy rather than keep hammering the same one.</summary>
    public int PlateauReads { get; init; } = 3;
    /// <summary>How many strategies exist (default, then progressively more aggressive ones).</summary>
    public int StrategyCount { get; init; } = 3;
}

/// <summary>The result of driving a sector to completion.</summary>
public sealed record RereadRun
{
    public required bool Recovered { get; init; }
    public required int TotalReads { get; init; }
    public required int StrategiesUsed { get; init; }
    public required IReadOnlyList<ReadAttempt> History { get; init; }
}

/// <summary>A source of simulated (or, on hardware, real) reads for one sector.</summary>
public interface IRereadSource
{
    /// <summary>Read the sector once under <paramref name="strategy"/> and report the outcome.</summary>
    ReadAttempt Read(int strategy);
}

/// <summary>
/// The adaptive re-read controller (Tier A: the deterministic logic, no hardware). Given a stubborn
/// sector's read history it decides the next move — read again, escalate to a more aggressive
/// strategy, accept, or give up — so a dumper spends effort where it pays and stops when it won't.
/// The rule set: accept the moment a read validates or consensus covers every byte; keep re-reading
/// a strategy while it is still lowering the uncertain-byte count; when it plateaus (no new best for
/// <see cref="AdaptiveRereadConfig.PlateauReads"/> reads) or hits its read cap, switch to the next
/// strategy; when the last strategy is spent, give up. It is a pure function of the read history, so
/// it is proven here against a simulated flaky-sector model and drops onto a real drive unchanged
/// (Tier B wires the strategies to actual READ CD speed/flags). Read-side only; it defeats nothing.
/// </summary>
public static class AdaptiveReread
{
    /// <summary>Decide the next action from the reads so far. Pure — same history, same decision.</summary>
    public static RereadDecision Decide(IReadOnlyList<ReadAttempt> history, AdaptiveRereadConfig config)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(config);

        if (history.Count == 0)
            return new RereadDecision(RereadAction.ReadAgain, "first read");

        var last = history[^1];
        if (last.EdcValid) return new RereadDecision(RereadAction.Accept, "a read validated (EDC ok)");
        if (last.UncertainBytes == 0)
            return new RereadDecision(RereadAction.Accept, "consensus now covers every byte");

        int strategy = last.Strategy;
        var thisStrategy = history.Where(h => h.Strategy == strategy).Select(h => h.UncertainBytes).ToList();
        int n = thisStrategy.Count;

        bool escalate;
        string reason;
        if (n >= config.MaxReadsPerStrategy)
        {
            escalate = true;
            reason = $"strategy {strategy} hit its {config.MaxReadsPerStrategy}-read cap";
        }
        else if (n >= config.PlateauReads && !ImprovedInWindow(thisStrategy, config.PlateauReads))
        {
            escalate = true;
            reason = $"strategy {strategy} plateaued ({config.PlateauReads} reads with no new best)";
        }
        else
        {
            escalate = false;
            reason = $"strategy {strategy} still improving ({last.UncertainBytes} uncertain byte(s) left)";
        }

        if (!escalate) return new RereadDecision(RereadAction.ReadAgain, reason);
        if (strategy + 1 < config.StrategyCount)
            return new RereadDecision(RereadAction.SwitchStrategy, reason + " → escalating");
        return new RereadDecision(RereadAction.GiveUp, reason + "; no strategy left");
    }

    /// <summary>Drive a sector to Accept or GiveUp against a read source, applying the policy.</summary>
    public static RereadRun Run(IRereadSource source, AdaptiveRereadConfig config)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(config);

        var history = new List<ReadAttempt>();
        // A generous overall backstop so a mis-specified policy can't loop forever.
        int backstop = Math.Max(1, config.StrategyCount) * Math.Max(1, config.MaxReadsPerStrategy) + 4;

        while (history.Count <= backstop)
        {
            var decision = Decide(history, config);
            switch (decision.Action)
            {
                case RereadAction.Accept:
                    return Finish(history, recovered: true);
                case RereadAction.GiveUp:
                    return Finish(history, recovered: false);
                case RereadAction.ReadAgain:
                    history.Add(source.Read(history.Count == 0 ? 0 : history[^1].Strategy));
                    break;
                case RereadAction.SwitchStrategy:
                    history.Add(source.Read(history[^1].Strategy + 1));
                    break;
            }
        }
        return Finish(history, recovered: false);
    }

    private static RereadRun Finish(List<ReadAttempt> history, bool recovered) => new()
    {
        Recovered = recovered,
        TotalReads = history.Count,
        StrategiesUsed = history.Count == 0 ? 0 : history.Select(h => h.Strategy).Distinct().Count(),
        History = history,
    };

    // True when the last `window` reads set a new minimum uncertain-byte count vs everything before them.
    private static bool ImprovedInWindow(IReadOnlyList<int> uncertain, int window)
    {
        int n = uncertain.Count;
        if (n < window) return true;                    // too early to call a plateau
        int bestBefore = int.MaxValue;
        for (int i = 0; i < n - window; i++) bestBefore = Math.Min(bestBefore, uncertain[i]);
        int bestInWindow = int.MaxValue;
        for (int i = n - window; i < n; i++) bestInWindow = Math.Min(bestInWindow, uncertain[i]);
        return bestInWindow < bestBefore;
    }
}

/// <summary>The controller's next move, with a human-readable reason for the dump log.</summary>
public sealed record RereadDecision(RereadAction Action, string Reason);
