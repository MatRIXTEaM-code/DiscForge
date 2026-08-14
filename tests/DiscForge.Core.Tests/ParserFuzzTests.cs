// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the parser-robustness fuzzer engine: a probe that only raises clean format errors is
/// reported clean, a probe that indexes past its buffer is caught as a crash (with a reproducible
/// iteration + mutation), and results are deterministic across runs.
/// </summary>
public class ParserFuzzTests
{
    private static byte[] Seed()
    {
        var b = new byte[2048];
        new Random(1).NextBytes(b);
        return b;
    }

    [Fact]
    public void A_probe_that_only_raises_clean_format_errors_is_reported_clean()
    {
        var probes = new List<(string, Action<byte[]>)>
        {
            ("robust", b => { if (b.Length < 8 || b[0] != 0x42) throw new FormatException("bad magic"); }),
        };
        var r = ParserFuzz.Run(Seed(), probes, iterations: 150);
        Assert.True(r.Clean);
        Assert.Equal(0, r.Crashes);
    }

    [Fact]
    public void A_probe_that_indexes_past_its_buffer_is_caught_as_a_crash()
    {
        var probes = new List<(string, Action<byte[]>)>
        {
            ("buggy", b => { int len = BitConverter.ToInt32(b, 0); _ = b[len]; }),
        };
        var r = ParserFuzz.Run(Seed(), probes, iterations: 200);
        Assert.True(r.Crashes > 0);
        Assert.Contains(r.Findings, f => f.ExceptionType.Contains("IndexOutOfRange"));
        Assert.All(r.Findings, f => Assert.False(string.IsNullOrEmpty(f.Mutation)));
    }

    [Fact]
    public void A_DiscForge_domain_format_exception_counts_as_a_clean_rejection()
    {
        var probes = new List<(string, Action<byte[]>)>
        {
            ("pvr", b => DiscForge.Core.Dreamcast.Pvr.Parse(b)),   // throws PvrFormatException on garbage
        };
        var r = ParserFuzz.Run(Seed(), probes, iterations: 100);
        Assert.True(r.Clean);
    }

    [Fact]
    public void Runs_are_deterministic()
    {
        var probes = new List<(string, Action<byte[]>)>
        {
            ("buggy", b => { int len = BitConverter.ToInt32(b, 0); _ = b[len]; }),
        };
        var seed = Seed();
        Assert.Equal(ParserFuzz.Run(seed, probes, 120).Crashes, ParserFuzz.Run(seed, probes, 120).Crashes);
    }
}
