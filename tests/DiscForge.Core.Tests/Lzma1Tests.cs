// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text;
using DiscForge.Core.Compression;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The clean-room LZMA1 decoder, proven against streams produced by liblzma — the reference
/// implementation — across data shapes (repetitive text, incompressible pseudo-random, mixed) and
/// property variants (default lc3/lp0/pb2, lc0/lp2/pb0, lc4/lp0/pb1). Each vector's plaintext is
/// regenerated here with the same deterministic generators and pinned by SHA-256, so a generator
/// mismatch fails loudly before any decode comparison.
/// </summary>
public class Lzma1Tests
{
    // ---- deterministic generators (mirrored in the vector-producing script) ----

    private static byte[] Lcg(int n)
    {
        var outb = new byte[n];
        long x = 12345;
        for (int i = 0; i < n; i++)
        {
            x = (x * 1103515245 + 12345) & 0x7fffffff;
            outb[i] = (byte)((x >> 16) & 0xFF);
        }
        return outb;
    }

    private static byte[] Text(int n)
    {
        var s = Encoding.ASCII.GetBytes("DiscForge preserves optical media provably or declines. ");
        var outb = new byte[n];
        for (int i = 0; i < n; i++) outb[i] = s[i % s.Length];
        return outb;
    }

    private static byte[] Mixed(int n)
    {
        var a = Lcg(n / 4);
        var b = Text(n / 4);
        var outb = new byte[n];
        int p = 0;
        foreach (var part in new[] { a, b, a, b })
            foreach (var by in part)
            {
                if (p >= n) return outb;
                outb[p++] = by;
            }
        return outb;
    }

    private static void Check(byte[] data, string sha256, string propsB64, string streamB64)
    {
        Assert.Equal(sha256, System.Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
        var decoded = Lzma1.Decode(System.Convert.FromBase64String(propsB64),
                                   System.Convert.FromBase64String(streamB64), data.Length);
        Assert.Equal(data, decoded);
    }

    [Fact]
    public void Decodes_repetitive_text_with_default_properties() =>
        Check(Text(5000), "e03cd0f0dcf7d210d451d7353834f17130fd4ce40002bf03c0e77d59257634ad",
            "XQAAgAA=",
            "ACIaSmZD4wpVQARK45wEExWsnbBA1/KzL5Ei49UhH8zga6d0TzPvZH/CEJcDr3wJx7aSkx4G78EkNXCzyr2gAO2eQKsjiF/uI8J6+ObjeHoCSBiO6///CjgAAA==");

    [Fact]
    public void Decodes_incompressible_data_with_default_properties() =>
        Check(Lcg(2048), "dd224044ac13ce36d08d0e35c34a1d9e66777a4a1f0d44856ace58a2fea48ae0",
            "XQAAgAA=",
            "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");

    [Fact]
    public void Decodes_custom_lc0_lp2_pb0_properties() =>
        Check(Text(4000), "6efafdeb6ce13be6013b653d2b567e815c25ea4a2f4120f46e6a64bac06d89ac",
            "EgAAAQA=",
            "ACIbHcPfSPovAOcTvLMetkijSZRuIOnJOjtKMXxO3yGnuW68kD4t9z9KLuALtWYAyzeDhmQActB6W1w+ASDAeBAxRpwle/N+HuF6e1pJQyKLIlL/zHAAAA==");

    [Fact]
    public void Decodes_custom_lc4_lp0_pb1_properties()
    {
        var data = Mixed(8192);
        Assert.Equal("4e4b0e291a92820e73a8116f29491a27a3a5d014ba04b566bb2ce45ccd050b30",
            System.Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
        var stream = System.Convert.FromBase64String(
            "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");
        var decoded = Lzma1.Decode(System.Convert.FromBase64String("MQAAAQA="), stream, data.Length);
        Assert.Equal(data, decoded);
    }

    [Fact]
    public void An_early_end_of_stream_marker_is_corruption_not_a_short_result()
    {
        // A valid 5000-byte stream decoded with a LARGER declared size hits its end-of-stream marker
        // early — that must throw, never return a silently zero-padded buffer.
        var props = System.Convert.FromBase64String("XQAAgAA=");
        var stream = System.Convert.FromBase64String(
            "ACIaSmZD4wpVQARK45wEExWsnbBA1/KzL5Ei49UhH8zga6d0TzPvZH/CEJcDr3wJx7aSkx4G78EkNXCzyr2gAO2eQKsjiF/uI8J6+ObjeHoCSBiO6///CjgAAA==");
        Assert.Throws<InvalidDataException>(() => Lzma1.Decode(props, stream, 6000));
    }

    [Fact]
    public void Rejects_invalid_properties_and_truncated_streams()
    {
        Assert.Throws<InvalidDataException>(() => Lzma1.Decode(new byte[] { 255, 0, 0, 0, 1 }, new byte[16], 10));
        Assert.Throws<InvalidDataException>(() => Lzma1.Decode(new byte[] { 0x5D, 0, 0, 0x80, 0 }, new byte[] { 0, 1, 2 }, 10));
    }

    [Fact]
    public void Decodes_zero_output_size_to_empty()
    {
        Assert.Empty(Lzma1.Decode(new byte[] { 0x5D, 0, 0, 0x80, 0 }, System.Array.Empty<byte>(), 0));
    }
}
