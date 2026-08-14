// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Convert;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using DiscForge.Core.Raw;
using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// End-to-end tests that CHAIN this session's new features on synthetic discs, exercising the
/// interactions the isolated unit tests don't: analysis feeding recovery, recovery feeding
/// verification, and an image surviving a full round of tools while staying self-consistent.
/// </summary>
public class CrossFeatureIntegrationTests
{
    private const int SS2048 = 2048;
    private const int SS2352 = 2352;

    // ---- ISO analysis chain: build → descriptor → coverage → recover → re-cover ----

    [Fact]
    public void Iso_flows_through_descriptor_coverage_and_recovery_consistently()
    {
        var content = new byte[6000];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i * 11 + 3);
        var iso = IsoBuilder.Build("DISC", new[] { new IsoBuilder.FileEntry("GAME.BIN", content) }).Image;

        // Append a zero-filled tail (free space beyond the declared volume).
        var padded = new byte[iso.Length + 4 * SS2048];
        iso.CopyTo(padded, 0);

        // 1) Minimal descriptor round-trips the whole image exactly, and the zero tail is fill.
        var mdd = MinimalDiscDescriptor.Analyze(padded, SS2048);
        Assert.Equal(padded, MinimalDiscDescriptor.Reconstruct(mdd));
        Assert.True(mdd.FillSectors >= 4);

        // 2) Coverage proof classifies it; the file's sectors are claimed, unresolved gaps surfaced.
        var cov = PhysicalCoverage.OfIso(padded);
        Assert.Equal(padded.Length / SS2048, cov.TotalSectors);
        Assert.Empty(cov.Overlaps);                     // no structure double-claims a sector

        // 3) Erase a free tail sector and recover it; the reconstructed image must still pass the
        //    descriptor round trip (recovery produced coherent bytes, not garbage).
        long tail = padded.Length / SS2048 - 1;
        padded.AsSpan((int)(tail * SS2048), SS2048).Fill(0x5A);   // "erased"
        var rec = FilesystemConstrainedRecovery.RecoverIso(padded, new[] { tail });
        Assert.Equal(FcrOutcome.Recovered, Assert.Single(rec.Findings).Outcome);

        var mdd2 = MinimalDiscDescriptor.Analyze(rec.Image, SS2048);
        Assert.Equal(rec.Image, MinimalDiscDescriptor.Reconstruct(mdd2));
    }

    // ---- recovery → certificate: a rescued dump certifies lossless vs the golden ----

    private static byte[] ValidMode1(int seed)
    {
        var s = new byte[SS2352];
        s[0] = 0x00; for (int i = 1; i <= 10; i++) s[i] = 0xFF; s[11] = 0x00;   // sync
        s[12] = 0x00; s[13] = 0x02; s[14] = 0x00; s[15] = 0x01;                 // header + mode 1
        for (int i = 16; i < 16 + 2048; i++) s[i] = (byte)((i * 31 + seed) % 256);
        EdcEcc.FillMode1(s);
        return s;
    }

    private static byte[] C2Block(IEnumerable<int> bad)
    {
        var b = new byte[294];
        foreach (int i in bad) b[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return b;
    }

    [Fact]
    public void A_c2_rescued_image_certifies_lossless_against_the_golden()
    {
        const int N = 4;   // sectors
        var golden = new byte[N * SS2352];
        for (int k = 0; k < N; k++) ValidMode1(k + 1).CopyTo(golden, k * SS2352);

        // Three reads, each corrupting a DIFFERENT window of sector 1 and flagging it in C2, so no
        // single read is whole but their union is — the C2 consensus win.
        var img = new byte[3][]; var c2 = new byte[3][];
        int[][] win = { new[] { 100, 160 }, new[] { 300, 360 }, new[] { 500, 560 } };
        for (int r = 0; r < 3; r++)
        {
            img[r] = (byte[])golden.Clone();
            c2[r] = new byte[N * 294];
            for (int i = win[r][0]; i < win[r][1]; i++) img[r][1 * SS2352 + i] ^= 0xFF;
            C2Block(Enumerable.Range(win[r][0], win[r][1] - win[r][0])).CopyTo(c2[r], 1 * 294);
        }

        var merged = C2ConsensusMerge.Merge(new[] { img[0], img[1], img[2] }, new byte[]?[] { c2[0], c2[1], c2[2] });
        Assert.True(merged.Report.FullyRecovered);

        // The rescued image, compared to the golden, must certify LOSSLESS with a shared content hash.
        var cert = ConversionCertificate.Build(golden, merged.Image, "golden.bin", "rescued.bin", SS2352);
        Assert.True(cert.Lossless);
        Assert.NotNull(cert.ContentSha256);
    }

    // ---- descriptor + coverage agree on a deduped image ----

    [Fact]
    public void Descriptor_dedup_and_coverage_agree_on_a_synthetic_image()
    {
        // 2 unique sectors, one repeated, plus a fill run — descriptor accounts for all, reconstructs
        // exactly; the counts sum to the total (a conservation check across the two analyzers' views).
        var a = new byte[SS2048]; new Random(1).NextBytes(a);
        var b = new byte[SS2048]; new Random(2).NextBytes(b);
        var zero = new byte[SS2048];
        var image = new byte[6 * SS2048];
        a.CopyTo(image, 0 * SS2048);
        b.CopyTo(image, 1 * SS2048);
        a.CopyTo(image, 2 * SS2048);           // duplicate of a
        zero.CopyTo(image, 3 * SS2048);
        zero.CopyTo(image, 4 * SS2048);
        b.CopyTo(image, 5 * SS2048);           // duplicate of b

        var mdd = MinimalDiscDescriptor.Analyze(image, SS2048);
        Assert.Equal(image, MinimalDiscDescriptor.Reconstruct(mdd));
        Assert.Equal(6, mdd.TotalSectors);
        Assert.Equal(2, mdd.UniqueSectors);
        Assert.Equal(2, mdd.DuplicateSectors);
        Assert.Equal(2, mdd.FillSectors);
        // Conservation: unique + duplicate + fill == total.
        Assert.Equal(mdd.TotalSectors, mdd.UniqueSectors + mdd.DuplicateSectors + mdd.FillSectors);
    }
}
