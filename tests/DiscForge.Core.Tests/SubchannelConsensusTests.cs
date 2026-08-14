// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The read-back consensus (Tier B of AdaptiveReread): several raw reads of the same range are voted
/// per sector so a transient sub-channel Q mis-read — the drive decoding one random sector's Q wrong
/// on one pass — is out-voted, exactly the "wandering mis-address" that makes a single-pass read-back
/// FAIL a burn that is actually byte-faithful. The tests that matter: (1) a Q corrupted at a DIFFERENT
/// sector on each read merges back to the truth; (2) a main-channel dropout on one read is repaired;
/// (3) a Q that is corrupt IDENTICALLY on every read — a stable on-disc LibCrypt-style Q — is preserved
/// verbatim, never "fixed". If (3) failed, consensus would be a protection-destroying rewrite.
/// </summary>
public class SubchannelConsensusTests
{
    private const int Main = SubchannelConsensus.MainBytes; // 2352
    private const int Sub = SubchannelConsensus.SubBytes;   //   96
    private const int SS = SubchannelConsensus.SectorBytes; // 2448

    /// <summary>A 2448-byte sector: deterministic main channel + a real CRC-valid position Q.</summary>
    private static byte[] Sector(int track, int index, int absSeconds, int mainSeed)
    {
        var sec = new byte[SS];
        for (int i = 0; i < Main; i++) sec[i] = (byte)((i * 7 + mainSeed) & 0xFF);

        var q = SubQ.Position(QControl.None, track, index, new Msf(0, 0, 0), new Msf(0, absSeconds, 0));
        var frame = new SubcodeFrame();
        System.Array.Copy(q, frame.Q, 12);                  // R..W left zero, P false
        frame.EmitInterleaved96(sec.AsSpan(Main, Sub));
        return sec;
    }

    private static byte[] Golden(int sectors)
    {
        var g = new byte[sectors * SS];
        for (int s = 0; s < sectors; s++)
            Sector(1, 1, absSeconds: s + 1, mainSeed: s * 13 + 1).CopyTo(g.AsSpan(s * SS, SS));
        return g;
    }

    /// <summary>Flip a Q bit (bit 6 of a sub byte) in sector <paramref name="s"/> — breaks its Q-CRC.</summary>
    private static void CorruptQ(byte[] read, int s) => read[s * SS + Main + 0] ^= 0x40;

    /// <summary>Flip a main-channel byte in sector <paramref name="s"/> — a transient dropout.</summary>
    private static void CorruptMain(byte[] read, int s) => read[s * SS + 100] ^= 0xFF;

    private static (byte[] outp, SubchannelConsensus.Report rep) Merge(int sectors, params byte[][] reads)
    {
        using var ms = new MemoryStream();
        var rep = SubchannelConsensus.Merge(reads, sectors, ms);
        return (ms.ToArray(), rep);
    }

    [Fact]
    public void Wandering_Q_misread_is_outvoted_back_to_the_truth()
    {
        const int S = 6;
        var golden = Golden(S);
        byte[] r0 = (byte[])golden.Clone(), r1 = (byte[])golden.Clone(), r2 = (byte[])golden.Clone();

        // Each read mis-decodes a DIFFERENT sector's Q — the wandering read glitch.
        CorruptQ(r0, 1);
        CorruptQ(r1, 3);
        CorruptQ(r2, 4);

        var (outp, rep) = Merge(S, r0, r1, r2);

        // Majority (2 of 3 correct per sector) reconstructs the golden byte-for-byte.
        Assert.Equal(golden, outp);
        // Only sector 1 was wrong in the FIRST read, so exactly one sector's sub differs from pass 0.
        Assert.Equal(1, rep.SubCorrected);
        Assert.Equal(0, rep.MainCorrected);
        Assert.Equal(0, rep.PreservedNoValidQ);
    }

    [Fact]
    public void Main_channel_dropout_on_one_read_is_repaired()
    {
        const int S = 4;
        var golden = Golden(S);
        byte[] r0 = (byte[])golden.Clone(), r1 = (byte[])golden.Clone(), r2 = (byte[])golden.Clone();

        CorruptMain(r0, 2);

        var (outp, rep) = Merge(S, r0, r1, r2);

        Assert.Equal(golden, outp);
        Assert.Equal(1, rep.MainCorrected);
        Assert.Equal(0, rep.SubCorrected);
    }

    [Fact]
    public void Stable_corrupt_Q_is_preserved_verbatim_not_repaired()
    {
        const int S = 4;
        var golden = Golden(S);
        // Sector 2 carries a deliberately-corrupt Q on the disc — identical on every read (LibCrypt shape).
        CorruptQ(golden, 2);
        byte[] r0 = (byte[])golden.Clone(), r1 = (byte[])golden.Clone(), r2 = (byte[])golden.Clone();

        // Meanwhile a genuine wandering glitch hits a different sector on one read.
        CorruptQ(r0, 0);

        var (outp, rep) = Merge(S, r0, r1, r2);

        // Sector 2's sub-channel is emitted exactly as it sits on the disc — no fabricated valid Q.
        var emittedSub2 = outp.AsSpan(2 * SS + Main, Sub).ToArray();
        var diskSub2 = golden.AsSpan(2 * SS + Main, Sub).ToArray();
        Assert.Equal(diskSub2, emittedSub2);

        var q = new byte[12];
        RawSubchannel.ExtractQ(emittedSub2, q);
        Assert.False(RawSubchannel.QCrcValid(q), "the preserved Q must remain the disc's corrupt Q");

        Assert.Equal(1, rep.PreservedNoValidQ);
        // Sector 0's wandering glitch on read 0 was still out-voted.
        Assert.Equal(golden.AsSpan(0, SS).ToArray(), outp.AsSpan(0, SS).ToArray());
    }

    [Fact]
    public void A_single_read_cannot_be_voted()
    {
        var golden = Golden(2);
        Assert.Throws<ArgumentException>(() =>
        {
            using var ms = new MemoryStream();
            SubchannelConsensus.Merge(new[] { golden }, 2, ms);
        });
    }
}
