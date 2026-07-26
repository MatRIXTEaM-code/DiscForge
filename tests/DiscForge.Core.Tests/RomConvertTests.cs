using System;
using DiscForge.Core.Rom;
using Xunit;

namespace DiscForge.Core.Tests;

public class RomConvertTests
{
    // ---- N64 byte order ----

    private static byte[] Z64(int words)
    {
        // Big-endian magic then deterministic 32-bit words.
        var d = new byte[4 + words * 4];
        d[0] = 0x80; d[1] = 0x37; d[2] = 0x12; d[3] = 0x40;
        for (int i = 4; i < d.Length; i++) d[i] = (byte)(i * 3 + 1);
        return d;
    }

    [Fact]
    public void N64_order_is_detected_from_the_magic()
    {
        Assert.Equal(N64ByteOrder.Z64, RomConvert.DetectN64Order(Z64(8)));
        var v64 = RomConvert.ConvertN64(Z64(8), N64ByteOrder.V64);
        Assert.Equal(N64ByteOrder.V64, RomConvert.DetectN64Order(v64));
    }

    [Theory]
    [InlineData(N64ByteOrder.V64)]
    [InlineData(N64ByteOrder.N64)]
    public void N64_conversion_round_trips_back_to_z64(N64ByteOrder via)
    {
        var z = Z64(64);
        var other = RomConvert.ConvertN64(z, via);
        Assert.NotEqual(z, other);                                    // actually changed the bytes
        Assert.Equal(z, RomConvert.ConvertN64(other, N64ByteOrder.Z64));
    }

    [Fact]
    public void N64_conversion_between_two_non_canonical_orders_works()
    {
        var z = Z64(32);
        var v = RomConvert.ConvertN64(z, N64ByteOrder.V64);
        var n = RomConvert.ConvertN64(v, N64ByteOrder.N64);          // v64 -> n64 directly
        Assert.Equal(RomConvert.ConvertN64(z, N64ByteOrder.N64), n);
    }

    [Fact]
    public void An_unrecognised_n64_order_is_rejected()
    {
        Assert.Throws<RomConvert.RomConvertException>(() => RomConvert.ConvertN64(new byte[64], N64ByteOrder.Z64));
    }

    // ---- SNES copier header ----

    [Fact]
    public void Snes_header_strip_and_add_round_trip()
    {
        var body = new byte[0x8000];                                 // 32 KiB, headerless (len%1024==0)
        for (int i = 0; i < body.Length; i++) body[i] = (byte)(i & 0xFF);
        Assert.False(RomConvert.HasSnesCopierHeader(body));

        var headered = RomConvert.AddSnesHeader(body);
        Assert.True(RomConvert.HasSnesCopierHeader(headered));
        Assert.Equal(body.Length + 512, headered.Length);
        Assert.Equal(body, RomConvert.StripSnesHeader(headered));
    }

    [Fact]
    public void Adding_a_header_twice_still_leaves_exactly_one()
    {
        var body = new byte[0x8000];
        var once = RomConvert.AddSnesHeader(body);
        var twice = RomConvert.AddSnesHeader(once);
        Assert.Equal(once.Length, twice.Length);
        Assert.Equal(body, RomConvert.StripSnesHeader(twice));
    }

    // ---- Genesis SMD ----

    [Fact]
    public void Genesis_bin_to_smd_round_trips_and_is_flagged_interleaved()
    {
        var bin = new byte[0x4000 * 3];                              // 3 blocks
        for (int i = 0; i < bin.Length; i++) bin[i] = (byte)((i * 5 + 7) & 0xFF);

        var smd = RomConvert.BinToSmd(bin);
        Assert.True(RomConvert.IsInterleavedSmd(smd));
        Assert.Equal(512 + bin.Length, smd.Length);
        Assert.Equal(bin, RomConvert.SmdToBin(smd));
    }

    [Fact]
    public void An_unaligned_genesis_rom_cannot_be_interleaved()
    {
        Assert.Throws<RomConvert.RomConvertException>(() => RomConvert.BinToSmd(new byte[0x4000 + 10]));
    }

    // ---- NES iNES ----

    [Fact]
    public void Nes_ines_header_is_stripped_including_a_trainer()
    {
        var rom = new byte[16 + 512 + 100];
        rom[0] = 0x4E; rom[1] = 0x45; rom[2] = 0x53; rom[3] = 0x1A;
        rom[6] = 0x04;                                               // trainer flag
        for (int i = 16 + 512; i < rom.Length; i++) rom[i] = (byte)(i & 0xFF);

        Assert.True(RomConvert.HasInesHeader(rom));
        var raw = RomConvert.StripInesHeader(rom);
        Assert.Equal(100, raw.Length);
        Assert.Equal(rom.AsSpan(16 + 512).ToArray(), raw);
    }

    [Fact]
    public void A_non_ines_file_is_rejected_by_strip()
    {
        Assert.Throws<RomConvert.RomConvertException>(() => RomConvert.StripInesHeader(new byte[64]));
    }
}
