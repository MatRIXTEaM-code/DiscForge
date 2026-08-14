// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The ECM codec: strip a raw CD image to <c>.ecm</c> and rebuild it byte-for-byte.
/// The load-bearing checks are (1) a full round trip over a mixed image of every
/// sector type plus a non-sector (audio-like) literal tail, and (2) feeding every
/// rebuilt Mode 1 / Mode 2 Form 1 sector through the INDEPENDENT
/// <see cref="EdcEcc.VerifyMode1"/> / <see cref="EdcEcc.VerifyMode2Form1"/> syndrome
/// evaluator — so the regenerated EDC/ECC is proven algebraically valid, not merely
/// equal to what the encoder assumed.
/// </summary>
public class EcmTests
{
    // Build a valid raw sector of the given kind at absolute LBA, filled with a
    // repeatable data pattern. Mode 2 addresses follow LBA+150 so a contiguous run
    // reconstructs exactly under the codec's running-counter rule.
    private static byte[] Sector(int lba, int mode, int form, int seed)
    {
        var s = new byte[2352];
        RawSectorBuilder.WriteSync(s);
        int a = lba + 150;
        s[0x0C] = Bcd.From(a / (60 * 75));
        s[0x0D] = Bcd.From(a / 75 % 60);
        s[0x0E] = Bcd.From(a % 75);

        var rng = new Random(seed);
        if (mode == 1)
        {
            s[0x0F] = 0x01;
            for (int i = 0; i < 2048; i++) s[0x10 + i] = (byte)rng.Next(256);
            EdcEcc.FillMode1(s);
        }
        else
        {
            s[0x0F] = 0x02;
            // Subheader: submode bit 0x20 selects Form 2. Duplicated at 0x10 and 0x14.
            byte submode = (byte)(form == 2 ? 0x20 : 0x08);
            Span<byte> sub = stackalloc byte[4] { 0x00, 0x00, submode, 0x00 };
            sub.CopyTo(s.AsSpan(0x10, 4));
            sub.CopyTo(s.AsSpan(0x14, 4));
            int dataLen = form == 2 ? 2324 : 2048;
            for (int i = 0; i < dataLen; i++) s[0x18 + i] = (byte)rng.Next(256);
            if (form == 2) EdcEcc.FillMode2Form2(s);
            else EdcEcc.FillMode2Form1(s);
        }
        return s;
    }

    private static byte[] MixedImage(out int sectorCount)
    {
        var sectors = new List<byte[]>();
        int lba = 0;
        for (int i = 0; i < 3; i++) sectors.Add(Sector(lba++, 1, 0, 100 + i));   // Mode 1
        for (int i = 0; i < 4; i++) sectors.Add(Sector(lba++, 2, 1, 200 + i));   // Form 1
        for (int i = 0; i < 2; i++) sectors.Add(Sector(lba++, 2, 2, 300 + i));   // Form 2
        sectorCount = sectors.Count;

        using var ms = new MemoryStream();
        foreach (var s in sectors) ms.Write(s, 0, s.Length);
        // A non-sector "audio" tail: raw bytes that must survive as a literal.
        var tail = new byte[5000];
        new Random(7).NextBytes(tail);
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    [Fact]
    public void Round_trip_rebuilds_a_mixed_image_byte_for_byte()
    {
        byte[] original = MixedImage(out int sectorCount);

        using var ecm = new MemoryStream();
        EcmCodec.Encode(new MemoryStream(original), ecm);

        // It's an ECM file and it actually saved space.
        var ecmBytes = ecm.ToArray();
        Assert.Equal(new byte[] { (byte)'E', (byte)'C', (byte)'M', 0 }, ecmBytes[..4]);
        Assert.True(ecmBytes.Length < original.Length, "ECM should be smaller than the raw image.");

        ecm.Position = 0;
        using var outStream = new MemoryStream();
        long written = EcmCodec.Decode(ecm, outStream);

        Assert.Equal(original.Length, written);
        Assert.Equal(original, outStream.ToArray());

        // Independent oracle: every rebuilt Mode 1 / Form 1 sector has valid EDC+ECC.
        byte[] rebuilt = outStream.ToArray();
        for (int i = 0; i < sectorCount; i++)
        {
            var s = rebuilt.AsSpan(i * 2352, 2352);
            byte mode = s[0x0F];
            if (mode == 0x01)
            {
                var (edcOk, eccOk) = EdcEcc.VerifyMode1(s);
                Assert.True(edcOk && eccOk, $"Mode 1 sector {i} failed independent verify.");
            }
            else if (mode == 0x02 && (s[0x12] & 0x20) == 0)
            {
                var (edcOk, eccOk) = EdcEcc.VerifyMode2Form1(s);
                Assert.True(edcOk && eccOk, $"Form 1 sector {i} failed independent verify.");
            }
        }
    }

    [Fact]
    public void A_purely_non_sector_input_round_trips_as_literals()
    {
        var data = new byte[9999];
        new Random(11).NextBytes(data);

        using var ecm = new MemoryStream();
        EcmCodec.Encode(new MemoryStream(data), ecm);
        ecm.Position = 0;

        using var outStream = new MemoryStream();
        EcmCodec.Decode(ecm, outStream);
        Assert.Equal(data, outStream.ToArray());
    }

    [Fact]
    public void Decode_rejects_a_file_without_the_magic()
    {
        using var notEcm = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        using var outStream = new MemoryStream();
        Assert.Throws<EcmCodec.EcmFormatException>(() => EcmCodec.Decode(notEcm, outStream));
    }

    [Fact]
    public void A_corrupt_trailing_edc_is_detected()
    {
        byte[] original = MixedImage(out _);
        using var ecm = new MemoryStream();
        EcmCodec.Encode(new MemoryStream(original), ecm);
        var bytes = ecm.ToArray();
        bytes[^1] ^= 0xFF;                      // damage the whole-file EDC

        using var outStream = new MemoryStream();
        Assert.Throws<EcmCodec.EcmFormatException>(
            () => EcmCodec.Decode(new MemoryStream(bytes), outStream));
    }

    [Fact]
    public void Form2_edc_helper_matches_an_independent_recompute()
    {
        var s = Sector(42, 2, 2, 999);
        uint edc = EdcEcc.ComputeEdc(s.AsSpan(0x10, 2332));
        uint stored = (uint)s[0x92C] | ((uint)s[0x92D] << 8)
                    | ((uint)s[0x92E] << 16) | ((uint)s[0x92F] << 24);
        Assert.Equal(edc, stored);
    }
}
