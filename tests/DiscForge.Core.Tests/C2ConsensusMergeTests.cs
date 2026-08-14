// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;
using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Byte-level C2 consensus recovery reassembles a raw sector from the C2-good bytes of several reads. The test
/// that matters: three reads that each corrupt a DIFFERENT window (so no single read validates on its own), with
/// C2 pointers marking exactly the corrupt bytes, must merge back to the original sector and pass its EDC — the
/// recovery a sector-level merge cannot do. A sector all reads corrupt in the SAME place stays unrecovered.
/// </summary>
public class C2ConsensusMergeTests
{
    private const int SS = 2352;
    private const int C2 = 294;

    private static byte[] ValidMode1(int seed)
    {
        var s = new byte[SS];
        s[0] = 0x00; for (int i = 1; i <= 10; i++) s[i] = 0xFF; s[11] = 0x00;   // sync
        s[12] = 0x00; s[13] = 0x02; s[14] = 0x00; s[15] = 0x01;                 // header + mode 1
        for (int i = 16; i < 16 + 2048; i++) s[i] = (byte)((i * 31 + seed) % 256);
        EdcEcc.FillMode1(s);
        return s;
    }

    private static byte[] C2Block(IEnumerable<int> badBytes)
    {
        var b = new byte[C2];
        foreach (int i in badBytes) b[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return b;
    }

    [Fact]
    public void Reassembles_a_sector_no_single_read_held_whole()
    {
        var master = ValidMode1(1);
        var clean = ValidMode1(2);
        var stuck = ValidMode1(3);
        const int N = 4;

        var img = new byte[3][]; var c2 = new byte[3][];
        for (int r = 0; r < 3; r++) { img[r] = new byte[N * SS]; c2[r] = new byte[N * C2]; }

        // Sector 0: each read corrupts a different 50-byte window and flags it in C2.
        int[][] win = { new[] { 100, 150 }, new[] { 300, 350 }, new[] { 500, 550 } };
        for (int r = 0; r < 3; r++)
        {
            var s = (byte[])master.Clone();
            for (int i = win[r][0]; i < win[r][1]; i++) s[i] ^= 0xFF;
            s.CopyTo(img[r].AsSpan(0 * SS, SS));
            C2Block(Enumerable.Range(win[r][0], win[r][1] - win[r][0])).CopyTo(c2[r].AsSpan(0 * C2, C2));
        }
        // Sectors 1,2: clean and identical in every read.
        for (int r = 0; r < 3; r++) { clean.CopyTo(img[r].AsSpan(1 * SS, SS)); clean.CopyTo(img[r].AsSpan(2 * SS, SS)); }
        // Sector 3: all reads corrupt the SAME wide window (700..1000) to different values — no read
        // vouches for it and the burst is far past the sector's ECC budget, so it stays unrecovered
        // even with the chained RSPC stage.
        for (int r = 0; r < 3; r++)
        {
            var s = (byte[])stuck.Clone();
            for (int i = 700; i < 1000; i++) s[i] = (byte)(i + r);
            s.CopyTo(img[r].AsSpan(3 * SS, SS));
            C2Block(Enumerable.Range(700, 300)).CopyTo(c2[r].AsSpan(3 * C2, C2));
        }

        var result = C2ConsensusMerge.Merge(new[] { img[0], img[1], img[2] }, new byte[]?[] { c2[0], c2[1], c2[2] });
        var rep = result.Report;

        Assert.Equal(2, rep.Agreed);                 // sectors 1, 2
        Assert.Equal(1, rep.Recovered);              // sector 0
        Assert.Equal(1, rep.Unrecovered);            // sector 3
        Assert.Equal(1, rep.RescuedFromFragments);   // the byte-consensus win
        Assert.Contains(3, rep.UnrecoveredSectors);

        // Sector 0 came back byte-identical to the original master, and validates.
        Assert.Equal(master, result.Image.AsSpan(0, SS).ToArray());
        Assert.Equal((true, true), EdcEcc.VerifyMode1(result.Image.AsSpan(0, SS)));
    }

    [Fact]
    public void The_sectors_own_ecc_finishes_what_voting_cant()
    {
        // Both reads corrupt the SAME two scattered bytes and flag them in C2. Byte-voting can't
        // help — no read vouches for those positions, so they stay wrong and EDC fails. But two
        // scattered erasures are well within the sector's Reed-Solomon Product-Code budget, so the
        // ECC stage (told exactly which bytes to trust least) restores them. Neither stage alone
        // recovers this sector; chained, they do.
        var master = ValidMode1(9);
        int[] bad = { 200, 1500 };

        var a = (byte[])master.Clone();
        var b = (byte[])master.Clone();
        foreach (int i in bad) { a[i] = 0xAA; b[i] = 0xAA; }   // same wrong value in both reads

        var c2 = C2Block(bad);
        var result = C2ConsensusMerge.Merge(new[] { a, b }, new byte[]?[] { c2, c2 });

        Assert.Equal(1, result.Report.Recovered);
        Assert.Equal(1, result.Report.EccRecovered);           // the new chained-ECC win
        Assert.Equal(master, result.Image.AsSpan(0, SS).ToArray());
        Assert.Equal((true, true), EdcEcc.VerifyMode1(result.Image.AsSpan(0, SS)));
    }

    [Fact]
    public void More_than_64_reads_do_not_overflow_the_vote_buffer()
    {
        // Regression: the byte-vote fallback buffer was capped at 64 but indexed by read number, so
        // 65+ reads on a non-identical sector overflowed. Build 65 reads, one corrupting a byte with
        // no C2 flag (forces the plain-majority fallback), and confirm it merges instead of crashing.
        const int N = 65;
        var master = ValidMode1(4);
        var images = new byte[N][];
        var c2 = new byte[]?[N];
        for (int r = 0; r < N; r++) { images[r] = (byte[])master.Clone(); c2[r] = null; }
        images[0][600] ^= 0xFF;                          // one differing byte, unflagged → not all-same

        var result = C2ConsensusMerge.Merge(images, c2);  // must not throw
        // Majority of 65 reads restores the byte (64 agree), so the sector validates.
        Assert.Equal(master, result.Image.AsSpan(0, SS).ToArray());
    }

    [Fact]
    public void A_read_with_no_c2_still_participates()
    {
        var master = ValidMode1(5);
        var a = (byte[])master.Clone(); for (int i = 100; i < 130; i++) a[i] ^= 0xFF;   // corrupt, flagged
        var b = (byte[])master.Clone();                                                 // clean, no C2 file

        var c2a = C2Block(Enumerable.Range(100, 30));
        var result = C2ConsensusMerge.Merge(new[] { a, b }, new byte[]?[] { c2a, null });

        // b's good bytes fill a's holes; the sector validates.
        Assert.Equal(master, result.Image.AsSpan(0, SS).ToArray());
        Assert.Equal(1, result.Report.Recovered);
    }
}
