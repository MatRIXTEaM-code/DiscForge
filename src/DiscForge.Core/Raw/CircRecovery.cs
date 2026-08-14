// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Raw;

/// <summary>The verdict of the recoverability oracle for a given burst.</summary>
public sealed record RecoveryVerdict
{
    public required int BurstFrames { get; init; }
    public required int MaxErasuresPerC2 { get; init; }
    public required int C2ErasureCapacity { get; init; }
    public required bool FullyCorrectable { get; init; }
    public required int MaxCorrectableBurstFrames { get; init; }

    public string Assessment => FullyCorrectable
        ? $"C2 corrects it: at most {MaxErasuresPerC2} erasure(s) land in any codeword (capacity {C2ErasureCapacity})."
        : $"Beyond C2: up to {MaxErasuresPerC2} erasures reach a single codeword (> {C2ErasureCapacity}); " +
          "the uncorrectable samples fall to interpolation/concealment.";
}

/// <summary>
/// The CIRC recovery model — why a scratch that would obliterate a plain Reed-Solomon codeword is
/// shrugged off by a CD. CIRC (Cross-Interleaved Reed-Solomon Code) protects audio with two RS stages,
/// C1 = RS(32,28) and C2 = RS(28,24), separated by a deep cross-interleave: each C2 codeword's 28
/// symbols are delayed by 4·j frames (j = 0..27), so one codeword is smeared across ~109 frames on the
/// disc. When a physical burst destroys a run of consecutive frames, de-interleaving scatters that
/// damage — each C2 codeword sees only about burst/4 erasures — turning an uncorrectable burst into many
/// easily-corrected ones. This models that exactly: it computes how a burst distributes into C2's
/// erasure budget (the oracle) and demonstrates the recovery for real with <see cref="ReedSolomonGf256"/>
/// (encode audio → interleave → corrupt a burst → de-interleave → C2 decode → verify). Modelling only.
/// </summary>
public static class CircRecovery
{
    public const int InterleaveDelay = 4;     // frames of delay per C2 symbol
    public const int C2Symbols = 28;          // RS(28,24)
    public const int C2Data = 24;
    public const int C2ErasureCapacity = C2Symbols - C2Data;   // 4

    /// <summary>The oracle: given a burst of <paramref name="burstFrames"/> consecutive fully-lost
    /// frames, work out the worst-case erasure load on any C2 codeword and whether C2 absorbs it.</summary>
    public static RecoveryVerdict AnalyzeBurst(int burstFrames)
    {
        if (burstFrames < 0) throw new ArgumentOutOfRangeException(nameof(burstFrames));
        int max = MaxErasuresForBurst(burstFrames);

        int maxCorrectable = 0;
        for (int b = 0; MaxErasuresForBurst(b) <= C2ErasureCapacity; b++) maxCorrectable = b;

        return new RecoveryVerdict
        {
            BurstFrames = burstFrames,
            MaxErasuresPerC2 = max,
            C2ErasureCapacity = C2ErasureCapacity,
            FullyCorrectable = max <= C2ErasureCapacity,
            MaxCorrectableBurstFrames = maxCorrectable,
        };
    }

    // Worst-case erasures a burst of consecutive frames puts into one C2 codeword: symbol j of
    // codeword c comes from frame (c + 4j); count the j whose frame lands in the burst, maxed over phase.
    private static int MaxErasuresForBurst(int burstFrames)
    {
        int max = 0;
        for (int c = 0; c < InterleaveDelay; c++)      // only the phase mod 4 matters
        {
            int count = 0;
            for (int j = 0; j < C2Symbols; j++)
                if (c + InterleaveDelay * j < burstFrames) count++;
            max = Math.Max(max, count);
        }
        return max;
    }

    /// <summary>Demonstrate the recovery for real: encode <paramref name="frames"/> C2 codewords of random
    /// audio, cross-interleave them, erase a burst of <paramref name="burstLen"/> consecutive interleaved
    /// frames, de-interleave, and C2-decode. Returns whether every interior codeword was recovered.</summary>
    public static bool SimulateBurst(int frames, int burstStart, int burstLen, out int maxErasuresSeen)
    {
        maxErasuresSeen = 0;
        var rs = new ReedSolomonGf256(C2Symbols, C2Data);
        var rng = new Random(12345);

        // Original audio + its C2 codewords.
        var codewords = new byte[frames][];
        for (int f = 0; f < frames; f++)
        {
            var data = new byte[C2Data];
            rng.NextBytes(data);
            codewords[f] = rs.Encode(data);
        }

        // A C2 codeword f contributes its symbol j to interleaved frame (f + 4j). So codeword f's symbol
        // j is erased exactly when interleaved frame (f + 4j) falls in the burst.
        int burstEnd = burstStart + burstLen;   // exclusive
        bool all = true;
        for (int f = 0; f < frames; f++)
        {
            var erased = new List<int>();
            for (int j = 0; j < C2Symbols; j++)
            {
                int interleavedFrame = f + InterleaveDelay * j;
                if (interleavedFrame >= burstStart && interleavedFrame < burstEnd) erased.Add(j);
            }
            maxErasuresSeen = Math.Max(maxErasuresSeen, erased.Count);
            if (erased.Count == 0) continue;

            var received = (byte[])codewords[f].Clone();
            foreach (var j in erased) received[j] = (byte)rng.Next(256);   // burst noise
            bool ok = rs.TryDecode(received, out var fixedUp, erased);
            if (!ok || !fixedUp.AsSpan(0, C2Data).SequenceEqual(codewords[f].AsSpan(0, C2Data)))
                all = false;
        }
        return all;
    }
}
