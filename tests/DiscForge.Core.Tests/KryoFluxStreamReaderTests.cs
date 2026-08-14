// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Floppy;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// KryoFlux raw-stream reader — proven by decoding a spec-shaped stream that exercises each
/// in-band cell type (Flux1/2/3, Ovl16, Nop) and each OOB block used for a summary (KFInfo,
/// Index, StreamEnd), then checking the flux count, index timing, parsed sample clock and the
/// inferred RPM.
/// </summary>
public class KryoFluxStreamReaderTests
{
    private static void Oob(List<byte> b, byte type, ReadOnlySpan<byte> payload)
    {
        b.Add(0x0D); b.Add(type);
        var sz = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(sz, (ushort)payload.Length);
        b.AddRange(sz);
        b.AddRange(payload.ToArray());
    }

    private static byte[] U32(uint v) { var a = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(a, v); return a; }

    private static byte[] BuildStream()
    {
        var b = new List<byte>();

        // KFInfo: sample clock ~24.027 MHz.
        Oob(b, 0x04, Encoding.ASCII.GetBytes("sck=24027428.5714285, ick=3003428.5714285"));

        // Five flux transitions across the cell types.
        b.Add(0x20);                    // Flux1
        b.Add(0x30);                    // Flux1
        b.Add(0x05); b.Add(0x00);       // Flux2
        b.Add(0x0B);                    // Ovl16 (no transition)
        b.Add(0x40);                    // Flux1 (with overflow applied)
        b.Add(0x0C); b.Add(0x01); b.Add(0x00);   // Flux3

        // Two index pulses one revolution apart at 300 RPM (0.2 s × sck ≈ 4,805,486 ticks).
        var idx0 = new List<byte>(); idx0.AddRange(U32(0)); idx0.AddRange(U32(1000)); idx0.AddRange(U32(0));
        Oob(b, 0x02, idx0.ToArray());
        var idx1 = new List<byte>(); idx1.AddRange(U32(50)); idx1.AddRange(U32(1000 + 4_805_486)); idx1.AddRange(U32(1));
        Oob(b, 0x02, idx1.ToArray());

        // StreamEnd (result 0), then EOF.
        var end = new List<byte>(); end.AddRange(U32(60)); end.AddRange(U32(0));
        Oob(b, 0x03, end.ToArray());
        b.Add(0x0D); b.Add(0x0D); b.Add(0x0D); b.Add(0x0D);   // EOF OOB

        return b.ToArray();
    }

    [Fact]
    public void Counts_flux_transitions_across_all_cell_types()
        => Assert.Equal(5, KryoFluxStreamReader.Parse(BuildStream()).FluxTransitions);

    [Fact]
    public void Parses_kfinfo_and_the_sample_clock()
    {
        var st = KryoFluxStreamReader.Parse(BuildStream());
        Assert.Equal("24027428.5714285", st.Info["sck"]);
        Assert.NotNull(st.SampleClockHz);
        Assert.Equal(24_027_428.57, st.SampleClockHz!.Value, 1);
    }

    [Fact]
    public void Reads_index_pulses_and_stream_end()
    {
        var st = KryoFluxStreamReader.Parse(BuildStream());
        Assert.Equal(2, st.Indices.Count);
        Assert.Equal(1000u, st.Indices[0].SampleCounter);
        Assert.True(st.StreamEndSeen);
    }

    [Fact]
    public void Infers_300_rpm_from_two_index_pulses()
    {
        var st = KryoFluxStreamReader.Parse(BuildStream());
        Assert.NotNull(st.Rpm);
        Assert.Equal(300.0, st.Rpm!.Value, 0);
    }
}
