// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Patch;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// IPS and BPS are validated the way a patch format should be: build a patch from an
/// original and a modified file, apply it to the original, and require the result to be
/// byte-identical to the modified file. Hand-built patches additionally pin the decoders
/// against the documented byte layout (RLE, truncate, the BPS copy commands), and the
/// BPS CRC-32 guards are shown to reject a patch applied to the wrong source.
/// </summary>
public class IpsBpsPatchTests
{
    private static byte[] Seeded(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    // ---- IPS ----------------------------------------------------------------

    [Fact]
    public void Ips_round_trips_scattered_edits()
    {
        var orig = Seeded(5000, 1);
        var mod = (byte[])orig.Clone();
        mod[0] ^= 0xFF; mod[1234] = 0x42; mod[4999] = 0x99;
        for (int i = 2000; i < 2050; i++) mod[i] = 0x00;   // a run -> RLE candidate

        var patch = IpsPatch.Parse(IpsPatch.Create(orig, mod));
        Assert.Contains(patch.Records, r => r.WasRle);      // the run became RLE
        Assert.Equal(mod, IpsPatch.Apply(patch, orig));
    }

    [Fact]
    public void Ips_grows_the_file_when_the_modified_is_longer()
    {
        var orig = Seeded(100, 2);
        var mod = new byte[300];
        Array.Copy(orig, mod, 100);
        for (int i = 100; i < 300; i++) mod[i] = (byte)i;

        var back = IpsPatch.Apply(IpsPatch.Parse(IpsPatch.Create(orig, mod)), orig);
        Assert.Equal(mod, back);
    }

    [Fact]
    public void Ips_truncates_when_the_modified_is_shorter()
    {
        var orig = Seeded(500, 3);
        var mod = orig.AsSpan(0, 200).ToArray();
        mod[10] ^= 0x55;

        var patch = IpsPatch.Parse(IpsPatch.Create(orig, mod));
        Assert.Equal(200, patch.TruncateLength);
        Assert.Equal(mod, IpsPatch.Apply(patch, orig));
    }

    [Fact]
    public void A_hand_built_ips_parses_to_its_records()
    {
        // PATCH | offset 0x000005 size 3 "ABC" | offset 0x000010 RLE 4x0x7F | EOF
        var b = new List<byte>();
        b.AddRange("PATCH"u8.ToArray());
        b.AddRange(new byte[] { 0x00, 0x00, 0x05, 0x00, 0x03 });
        b.AddRange("ABC"u8.ToArray());
        b.AddRange(new byte[] { 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x04, 0x7F });
        b.AddRange("EOF"u8.ToArray());

        var patch = IpsPatch.Parse(b.ToArray());
        Assert.Equal(2, patch.Records.Count);
        Assert.Equal(5, patch.Records[0].Offset);
        Assert.Equal("ABC"u8.ToArray(), patch.Records[0].Data);
        Assert.Equal(0x10, patch.Records[1].Offset);
        Assert.Equal(new byte[] { 0x7F, 0x7F, 0x7F, 0x7F }, patch.Records[1].Data);
    }

    [Fact]
    public void An_ips_without_the_magic_is_refused() =>
        Assert.Throws<IpsFormatException>(() => IpsPatch.Parse(Seeded(64, 9)));

    // ---- BPS ----------------------------------------------------------------

    [Fact]
    public void Bps_round_trips_and_verifies_its_checksums()
    {
        var source = Seeded(8000, 10);
        var target = (byte[])source.Clone();
        for (int i = 3000; i < 3400; i++) target[i] = (byte)(i * 3);   // a modified region
        var grown = new byte[9000];
        Array.Copy(target, grown, target.Length);
        for (int i = 8000; i < 9000; i++) grown[i] = (byte)(i ^ 0x5A); // and an appended tail

        var patch = BpsPatch.Parse(BpsPatch.Create(source, grown, "made by DiscForge"));
        Assert.Equal("made by DiscForge", patch.Metadata);
        Assert.Equal(source.Length, patch.SourceSize);
        Assert.Equal(grown.Length, patch.TargetSize);
        Assert.Equal(grown, BpsPatch.Apply(patch, source));
    }

    [Fact]
    public void Bps_refuses_the_wrong_source()
    {
        var source = Seeded(1000, 11);
        var target = (byte[])source.Clone();
        target[500] ^= 0xFF;
        var patch = BpsPatch.Parse(BpsPatch.Create(source, target));

        var wrong = (byte[])source.Clone();
        wrong[0] ^= 0x01;   // a different source of the same length
        Assert.Throws<BpsFormatException>(() => BpsPatch.Apply(patch, wrong));
    }

    [Fact]
    public void A_hand_built_bps_exercises_source_and_target_copy()
    {
        // source = "ABCDEFGH"; target = "ABCDEFGHABCXX":
        //   SourceRead 8  (copies ABCDEFGH from source at output pos 0..7)
        //   SourceCopy 3 from source offset 0 (relative +0) -> "ABC"
        //   TargetRead 2 literal "XX"
        byte[] source = "ABCDEFGH"u8.ToArray();
        byte[] expected = "ABCDEFGHABCXX"u8.ToArray();

        var body = new List<byte>();
        body.AddRange("BPS1"u8.ToArray());
        EncodeInto(body, source.Length);      // source size 8
        EncodeInto(body, expected.Length);    // target size 13
        EncodeInto(body, 0);                   // metadata size 0

        // SourceRead length 8 -> ((8-1)<<2)|0 = 28
        EncodeInto(body, ((8L - 1) << 2) | 0);
        // SourceCopy length 3 -> ((3-1)<<2)|2 = 10, then signed rel offset 0 -> encoded 0
        EncodeInto(body, ((3L - 1) << 2) | 2);
        EncodeInto(body, 0);                   // relative source offset 0 (sign bit 0)
        // TargetRead length 2 -> ((2-1)<<2)|1 = 5, then the two literal bytes
        EncodeInto(body, ((2L - 1) << 2) | 1);
        body.AddRange("XX"u8.ToArray());

        var withFooter = new byte[body.Count + 12];
        body.CopyTo(withFooter);
        int f = body.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(withFooter.AsSpan(f, 4), BpsPatch.Crc32(source));
        BinaryPrimitives.WriteUInt32LittleEndian(withFooter.AsSpan(f + 4, 4), BpsPatch.Crc32(expected));
        BinaryPrimitives.WriteUInt32LittleEndian(withFooter.AsSpan(f + 8, 4), BpsPatch.Crc32(withFooter.AsSpan(0, f + 8)));

        var patch = BpsPatch.Parse(withFooter);
        Assert.Equal(expected, BpsPatch.Apply(patch, source));
    }

    [Fact]
    public void A_bps_without_the_magic_is_refused() =>
        Assert.Throws<BpsFormatException>(() => BpsPatch.Parse(Seeded(64, 12)));

    // Beat variable-length integer encoder, mirrored here to hand-build a fixture.
    private static void EncodeInto(List<byte> outp, long number)
    {
        while (true)
        {
            byte x = (byte)(number & 0x7F);
            number >>= 7;
            if (number == 0) { outp.Add((byte)(0x80 | x)); break; }
            outp.Add(x);
            number -= 1;
        }
    }
}
