// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using DiscForge.Core.Convert;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The lossless-conversion certificate — a shareable proof that two images decode to the same raw
/// bytes. A concrete dec(enc(x)) ≡ x attestation for a round-trip: when lossless, one content
/// SHA-256 stands for both sides; when not, the certificate pinpoints the divergence and refuses
/// the lossless verdict.
/// </summary>
public class ConversionCertificateTests
{
    private static byte[] Disc(int sectors, int seed)
    {
        var b = new byte[sectors * ConversionVerify.CdSector];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void Identical_decode_certifies_lossless_with_a_single_content_hash()
    {
        var original = Disc(10, 3);
        var roundTripped = (byte[])original.Clone();   // decode of a lossless conversion

        var cert = ConversionCertificate.Build(original, roundTripped, "game.cue", "game.chd",
            stamp: "2026-08-12T00:00:00Z");

        Assert.True(cert.Lossless);
        Assert.Equal(10, cert.Sectors);
        // The content hash equals an independent SHA-256 of the bytes, and both sides share it.
        string expected = System.Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        Assert.Equal(expected, cert.ContentSha256);
        Assert.Equal(cert.Sha256A, cert.Sha256B);
        Assert.Contains("LOSSLESS", cert.Summary);
    }

    [Fact]
    public void A_changed_byte_is_not_lossless_and_has_no_shared_hash()
    {
        var original = Disc(10, 3);
        var altered = (byte[])original.Clone();
        altered[5000] ^= 0xFF;

        var cert = ConversionCertificate.Build(original, altered, "a.bin", "b.bin");

        Assert.False(cert.Lossless);
        Assert.Null(cert.ContentSha256);
        Assert.NotEqual(cert.Sha256A, cert.Sha256B);
        Assert.Equal(5000, cert.FirstDiffOffset);
    }

    [Fact]
    public void A_dropped_sector_is_reported_as_a_size_delta()
    {
        var original = Disc(10, 7);
        var shortened = original.AsSpan(0, 9 * ConversionVerify.CdSector).ToArray();

        var cert = ConversionCertificate.Build(original, shortened, "full.bin", "short.bin");

        Assert.False(cert.Lossless);
        Assert.Equal(original.Length, cert.LengthA);
        Assert.Equal(shortened.Length, cert.LengthB);
        Assert.Contains("sizes differ", cert.Summary);
    }

    [Fact]
    public void Certificate_renders_deterministic_json_and_html()
    {
        var original = Disc(4, 1);
        var cert = ConversionCertificate.Build(original, (byte[])original.Clone(), "src.cue", "dst.chd",
            stamp: "2026-08-12T12:00:00Z");

        string json = ConversionCertificate.Json(cert);
        Assert.Contains("\"verdict\":\"LOSSLESS\"", json);
        Assert.Contains($"\"contentSha256\":\"{cert.ContentSha256}\"", json);
        Assert.Contains("\"sectors\":4", json);

        string html = ConversionCertificate.Html(cert);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("LOSSLESS", html);
        Assert.Contains(cert.ContentSha256!, html);
        Assert.Contains("2026-08-12T12:00:00Z", html);
        Assert.DoesNotContain("<script", html);
    }
}
