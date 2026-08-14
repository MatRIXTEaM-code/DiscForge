// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.Raw;

/// <summary>
/// A flux capture demodulated into the channel-bit cell domain: the number of blank cells before the first
/// pit/land transition, then the length in cells of each pit/land run, then the total cell count. This is the
/// exact, lossless bridge between a raw optical flux capture and the EFM channel bitstream — an EFM '1' channel
/// bit is a pit/land transition and each run is <c>1 + (following zeros)</c> cells long, so the cell-domain form
/// round-trips with a channel bitstream bit-for-bit.
/// </summary>
public sealed record FluxBitstream(int LeadingCells, byte[] RunLengths, int TotalCells)
{
    /// <summary>Reconstruct the EFM channel bitstream: a transition after the leading blank cells, then a
    /// transition after each inter-transition run, with the tail left blank to the recorded total. RunLengths
    /// are the gaps BETWEEN transitions only — the leading and trailing partial cells are not runs, so they can
    /// never masquerade as a sub-3T interval during clock recovery.</summary>
    public bool[] ToChannelBits()
    {
        var bits = new bool[TotalCells];
        int pos = LeadingCells;
        if (pos < TotalCells) bits[pos] = true;          // the first transition
        foreach (var run in RunLengths)
        {
            pos += run;
            if (pos >= TotalCells) break;
            bits[pos] = true;                            // each subsequent transition
        }
        return bits;
    }

    /// <summary>Serialise to the compact on-disk flux-cell payload: leadingCells, count, totalCells, run bytes.</summary>
    public byte[] Serialize()
    {
        var buf = new byte[12 + RunLengths.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0), LeadingCells);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), RunLengths.Length);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), TotalCells);
        RunLengths.CopyTo(buf.AsSpan(12));
        return buf;
    }

    public static FluxBitstream Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) throw new ArgumentException("Flux-cell payload is too short.", nameof(data));
        int leading = BinaryPrimitives.ReadInt32LittleEndian(data);
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int total = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if (count < 0 || total < 0 || data.Length < 12 + count)
            throw new ArgumentException("Flux-cell payload is malformed.", nameof(data));
        return new FluxBitstream(leading, data.Slice(12, count).ToArray(), total);
    }
}

/// <summary>
/// The flux demodulator — the stage between a raw optical flux/RF capture and the EFM decoder that
/// <c>FluxContainer</c> deferred as "a separate, later stage". It recovers the channel-bit cell clock from the
/// transition timing and quantises each pit/land interval to a whole number of cells (EFM's 3T–11T run-length
/// law), producing the channel bitstream the existing <see cref="Efm"/> decoder consumes. This is the genuinely
/// unsolved, hardware-independent part of low-level optical preservation, and it is validated software-first by
/// round-tripping against DiscForge's own EFM encoder.
///
/// Clean-room note: this reads and decodes the disc's own physical signal — the purest preservation there is. It
/// defeats nothing. Decoding a REAL disc additionally needs the authoritative ECMA-130 8-to-14 table dropped
/// into <see cref="Efm"/> (a data swap); this demodulation stage is table-independent and complete now.
/// </summary>
public static class FluxDemodulator
{
    /// <summary>EFM run-length bounds in channel cells (d=2 → 3T minimum, k=10 → 11T maximum).</summary>
    public const int MinRun = 3;
    public const int MaxRun = 11;

    /// <summary>Cell-domain view of an EFM channel bitstream — the exact inverse of <see cref="FluxBitstream.ToChannelBits"/>.</summary>
    public static FluxBitstream FromChannelBits(ReadOnlySpan<bool> channel)
    {
        var runs = new List<byte>();
        int first = -1;
        for (int i = 0; i < channel.Length; i++)
            if (channel[i]) { first = i; break; }

        if (first < 0) return new FluxBitstream(channel.Length, Array.Empty<byte>(), channel.Length);

        // Only the gaps BETWEEN transitions are runs. The leading blank (first) and the trailing blank
        // (length - last) are partial cells recorded in LeadingCells / TotalCells, never as runs.
        int prev = first;
        for (int i = first + 1; i < channel.Length; i++)
            if (channel[i]) { runs.Add((byte)(i - prev)); prev = i; }
        return new FluxBitstream(first, runs.ToArray(), channel.Length);
    }

