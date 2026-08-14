// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Two hard-disk CHD extras: the 'huff' (order-0 Huffman) hunk codec, and multi-level
/// parent chains. All fixtures are real chdman images validated byte-exact by the CHD
/// SHA-1 — a huff-compressed image, and a grandparent → parent → child chain where the
/// child needs the parent AND the grandparent to resolve.
/// </summary>
public class ChdChainAndHuffTests
{
    private const string HuffB64 =
        "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";

    private const string GrandB64 =
        "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";

    private const string ParentB64 =
        "TUNvbXBySEQAAAB8AAAABXpsaWJsem1hAAAAAAAAAAAAAAAAAACAAAAAAAAAAAFSAAAAAAAAAHwAABAAAAACAHZ29Bn5TcPdSW8tfPK3CkBl1PcYdSgb14" +
        "6vRBVx/DsoL1mJ0a6EiZziun7Bm/NLN1mvyh6QSGjJkdaLeEdEREQBAAAfAAAAAAAAAABDWUxTOjEsSEVBRFM6MixTRUNTOjMyLEJQUzo1MTIAAAqF//Hs" +
        "juEPERGYfEgHR69+K14qJTex2zwymK10EK9H8j/W2H0lWRv3HjB6VryyK+TFJpLEytxtW41st2ouLBKslqfcS+txgfpSoMhphy2A4CYykIDKlwWVONRUyY" +
        "B9sf6kltCcbERAy3yKTT+JYOGBvVBE8DCFBH7RjU5xGcG2Qxxu+VdrCtxzXIWBCPyT6xzkQR2A6plG6VRJkRONvgJm5ABGdgAAAAAMAAAAAACrt18IAAAA" +
        "AjEBMQAREBsinZog";

    private const string ChildB64 =
        "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";

    private const string FlacB64 =
        "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" +
        "AAAAAKqgcgsAAAAREMLEnRVYlzxg";

    private static byte[] Huff() => System.Convert.FromBase64String(HuffB64);
    private static byte[] Flac() => System.Convert.FromBase64String(FlacB64);
    private static byte[] Grand() => System.Convert.FromBase64String(GrandB64);
    private static byte[] Parent() => System.Convert.FromBase64String(ParentB64);
    private static byte[] Child() => System.Convert.FromBase64String(ChildB64);

    [Fact]
    public void A_huff_compressed_hard_disk_chd_extracts_and_verifies()
    {
        var raw = ChdHdExtractor.Extract(Huff());
        Assert.Equal(2 * 4096, raw.Length);   // does not throw => SHA-1 verified
    }

    [Fact]
    public void A_full_grandparent_chain_resolves_and_verifies()
    {
        // chain is nearest-first: the child's parent, then the grandparent.
        var raw = ChdHdExtractor.Extract(Child(), new[] { Parent(), Grand() });
        Assert.Equal(8 * 4096, raw.Length);
    }

    [Fact]
    public void A_chain_missing_the_grandparent_is_declined()
    {
        // The parent itself is a delta of the grandparent, so parent-only is not enough.
        Assert.Throws<ChdFormatException>(() => ChdHdExtractor.Extract(Child(), new[] { Parent() }));
    }

    [Fact]
    public void A_flac_compressed_hard_disk_chd_extracts_and_verifies()
    {
        var raw = ChdHdExtractor.Extract(Flac());
        Assert.Equal(2 * 4096, raw.Length);   // does not throw => SHA-1 verified
    }
}
