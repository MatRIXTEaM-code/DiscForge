// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The Dump Certificate's chain of custody, proven link by link: the Merkle
/// tree commits to every sector (root by hand-computed vector, odd-node
/// promotion, every index provable, tampering caught), the signature binds the
/// content (round-trips, breaks on any field change), and a single 2352-byte
/// slice verifies against a signed certificate with no access to the image.
/// </summary>
public class DumpCertificateTests
{
    private static byte[] Sector(int seed)
    {
        var s = new byte[2352];
        new Random(seed).NextBytes(s);
        return s;
    }

    private static MemoryStream Image(int sectors)
    {
        var ms = new MemoryStream();
        for (int i = 0; i < sectors; i++) ms.Write(Sector(i), 0, 2352);
        ms.Position = 0;
        return ms;
    }

    // ---- Merkle ------------------------------------------------------------

    [Fact]
    public void Root_ThreeLeaves_MatchesHandComputation()
    {
        // promote-odd: root = H(H(L0‖L1) ‖ L2)
        using var img = Image(3);
        var leaves = SectorMerkle.LeafHashes(img);
        var l01 = SHA256.HashData(leaves[0].Concat(leaves[1]).ToArray());
        var expected = SHA256.HashData(l01.Concat(leaves[2]).ToArray());
        Assert.Equal(expected, SectorMerkle.Root(leaves));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]     // odd at several levels — exercises promotion
    [InlineData(16)]
    public void Prove_EveryIndexVerifies_AndTamperingFails(int sectors)
    {
        using var img = Image(sectors);
        var leaves = SectorMerkle.LeafHashes(img);
        var root = SectorMerkle.Root(leaves);

        for (long i = 0; i < sectors; i++)
        {
            var path = SectorMerkle.Prove(leaves, i);
            Assert.True(SectorMerkle.VerifySector(Sector((int)i), path, root), $"index {i} must verify");

            var tampered = Sector((int)i);
            tampered[100] ^= 0xFF;
            Assert.False(SectorMerkle.VerifySector(tampered, path, root), $"tampered index {i} must fail");
        }
    }

    [Fact]
    public void Root_ChangesWhenAnySectorChanges()
    {
        using var a = Image(10);
        var rootA = SectorMerkle.ComputeRoot(a, 2352, out _);

        using var b = Image(10);
        var buf = b.GetBuffer();
        buf[7 * 2352 + 1000] ^= 0x01;                   // one bit, one sector
        b.Position = 0;
        var rootB = SectorMerkle.ComputeRoot(b, 2352, out _);
        Assert.NotEqual(rootA, rootB);
    }

    // ---- certificate -------------------------------------------------------

    [Fact]
    public void Certificate_SignVerify_RoundTrips_AndBreaksOnEdit()
    {
        using var img = Image(9);
        var cert = DumpCertificate.Create(img, "t.bin", "2026-08-22T20:00:00Z") with
        {
            Drive = "PLEXTOR CD-R PX-W5224A",
            Firmware = "1.03",
            Settings = "retries=2 c2=on sync-gate=on",
            AuditGrade = "PASS",
            Spans = new[] { new CertifiedSpan("track 1 (data)", 0, 8, false, false, "COMPLETE") },
        };
        Assert.False(cert.Signed);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = cert.Sign(key);
        Assert.True(signed.Signed);
        Assert.True(signed.VerifySignature());

        // Any content edit invalidates the signature — including the Merkle root.
        Assert.False((signed with { AuditGrade = "FAIL" }).VerifySignature());
        Assert.False((signed with { MerkleRoot = new string('0', 64) }).VerifySignature());
        // A pinned foreign key refuses the certificate.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(signed.VerifySignature(other.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Certificate_VerifyImage_CatchesASingleFlippedBit()
    {
        using var img = Image(12);
        var cert = DumpCertificate.Create(img, "t.bin", "2026-08-22T20:00:00Z");
        img.Position = 0;
        Assert.True(cert.VerifyImage(img));

        img.GetBuffer()[5 * 2352 + 42] ^= 0x01;
        img.Position = 0;
        Assert.False(cert.VerifyImage(img));
    }

    /// <summary>
    /// The flagship property, end to end: certify and sign an image, extract a
    /// proof for one sector, then verify that sector using ONLY the slice, the
    /// proof and the certificate — the image itself is gone.
    /// </summary>
    [Fact]
    public void SliceProof_VerifiesWithoutTheImage()
    {
        DumpCertificate signed;
        SectorProof proof;
        using (var img = Image(300))
        {
            var cert = DumpCertificate.Create(img, "t.bin", "2026-08-22T20:00:00Z");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            signed = cert.Sign(key);
            img.Position = 0;
            proof = SectorProof.Create(SectorMerkle.LeafHashes(img), 137, "t.bin");
        }   // image disposed — only slice + proof + certificate remain

        Assert.True(signed.VerifySignature());
        Assert.True(proof.Verify(Sector(137), signed.MerkleRoot));
        Assert.False(proof.Verify(Sector(138), signed.MerkleRoot));     // wrong slice
        var wrong = Sector(137); wrong[0] ^= 1;
        Assert.False(proof.Verify(wrong, signed.MerkleRoot));           // tampered slice
    }

    [Fact]
    public void Certificate_And_Proof_SurviveJsonRoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dforge_cert_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var img = Image(5);
            var cert = DumpCertificate.Create(img, "t.bin", "2026-08-22T20:00:00Z");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var signed = cert.Sign(key);
            string cp = Path.Combine(dir, "t.bin.dcert.json");
            signed.Save(cp);
            var loaded = DumpCertificate.Load(cp);
            Assert.True(loaded.VerifySignature());
            Assert.Equal(signed.MerkleRoot, loaded.MerkleRoot);

            img.Position = 0;
            var proof = SectorProof.Create(SectorMerkle.LeafHashes(img), 3, "t.bin");
            string pp = Path.Combine(dir, "p.json");
            proof.Save(pp);
            Assert.True(SectorProof.Load(pp).Verify(Sector(3), loaded.MerkleRoot));
        }
        finally { Directory.Delete(dir, true); }
    }
}
