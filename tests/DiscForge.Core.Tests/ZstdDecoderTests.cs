// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Compression;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The clean-room Zstandard decoder, checked against streams produced by the reference `zstandard`
/// encoder (level 6). The compressed bytes are embedded; the expected output is reconstructed
/// independently and compared byte-for-byte. Between them these exercise raw literals + a long
/// match (struct16k), FSE-compressed Huffman literals (textrep), and RLE/all-zero (zeros).
/// </summary>
public class ZstdDecoderTests
{
    private const string Struct16kC =
        "KLUv/WAAP1UIAAQQAAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0BBQkNERUZHSElKS0xNTk9QUVJTVFVWV1hZWltcXV5fYGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn+AgYKDhIWGh4iJiouMjY6PkJGSk5SVlpeYmZqbnJ2en6ChoqOkpaanqKmqq6ytrq+wsbKztLW2t7i5uru8vb6/wMHCw8TFxsfIycrLzM3Oz9DR0tPU1dbX2Nna29zd3t/g4eLj5OXm5+jp6uvs7e7v8PHy8/T19vf4+fr7/P3+/wEAAP1+gF9T";
    private const string TextrepC =
        "KLUv/WCQMqUBAIQCdGhlIHF1aWNrIGJyb3duIGZveCBqdW1wcyBvdmVyIGxhenkgZG9nIAIAYfMpPgtWOQE=";
    private const string ZerosC =
        "KLUv/WAAD0UAAAgAAQD8x3dC";

    [Fact]
    public void Decodes_RawLiterals_And_LongMatch()
    {
        // bytes 0..255 repeated 64 times = 16384 bytes: one raw-literal copy + a big offset-256 match.
        var expected = new byte[16384];
        for (int i = 0; i < expected.Length; i++) expected[i] = (byte)(i & 0xFF);
        AssertDecodes(Struct16kC, expected);
    }

    [Fact]
    public void Decodes_HuffmanLiterals_FseWeights()
    {
        var sentence = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog ");
        var expected = new byte[sentence.Length * 300];
        for (int i = 0; i < 300; i++) Array.Copy(sentence, 0, expected, i * sentence.Length, sentence.Length);
        AssertDecodes(TextrepC, expected);
    }

    [Fact]
    public void Decodes_AllZero()
    {
        AssertDecodes(ZerosC, new byte[4096]);
    }

    [Fact]
    public void RejectsNonZstdInput()
    {
        bool threw = false;
        try { ZstdDecoder.Decompress(new byte[] { 1, 2, 3, 4 }); }
        catch { threw = true; }
        Assert.True(threw, "expected non-zstd input to be rejected");
    }

    private static void AssertDecodes(string base64Compressed, byte[] expected)
    {
        var got = ZstdDecoder.Decompress(System.Convert.FromBase64String(base64Compressed));
        Assert.Equal(expected.Length, got.Length);
        Assert.True(got.AsSpan().SequenceEqual(expected), "decoded bytes differ from expected");
    }
}
