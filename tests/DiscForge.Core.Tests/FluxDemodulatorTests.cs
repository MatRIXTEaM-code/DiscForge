// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The flux demodulator is the stage between a raw optical flux capture and the EFM decoder. It is validated
/// software-first: because a real ECMA-130 codebook is not present, correctness is proven by round-tripping
/// against DiscForge's own EFM encoder. These tests pin the exact cell-domain round-trip, the full
/// bytes→EFM→flux→EFM→bytes pipeline, cell-clock recovery from jittered transition timing (exact up to the
/// half-cell ambiguity limit), and the serialised flux payload.
/// </summary>
public class FluxDemodulatorTests
{
    private static byte[] Sample(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((i * 73 + 29) % 256);
        return b;
    }

    [Fact]
    public void Cell_domain_round_trips_a_channel_bitstream_exactly()
    {
        var bits = Efm.Encode(Sample(256));
        var flux = FluxDemodulator.FromChannelBits(bits);
        var back = flux.ToChannelBits();
        Assert.Equal(bits, back);
    }

    [Fact]
    public void The_full_pipeline_recovers_the_original_bytes()
    {
        var data = Sample(1000);
        var bits = Efm.Encode(data);
        var flux = FluxDemodulator.FromChannelBits(bits);
        var decoded = FluxDecoder.Decode(flux);
        Assert.Equal(data, decoded);
    }

    [Fact]
    public void Clock_recovery_from_jittered_timing_reconstructs_exact_runs()
    {
        var data = Sample(800);
        var flux = FluxDemodulator.FromChannelBits(Efm.Encode(data));

        const int cell = 16;
        var timings = FluxDemodulator.ToTimings(flux, cell, jitter: 3, seed: 5);  // ±3 well under the ±8 limit
        double recovered = FluxDemodulator.EstimateCellPeriod(timings);
        Assert.InRange(recovered, cell - 0.5, cell + 0.5);

        var demod = FluxDemodulator.Demodulate(timings, flux.LeadingCells) with { TotalCells = flux.TotalCells };
        Assert.Equal(flux.RunLengths, demod.RunLengths);         // every run recovered exactly
        Assert.Equal(data, FluxDecoder.Decode(demod));           // …so the bytes come back
    }

    [Fact]
    public void The_serialised_flux_payload_round_trips()
    {
        var flux = FluxDemodulator.FromChannelBits(Efm.Encode(Sample(300)));
        var back = FluxBitstream.Deserialize(flux.Serialize());
        Assert.Equal(flux.LeadingCells, back.LeadingCells);
        Assert.Equal(flux.TotalCells, back.TotalCells);
        Assert.Equal(flux.RunLengths, back.RunLengths);
        Assert.Equal(FluxDecoder.Decode(flux), FluxDecoder.Decode(back));
    }

    [Fact]
    public void Byte_capacity_matches_the_efm_framing()
    {
        // EFM lays out n bytes as 14-bit words with 3 merging bits between them: 17n − 3 cells.
        for (int n = 1; n <= 50; n++)
            Assert.Equal(n, FluxDecoder.EfmByteCapacity(17 * n - 3));
    }
}
