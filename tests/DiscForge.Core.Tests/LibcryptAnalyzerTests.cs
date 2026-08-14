// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.PlayStation;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

public class LibcryptAnalyzerTests
{
    private const int N = 2000;

    private static byte[] AuthorSub(Func<int, byte[]> qFor)
    {
        var sub = new byte[N * 96];
        for (int s = 0; s < N; s++)
        {
            var q = qFor(s);
            for (int i = 0; i < 96; i++)
                if ((q[i >> 3] & (0x80 >> (i & 7))) != 0)
                    sub[s * 96 + i] |= 0x40;
        }
        return sub;
    }

    private static byte[] GoodQ(int s) =>
        SubQ.Position(QControl.Data, 1, 1, Msf.FromSectors(s), Msf.FromSectors(s + 150));

    [Fact]
    public void Clean_disc_has_no_libcrypt()
    {
        var r = LibcryptAnalyzer.Scan(AuthorSub(GoodQ));
        Assert.False(r.Present);
        Assert.Equal(LibcryptVariant.None, r.Variant);
        Assert.Equal(0, r.Fingerprint);
    }

    [Fact]
    public void Broken_crc_sectors_are_crc_type()
    {
        int[] corrupt = { 600, 601, 1300, 1301 };
        var sub = AuthorSub(GoodQ);
        foreach (var s in corrupt) sub[s * 96 + 30] ^= 0x40;   // wreck the Q data → CRC fails

        var r = LibcryptAnalyzer.Scan(sub);
        Assert.True(r.Present);
        Assert.Equal(LibcryptVariant.CrcType, r.Variant);
        Assert.Equal(corrupt.Length, r.Count);
        Assert.All(r.Sectors, s => Assert.False(s.CrcValid));
        Assert.All(r.Sectors, s => Assert.NotEqual(0, s.CrcDelta));
    }

    [Fact]
    public void Valid_crc_but_wrong_address_is_address_type()
    {
        const int bad = 900;
        var sub = AuthorSub(s => s == bad
            ? SubQ.Position(QControl.Data, 1, 1, Msf.FromSectors(s), Msf.FromSectors(s + 150 + 3000))
            : GoodQ(s));

        var r = LibcryptAnalyzer.Scan(sub);
        Assert.Equal(LibcryptVariant.AddressType, r.Variant);
        Assert.Single(r.Sectors);
        Assert.True(r.Sectors[0].CrcValid);
        Assert.True(r.Sectors[0].AddressAltered);
    }

    [Fact]
    public void A_disc_with_both_kinds_is_mixed()
    {
        var sub = AuthorSub(s => s == 900
            ? SubQ.Position(QControl.Data, 1, 1, Msf.FromSectors(s), Msf.FromSectors(s + 150 + 3000))
            : GoodQ(s));
        sub[600 * 96 + 30] ^= 0x40;   // add a broken-CRC sector

        var r = LibcryptAnalyzer.Scan(sub);
        Assert.Equal(LibcryptVariant.Mixed, r.Variant);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void Paired_sectors_are_counted()
    {
        var sub = AuthorSub(GoodQ);
        foreach (var s in new[] { 600, 601 }) sub[s * 96 + 30] ^= 0x40;   // within the pair window
        var r = LibcryptAnalyzer.Scan(sub);
        Assert.Equal(2, r.PairedSectors);
    }

    [Fact]
    public void Fingerprint_is_stable_and_translation_invariant()
    {
        // Two clean discs, each carrying the SAME relative tamper shape {+0, +1, +100} but at a
        // different absolute offset. The fingerprint keys on relative position, so both must match.
        var subA = AuthorSub(GoodQ);
        foreach (var s in new[] { 600, 601, 700 }) subA[s * 96 + 30] ^= 0x40;
        var subB = AuthorSub(GoodQ);
        foreach (var s in new[] { 800, 801, 900 }) subB[s * 96 + 30] ^= 0x40;

        var a = LibcryptAnalyzer.Scan(subA);
        var b = LibcryptAnalyzer.Scan(subB);
        Assert.NotEqual(0, a.Fingerprint);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Equal(a.CrcDeltaXor, b.CrcDeltaXor);
    }

    [Fact]
    public void A_flood_of_bad_frames_is_refused_as_damage()
    {
        var sub = AuthorSub(GoodQ);
        for (int s = 0; s < N; s += 5) sub[s * 96 + 30] ^= 0x40;
        Assert.Throws<InvalidDataException>(() => LibcryptAnalyzer.Scan(sub));
    }

    [Fact]
    public void ToSbi_bridges_to_the_emulator_sidecar()
    {
        int[] corrupt = { 600, 601 };
        var sub = AuthorSub(GoodQ);
        foreach (var s in corrupt) sub[s * 96 + 30] ^= 0x40;

        var r = LibcryptAnalyzer.Scan(sub);
        var doc = LibcryptAnalyzer.ToSbi(r);
        Assert.Equal(corrupt.Length, doc.Entries.Count);

        // The bridge must agree with the SBI writer reading the same subchannel.
        var direct = Sbi.FromSubchannel(sub, startLba: 0);
        Assert.Equal(direct.Entries.Count, doc.Entries.Count);
        Assert.Equal(direct.Entries[0].AbsSectors, doc.Entries[0].AbsSectors);

        var bytes = Sbi.Write(doc);
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual(Sbi.Magic));
    }

    [Fact]
    public void Key_material_is_the_xor_of_the_per_sector_deltas()
    {
        var sectors = new List<LibcryptSector>
        {
            new(600, Msf.FromSectors(750), Msf.FromSectors(750), 0x1234, 0x1200, false, false, new byte[10]),
            new(601, Msf.FromSectors(751), Msf.FromSectors(751), 0x00FF, 0x000F, false, false, new byte[10]),
        };
        var r = LibcryptAnalyzer.Build(sectors);
        ushort expected = (ushort)((0x1234 ^ 0x1200) ^ (0x00FF ^ 0x000F));
        Assert.Equal(expected, r.CrcDeltaXor);
        Assert.Equal(LibcryptVariant.CrcType, r.Variant);
    }

    [Fact]
    public void An_empty_report_summarises_cleanly()
    {
        var r = LibcryptAnalyzer.Build(new List<LibcryptSector>());
        Assert.False(r.Present);
        Assert.Contains("No LibCrypt", r.Summary());
    }
}
