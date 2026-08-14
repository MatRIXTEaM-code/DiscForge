// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Diagnostics;
using System.Text;

namespace DiscForge.Core.Util;

/// <summary>How a parser handled one mutated input.</summary>
public enum FuzzOutcome : byte { Ok, Crash, Timeout }

/// <summary>A parser that misbehaved on a mutated input, with enough to reproduce it.</summary>
public sealed record FuzzFinding(
    string Probe, FuzzOutcome Outcome, string ExceptionType, string Message,
    int Iteration, string Mutation);

/// <summary>The result of a fuzzing run over a set of parser probes.</summary>
public sealed record FuzzReport
{
    public required int Iterations { get; init; }
    public required int ProbeCount { get; init; }
    public required int Runs { get; init; }
    public required IReadOnlyList<FuzzFinding> Findings { get; init; }

    public int Crashes => Findings.Count(f => f.Outcome == FuzzOutcome.Crash);
    public int Timeouts => Findings.Count(f => f.Outcome == FuzzOutcome.Timeout);
    public bool Clean => Findings.Count == 0;

    public string Summary() =>
        $"Fuzzed {ProbeCount} parser(s) × {Iterations} mutation(s) = {Runs:N0} runs — " +
        (Clean ? "no unclean failures (every input parsed or raised a clean format error)."
               : $"{Crashes} crash(es), {Timeouts} timeout(s).");
}

/// <summary>
/// fuzz-parsers — structure-aware robustness fuzzing of DiscForge's format parsers. With ~180 commands of
/// binary parsing over <i>untrusted</i> disc images, a parser that throws <c>IndexOutOfRange</c> or hangs
/// on a malformed image is a real reliability/security defect. This mutates a seed image (bit flips, field
/// zeroing, truncation, and — the high-yield one — corrupting the 4-byte little-endian length/offset fields
/// a parser trusts) and runs each parser, classifying the outcome: a clean parse or a <i>domain</i> format
/// exception is fine; an <c>IndexOutOfRange</c>/<c>NullReference</c>/<c>Overflow</c> or a timeout is a bug,
/// reported with the exact iteration + mutation so it reproduces. Deterministic (seeded), so re-runs match.
/// </summary>
public static class ParserFuzz
{
    /// <summary>An exception a parser is *allowed* to throw on garbage input — a clean, expected rejection.
    /// Anything else (index/null/overflow/arithmetic) is a parser defect worth fixing. DiscForge's own
    /// format-exception types (e.g. <c>PvrFormatException</c>, <c>IpBinFormatException</c>) are intentional
    /// rejections and count as clean.</summary>
    public static bool IsCleanRejection(Exception e) =>
        e is FormatException or
             InvalidDataException or
             System.Text.Json.JsonException or
             EndOfStreamException or
             NotSupportedException or
             InvalidOperationException or
             ArgumentException   // includes ArgumentOutOfRange thrown by explicit validation
        || e.GetType().FullName?.StartsWith("DiscForge", StringComparison.Ordinal) == true;

    // Genuinely bad: these mean the parser indexed past a buffer, hit a null it didn't guard, or overflowed.
    private static bool IsCrash(Exception e) => e is
        IndexOutOfRangeException or
        NullReferenceException or
        OverflowException or
        ArithmeticException or
        AccessViolationException;

    /// <summary>Fuzz each probe with <paramref name="iterations"/> mutations of the seed.</summary>
    public static FuzzReport Run(
        byte[] seed,
        IReadOnlyList<(string Name, Action<byte[]> Probe)> probes,
        int iterations = 500,
        int perRunTimeoutMs = 2000)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(probes);

        var findings = new List<FuzzFinding>();
        int runs = 0;

        foreach (var (name, probe) in probes)
        {
            for (int it = 0; it < iterations; it++)
            {
                var (mutated, kind) = Mutate(seed, it);
                runs++;
                var (outcome, exType, msg) = RunOne(probe, mutated, perRunTimeoutMs);
                if (outcome != FuzzOutcome.Ok)
                    findings.Add(new FuzzFinding(name, outcome, exType, msg, it, kind));
            }
        }

        return new FuzzReport
        {
            Iterations = iterations,
            ProbeCount = probes.Count,
            Runs = runs,
            Findings = findings,
        };
    }

    private static (FuzzOutcome, string, string) RunOne(Action<byte[]> probe, byte[] input, int timeoutMs)
    {
        Exception? caught = null;
        var t = new Thread(() =>
        {
            try { probe(input); }
            catch (Exception ex) { caught = ex; }
        }) { IsBackground = true };

        t.Start();
        if (!t.Join(timeoutMs))
            return (FuzzOutcome.Timeout, "timeout", $"exceeded {timeoutMs} ms");

        if (caught is null) return (FuzzOutcome.Ok, "", "");
        if (IsCrash(caught)) return (FuzzOutcome.Crash, caught.GetType().Name, Short(caught.Message));
        if (IsCleanRejection(caught)) return (FuzzOutcome.Ok, "", "");
        // Unknown exception type — treat as a crash so it's surfaced rather than silently ignored.
        return (FuzzOutcome.Crash, caught.GetType().Name, Short(caught.Message));
    }

    // Deterministic structure-aware mutation of the seed, selected by iteration index.
    private static (byte[] Data, string Kind) Mutate(byte[] seed, int iteration)
    {
        var rng = new Random(unchecked(iteration * 2654435761u.GetHashCode()) ^ iteration);
        var d = (byte[])seed.Clone();
        if (d.Length == 0) return (d, "empty");

        int choice = iteration % 6;
        switch (choice)
        {
            case 0:   // single bit flip
            {
                int p = rng.Next(d.Length);
                d[p] ^= (byte)(1 << rng.Next(8));
                return (d, $"bitflip@{p}");
            }
            case 1:   // random byte
            {
                int p = rng.Next(d.Length);
                d[p] = (byte)rng.Next(256);
                return (d, $"setbyte@{p}");
            }
            case 2:   // truncate
            {
                int len = rng.Next(d.Length);
                return (d[..len], $"truncate->{len}");
            }
            case 3:   // corrupt a 4-byte length/offset field to a huge value (the high-yield case)
            {
                int p = rng.Next(Math.Max(1, d.Length - 4));
                for (int i = 0; i < 4 && p + i < d.Length; i++) d[p + i] = 0xFF;
                return (d, $"maxfield@{p}");
            }
            case 4:   // zero a region
            {
                int p = rng.Next(d.Length);
                int len = Math.Min(rng.Next(64), d.Length - p);
                Array.Clear(d, p, len);
                return (d, $"zero@{p}+{len}");
            }
            default:  // set a 4-byte field to a moderately large value (offset just past EOF)
            {
                int p = rng.Next(Math.Max(1, d.Length - 4));
                uint v = (uint)(d.Length + rng.Next(1, 1 << 20));
                for (int i = 0; i < 4 && p + i < d.Length; i++) d[p + i] = (byte)(v >> (8 * i));
                return (d, $"oobfield@{p}");
            }
        }
    }

    private static string Short(string s) => s.Length <= 120 ? s : s[..117] + "…";

    public static string Render(FuzzReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder(r.Summary());
        foreach (var f in r.Findings.Take(20))
            sb.Append($"\n  [{f.Outcome}] {f.Probe}: {f.ExceptionType} — {f.Message}  (iter {f.Iteration}, {f.Mutation})");
        if (r.Findings.Count > 20) sb.Append($"\n  … and {r.Findings.Count - 20} more.");
        return sb.ToString();
    }
}
