// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Preservation;
using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The provenance merge rebuilds one image from several imperfect copies while honouring each copy's bad-sector
/// map, and emits a signed, checkable certificate. These tests pin the behaviour that makes it unique: a sector
/// a copy could not read is EXCLUDED from the vote (not counted as zero-filled data), every output sector's
/// origin is recorded, the signature verifies, and the certificate is bound to the exact reconstructed image so
/// a tampered output is caught.
/// </summary>
public class MergeCertificateTests
{
    private const int SS = DumpMerge.RawSectorSize;

    private static byte[] Image(int sectors, IDictionary<int, byte>? overrides = null)
    {
        var b = new byte[sectors * SS];
        for (int s = 0; s < sectors; s++)
        {
            byte v = overrides is not null && overrides.TryGetValue(s, out var o) ? o : (byte)((s + 1) & 0xFF);
            b.AsSpan(s * SS, SS).Fill(v);
        }
        return b;
    }

    [Fact]
    public void A_holed_sector_is_excluded_and_taken_from_the_other_copy()
    {
        var a = Image(10);
        var b = Image(10, new Dictionary<int, byte> { [5] = 0xAA, [7] = 0xBB });
        // Copy A could not read sector 5 — its bytes there must not be voted on.
        var holeA = new BadSectorMap { Image = "a", TotalSectors = 10, UnreadableLba = new long[] { 5 } };

        var r = ProvenanceMerge.Merge(new[] { a, b }, new BadSectorMap?[] { holeA, null });
        var c = r.Certificate;

        Assert.Equal(8, c.AllAgree);
        Assert.Equal(1, c.SingleSource);      // sector 5 — only B had it
        Assert.Equal(1, c.VoteBestEffort);    // sector 7 — both present, no EDC, tie → earliest
        Assert.Equal(1, c.HoleExcluded);
        Assert.Equal(0, c.Unrecovered);
        Assert.True(c.FullyRecovered);

        // The output at the holed sector is B's real data, not A's.
        Assert.Equal(0xAA, r.Image[5 * SS]);
        // Provenance says sector 5 came from source 1 (B), decided SingleSource.
        var run5 = c.Runs.Single(x => x.StartSector <= 5 && x.EndSector >= 5);
        Assert.Equal(MergeMethod.SingleSource, run5.Method);
        Assert.Equal(1, run5.Source);
    }

    [Fact]
    public void All_sectors_holed_in_every_copy_is_unrecovered()
    {
        var a = Image(4);
        var b = Image(4);
        var holeA = new BadSectorMap { Image = "a", TotalSectors = 4, UnreadableLba = new long[] { 2 } };
        var holeB = new BadSectorMap { Image = "b", TotalSectors = 4, UnreadableLba = new long[] { 2 } };

        var r = ProvenanceMerge.Merge(new[] { a, b }, new BadSectorMap?[] { holeA, holeB });
        Assert.Equal(1, r.Certificate.Unrecovered);
        Assert.False(r.Certificate.FullyRecovered);
        Assert.Contains(2L, r.Certificate.UnrecoveredSectors);
    }

    [Fact]
    public void Signing_verifies_and_binds_the_exact_image()
    {
        var a = Image(6);
        var b = Image(6, new Dictionary<int, byte> { [3] = 0x55 });
        var r = ProvenanceMerge.Merge(new[] { a, b });

        var (privB64, _) = DumpLineageLog.GenerateKey();
        using var priv = DumpLineageLog.LoadPrivateKey(privB64);
        var signed = r.Certificate.Sign(priv);

        Assert.NotNull(signed.Signature);
        Assert.True(signed.VerifySignature());

        // Re-pointing the certificate at a different image (any field the signature covers) breaks verification.
        var tampered = signed with { OutputSha256 = new string('0', 64) };
        Assert.False(tampered.VerifySignature());
    }

    [Fact]
    public void The_certificate_survives_a_json_round_trip()
    {
        var r = ProvenanceMerge.Merge(new[] { Image(5), Image(5, new Dictionary<int, byte> { [2] = 9 }) });
        var (privB64, _) = DumpLineageLog.GenerateKey();
        using var priv = DumpLineageLog.LoadPrivateKey(privB64);
        var signed = r.Certificate.Sign(priv);

        var path = Path.Combine(Path.GetTempPath(), "dforge_dmc_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            signed.Save(path);
            var back = MergeCertificate.Load(path);
            Assert.Equal(signed.OutputSha256, back.OutputSha256);
            Assert.Equal(signed.Runs.Count, back.Runs.Count);
            Assert.True(back.VerifySignature());   // signature survives serialization
        }
        finally { File.Delete(path); }
    }
}