    /// <summary>
    /// Demodulate raw transition timing (sample-domain intervals between pit/land edges) into the cell domain.
    /// The cell period is recovered from the timing itself when <paramref name="cellSamples"/> is not supplied.
    /// </summary>
    public static FluxBitstream Demodulate(ReadOnlySpan<int> transitionIntervalsSamples, int leadingCells = 0,
                                           double? cellSamples = null)
    {
        double cell = cellSamples ?? EstimateCellPeriod(transitionIntervalsSamples);
        if (cell <= 0) throw new InvalidDataException("Could not recover a positive cell period from the flux.");

        var runs = new byte[transitionIntervalsSamples.Length];
        long totalCells = leadingCells;
        for (int i = 0; i < transitionIntervalsSamples.Length; i++)
        {
            int n = (int)Math.Round(transitionIntervalsSamples[i] / cell, MidpointRounding.AwayFromZero);
            if (n < MinRun) n = MinRun;
            if (n > MaxRun) n = MaxRun;
            runs[i] = (byte)n;
            totalCells += n;
        }
        return new FluxBitstream(leadingCells, runs, (int)totalCells);
    }

    /// <summary>
    /// Recover the channel-cell period from transition intervals. A single jittered 3T run makes a naive
    /// min/3 seed unreliable, so the period is first found by a residual-minimising search — the true cell is the
    /// one against which every interval quantises closest to a whole number of cells — bracketed by both the
    /// shortest and longest runs (cell ∈ [maxInterval/(11.5), minInterval/(2.5)]). The winner is then polished by
    /// least squares (Σinterval / Σcells), which cancels the remaining random jitter across the whole capture.
    /// </summary>
    public static double EstimateCellPeriod(ReadOnlySpan<int> intervals)
    {
        if (intervals.Length == 0) return 0;
        int min = int.MaxValue, max = 0;
        foreach (var v in intervals) { if (v > 0 && v < min) min = v; if (v > max) max = v; }
        if (min == int.MaxValue) return 0;

        // The true cell period lies between "the longest run is 11T" and "the shortest run is 3T".
        double lo = max / (MaxRun + 0.5);
        double hi = min / (MinRun - 0.5);
        if (hi < lo) (lo, hi) = (min / (double)MinRun * 0.85, min / (double)MinRun * 1.15);

        double bestCell = min / (double)MinRun, bestResidual = double.MaxValue;
        const int steps = 1000;
        for (int i = 0; i <= steps; i++)
        {
            double cell = lo + (hi - lo) * i / steps;
            if (cell <= 0) continue;
            double residual = 0;
            foreach (var v in intervals)
            {
                double q = v / cell;
                residual += Math.Abs(q - Math.Round(q));
            }
            if (residual < bestResidual) { bestResidual = residual; bestCell = cell; }
        }

        // Polish: least-squares cell from the quantisation the search settled on.
        double refined = bestCell;
        for (int pass = 0; pass < 4; pass++)
        {
            long sumIntervals = 0, sumCells = 0;
            foreach (var v in intervals)
            {
                int n = (int)Math.Round(v / refined, MidpointRounding.AwayFromZero);
                if (n < MinRun) n = MinRun; else if (n > MaxRun) n = MaxRun;
                sumIntervals += v; sumCells += n;
            }
            if (sumCells == 0) break;
            double next = (double)sumIntervals / sumCells;
            if (Math.Abs(next - refined) < 1e-9) { refined = next; break; }
            refined = next;
        }
        return refined;
    }

    /// <summary>Synthesise transition timing from a cell-domain stream at a given cell period (for capture
    /// modelling and round-trip tests). <paramref name="jitter"/> perturbs each edge by ±jitter samples.</summary>
    public static int[] ToTimings(FluxBitstream flux, int cellSamples, int jitter = 0, int seed = 1)
    {
        var intervals = new int[flux.RunLengths.Length];
        // A tiny deterministic LCG so tests need no Random (which the environment forbids in some contexts).
        uint state = (uint)seed | 1u;
        int Noise()
        {
            if (jitter == 0) return 0;
            state = state * 1664525u + 1013904223u;
            return (int)(state % (uint)(2 * jitter + 1)) - jitter;
        }
        for (int i = 0; i < flux.RunLengths.Length; i++)
            intervals[i] = Math.Max(1, flux.RunLengths[i] * cellSamples + Noise());
        return intervals;
    }
}

/// <summary>
/// Chains the flux demodulator into the existing EFM decoder: cell-domain flux → channel bits → bytes. The byte
/// stream it yields is only as faithful to a real disc as <see cref="Efm"/>'s codebook; with DiscForge's modelled
/// table it round-trips its own encoder exactly, and with the authoritative ECMA-130 table dropped in it decodes
/// a real disc's flux. The demodulation itself is table-independent and complete.
/// </summary>
public static class FluxDecoder
{
    public static byte[] Decode(FluxBitstream flux)
    {
        ArgumentNullException.ThrowIfNull(flux);
        bool[] channel = flux.ToChannelBits();
        int byteCount = EfmByteCapacity(channel.Length);
        return Efm.Decode(channel, byteCount);
    }

    /// <summary>How many EFM bytes a channel stream of this many cells can hold (14-bit word + 3 merging bits,
    /// no merging bits before the first word).</summary>
    internal static int EfmByteCapacity(int cells) => cells < 14 ? 0 : (cells - 14) / 17 + 1;
}
