// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Identify;
using DiscForge.Core.Rom;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Cartridge-ROM identification + No-Intro hashing. For each core platform a minimal ROM buffer is
/// hand-built with a valid header — correct magic/console name, a known title, game code and region
/// and, where the format has one, a checksum that recomputes — so the identify and checksum paths
/// are exercised. Covers N64 in all three byte orders, SNES LoROM/HiROM selection by checksum, the
/// SNES 512-byte copier header (stripped for hashing), the Game Boy header-checksum recompute and
/// the NES mapper decode, plus best-effort readers and the malformed-input contract.
/// </summary>
public class RomIdentifyTests
{
    // ---- builders -----------------------------------------------------------

    private static byte[] New(int size) => new byte[size];

    private static void PutAscii(byte[] b, int at, string s)
    {
        for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i];
    }

    private static void PutU16Le(byte[] b, int at, ushort v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
    private static void PutU16Be(byte[] b, int at, ushort v) { b[at] = (byte)(v >> 8); b[at + 1] = (byte)v; }
    private static void PutU32Be(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    // ---- N64 ----------------------------------------------------------------

    private static byte[] BuildN64Z64()
    {
        var b = New(0x1000);
        b[0] = 0x80; b[1] = 0x37; b[2] = 0x12; b[3] = 0x40;   // big-endian native
        PutU32Be(b, 0x10, 0xAABBCCDD);                        // CRC1
        PutU32Be(b, 0x14, 0x11223344);                        // CRC2
        PutAscii(b, 0x20, "SUPER MARIO 64");                  // 20-byte internal name
        PutAscii(b, 0x3B, "NSME");                            // game code; [3]='E' -> USA
        return b;
    }

    private static byte[] ToV64(byte[] z64)
    {
        var v = (byte[])z64.Clone();
        for (int i = 0; i + 1 < v.Length; i += 2) (v[i], v[i + 1]) = (v[i + 1], v[i]);
        return v;
    }

    private static byte[] ToN64Le(byte[] z64)
    {
        var n = (byte[])z64.Clone();
        for (int i = 0; i + 3 < n.Length; i += 4)
        {
            (n[i], n[i + 3]) = (n[i + 3], n[i]);
            (n[i + 1], n[i + 2]) = (n[i + 2], n[i + 1]);
        }
        return n;
    }

    [Fact]
    public void N64_big_endian_header_is_read()
    {
        var id = RomIdentify.Identify(BuildN64Z64());
        Assert.Equal("Nintendo 64", id.Platform);
        Assert.Equal("SUPER MARIO 64", id.Title);
        Assert.Equal("NSME", id.GameCode);
        Assert.Equal("USA", id.Region);
        Assert.Equal("AABBCCDD", id.Extra["CRC1"]);
        Assert.Contains("big-endian", id.Extra["ByteOrder"]);
    }

    [Fact]
    public void N64_all_three_byte_orders_normalise_to_the_same_result()
    {
        var z = RomIdentify.Identify(BuildN64Z64());
        var v = RomIdentify.Identify(ToV64(BuildN64Z64()));
        var n = RomIdentify.Identify(ToN64Le(BuildN64Z64()));

        foreach (var id in new[] { v, n })
        {
            Assert.Equal(z.Platform, id.Platform);
            Assert.Equal(z.Title, id.Title);
            Assert.Equal(z.GameCode, id.GameCode);
            Assert.Equal(z.Region, id.Region);
        }
        Assert.Contains("byte-swapped", v.Extra["ByteOrder"]);
        Assert.Contains("little-endian", n.Extra["ByteOrder"]);
    }

    // ---- SNES ---------------------------------------------------------------

    private static byte[] BuildSnes(int headerOffset, int totalSize, int prefix = 0)
    {
        var b = New(prefix + totalSize);
        int hdr = prefix + headerOffset;
        PutAscii(b, hdr + 0x00, "SUPER TESTROM        ");       // 21-byte title
        b[hdr + 0x15] = 0x20;                                   // map mode
        b[hdr + 0x17] = 0x0A;                                   // ROM size code
        b[hdr + 0x18] = 0x03;                                   // RAM size code
        b[hdr + 0x19] = 0x01;                                   // country = USA
        PutU16Le(b, hdr + 0x1C, 0xABCD);                        // checksum
        PutU16Le(b, hdr + 0x1E, 0x5432);                        // complement (sum = 0xFFFF)
        return b;
    }

    [Fact]
    public void Snes_lorom_is_selected_by_a_valid_checksum()
    {
        var id = RomIdentify.Identify(BuildSnes(0x7FC0, 0x8000));
        Assert.Equal("SNES", id.Platform);
        Assert.Equal("SUPER TESTROM", id.Title);
        Assert.Equal("LoROM", id.Extra["Layout"]);
        Assert.Equal("USA", id.Region);
    }

    [Fact]
    public void Snes_hirom_is_selected_when_only_the_hirom_header_validates()
    {
        var id = RomIdentify.Identify(BuildSnes(0xFFC0, 0x10000));
        Assert.Equal("SNES", id.Platform);
        Assert.Equal("HiROM", id.Extra["Layout"]);
    }

    [Fact]
    public void Snes_512_byte_copier_header_is_skipped_for_parsing()
    {
        var rom = BuildSnes(0x7FC0, 0x8000, prefix: 512);       // len % 1024 == 512
        var id = RomIdentify.Identify(rom);
        Assert.Equal("SNES", id.Platform);
        Assert.Equal("LoROM", id.Extra["Layout"]);
        Assert.Contains(id.Warnings, w => w.Contains("copier header"));
    }

    // ---- Genesis ------------------------------------------------------------

    private static byte[] BuildGenesis(bool overseasOnly = false, int size = 0x400)
    {
        var b = New(size);
        PutAscii(b, 0x100, "SEGA MEGA DRIVE ");
        if (!overseasOnly) PutAscii(b, 0x120, "SONIC THE HEDGEHOG");
        PutAscii(b, 0x150, "SONIC THE HEDGEHOG (OVERSEAS)");
        PutAscii(b, 0x180, "GM 00001009-00");
        PutU16Be(b, 0x18E, 0xBEEF);
        PutAscii(b, 0x1F0, "U");
        return b;
    }

    [Fact]
    public void Genesis_header_is_read()
    {
        var id = RomIdentify.Identify(BuildGenesis());
        Assert.Equal("Sega Mega Drive / Genesis", id.Platform);
        Assert.Equal("SONIC THE HEDGEHOG", id.Title);
        Assert.Equal("GM 00001009-00", id.GameCode);
        Assert.Equal("USA", id.Region);
        Assert.Equal("BEEF", id.Extra["Checksum"]);
    }

    [Fact]
    public void Genesis_smd_interleaved_dump_is_deinterleaved_and_flagged()
    {
        var flat = BuildGenesis(size: 0x4000);                  // one 16 KiB block (de-interleave unit)
        var smd = New(512 + flat.Length);
        smd[8] = 0xAA; smd[9] = 0xBB;                           // SMD marker
        for (int blk = 0; blk * 0x4000 < flat.Length; blk++)
        {
            // interleave: odd bytes to first 8K half, even bytes to second 8K half
            int dst = 512 + blk * 0x4000;
            for (int i = 0; i < 0x2000; i++)
            {
                int even = blk * 0x4000 + i * 2;
                int odd = even + 1;
                smd[dst + i] = odd < flat.Length ? flat[odd] : (byte)0;
                smd[dst + 0x2000 + i] = even < flat.Length ? flat[even] : (byte)0;
            }
        }
        var id = RomIdentify.Identify(smd);
        Assert.Equal("Sega Mega Drive / Genesis", id.Platform);
        Assert.Equal("SONIC THE HEDGEHOG", id.Title);
        Assert.Contains("SMD", id.Extra["Interleave"]);
    }

    // ---- Game Boy -----------------------------------------------------------

    private static byte[] BuildGb(bool goodLogo = true, byte cgb = 0x00, bool fixHeaderChecksum = true)
    {
        var b = New(0x8000);
        if (goodLogo) System.Array.Copy(GameBoyRom.NintendoLogo, 0, b, 0x104, GameBoyRom.NintendoLogo.Length);
        PutAscii(b, 0x134, "TETRIS");
        b[0x143] = cgb;
        b[0x146] = 0x00;                                        // SGB
        b[0x147] = 0x00;                                        // ROM only
        b[0x148] = 0x00;                                        // 32 KiB
        b[0x149] = 0x00;                                        // no RAM
        b[0x14A] = 0x00;                                        // Japan
        PutU16Be(b, 0x14E, 0x1234);                             // global checksum
        b[0x14D] = fixHeaderChecksum ? GameBoyRom.ComputeHeaderChecksum(b) : (byte)0xFF;
        return b;
    }

    [Fact]
    public void Gb_header_with_a_valid_checksum_has_no_warnings()
    {
        var id = RomIdentify.Identify(BuildGb());
        Assert.Equal("Game Boy", id.Platform);
        Assert.Equal("TETRIS", id.Title);
        Assert.Equal("Japan", id.Region);
        Assert.Empty(id.Warnings);
    }

    [Fact]
    public void Gbc_only_flag_selects_game_boy_color()
    {
        var id = RomIdentify.Identify(BuildGb(cgb: 0xC0));
        Assert.Equal("Game Boy Color", id.Platform);
        Assert.Contains("GBC-only", id.Extra["CgbFlag"]);
    }

    [Fact]
    public void Gb_bad_header_checksum_is_reported_as_a_warning()
    {
        var id = RomIdentify.Identify(BuildGb(fixHeaderChecksum: false));
        Assert.Equal("Game Boy", id.Platform);            // good logo still identifies it
        Assert.Contains(id.Warnings, w => w.Contains("header checksum mismatch"));
    }

    [Fact]
    public void Gb_bad_nintendo_logo_is_reported_as_a_warning()
    {
        var id = RomIdentify.Identify(BuildGb(goodLogo: false));
        Assert.Contains(id.Warnings, w => w.Contains("logo"));
    }

    // ---- GBA ----------------------------------------------------------------

    private static byte[] BuildGba()
    {
        var b = New(0x400);
        System.Array.Copy(GbaRom.NintendoLogo, 0, b, 0x04, GbaRom.NintendoLogo.Length);
        PutAscii(b, 0xA0, "TESTGBA");
        PutAscii(b, 0xAC, "AZLE");                              // game code; [3]='E' -> USA
        PutAscii(b, 0xB0, "01");                                // maker
        b[0xB2] = 0x96;                                         // fixed byte
        b[0xBD] = GbaRom.ComputeHeaderChecksum(b);
        return b;
    }

    [Fact]
    public void Gba_header_is_read_and_checksum_matches()
    {
        var id = RomIdentify.Identify(BuildGba());
        Assert.Equal("Game Boy Advance", id.Platform);
        Assert.Equal("TESTGBA", id.Title);
        Assert.Equal("AZLE", id.GameCode);
        Assert.Equal("USA", id.Region);
        Assert.Empty(id.Warnings);
    }

    // ---- NES ----------------------------------------------------------------

    [Fact]
    public void Nes_mapper_number_is_decoded_from_flags6_and_flags7()
    {
        var b = New(0x4010);
        PutAscii(b, 0, "NES");
        b[3] = 0x1A;
        b[4] = 0x02;                                            // PRG = 32 KiB
        b[5] = 0x01;                                            // CHR = 8 KiB
        b[6] = 0x41;                                            // low mapper nibble 4, vertical mirroring
        b[7] = 0x30;                                            // high mapper nibble 3 -> mapper 0x34 = 52
        var id = RomIdentify.Identify(b);
        Assert.Equal("NES / Famicom", id.Platform);
        Assert.Equal("52", id.Extra["Mapper"]);
        Assert.Equal("32 KiB", id.Extra["PrgRom"]);
        Assert.Equal("8 KiB", id.Extra["ChrRom"]);
        Assert.Equal("vertical", id.Extra["Mirroring"]);
    }

    [Fact]
    public void Nes20_container_is_detected_and_extends_the_mapper()
    {
        var b = New(0x4010);
        PutAscii(b, 0, "NES");
        b[3] = 0x1A;
        b[4] = 0x01;
        b[7] = 0x08;                                            // (byte7 & 0x0C) == 0x08 -> NES 2.0
        b[8] = 0x01;                                            // mapper bits 8..11 -> +0x100
        var id = RomIdentify.Identify(b);
        Assert.Equal("NES 2.0", id.Extra["Container"]);
        Assert.Equal("256", id.Extra["Mapper"]);
    }

    // ---- extended (best-effort) ---------------------------------------------

    [Fact]
    public void Master_system_signature_is_identified()
    {
        var b = New(0x8000);
        PutAscii(b, 0x7FF0, "TMR SEGA");
        b[0x7FF0 + 0x0F] = 0x40;                                // region nibble 4 -> SMS Export
        var id = RomIdentify.Identify(b);
        Assert.Equal("Sega Master System", id.Platform);
        Assert.Equal("Export", id.Region);
    }

    [Fact]
    public void Neo_geo_pocket_licence_header_is_identified()
    {
        var b = New(0x1000);
        PutAscii(b, 0, "COPYRIGHT BY SNK CORPORATION");
        b[0x23] = 0x10;                                         // colour
        PutAscii(b, 0x24, "NGPC GAME");
        var id = RomIdentify.Identify(b);
        Assert.Equal("Neo Geo Pocket Color", id.Platform);
        Assert.Equal("NGPC GAME", id.Title);
    }

    [Fact]
    public void Lynx_magic_is_identified()
    {
        var b = New(0x100);
        PutAscii(b, 0, "LYNX");
        PutAscii(b, 0x06, "CALIFORNIA GAMES");
        PutAscii(b, 0x26, "ATARI");
        var id = RomIdentify.Identify(b);
        Assert.Equal("Atari Lynx", id.Platform);
        Assert.Equal("CALIFORNIA GAMES", id.Title);
    }

    [Fact]
    public void Atari7800_header_is_identified()
    {
        var b = New(0x100);
        b[0] = 0x02;                                            // header version
        PutAscii(b, 1, "ATARI7800");
        PutAscii(b, 0x11, "TEST 7800 CART");
        var id = RomIdentify.Identify(b);
        Assert.Equal("Atari 7800", id.Platform);
        Assert.Equal("TEST 7800 CART", id.Title);
    }

    [Fact]
    public void Wonderswan_footer_is_identified_by_its_checksum()
    {
        var b = New(0x10000);
        int f = b.Length - 16;
        b[f + 0x00] = 0x42;                                     // developer
        b[f + 0x01] = 0x01;                                     // colour flag -> WonderSwan Color
        b[f + 0x02] = 0x07;                                     // cart id
        int sum = 0;
        for (int i = 0; i < b.Length - 2; i++) sum += b[i];
        PutU16Le(b, b.Length - 2, (ushort)sum);
        var id = RomIdentify.Identify(b);
        Assert.Equal("WonderSwan Color", id.Platform);
        Assert.Equal("0x07", id.Extra["CartId"]);
    }

    [Fact]
    public void A_disc_track_is_not_mistaken_for_a_wonderswan_cart()
    {
        // A non-power-of-two buffer with an all-zero footer (checksum 0 == stored 0)
        // used to slip through the checksum-only gate and mislabel a CD track as a
        // WonderSwan ROM. Mask ROMs are power-of-two sizes, so this must be rejected.
        var track = new byte[3000];
        Assert.Null(DiscForge.Core.Rom.WonderSwanRom.TryRead(track));

        // And a power-of-two buffer with a zero checksum is likewise not a cart.
        Assert.Null(DiscForge.Core.Rom.WonderSwanRom.TryRead(new byte[0x10000]));
    }

    [Fact]
    public void Nintendo_ds_is_identified_by_its_logo_crc()
    {
        var b = New(0x400);
        PutAscii(b, 0x00, "TESTDS");
        PutAscii(b, 0x0C, "ADAE");                              // [3]='E' -> USA
        PutAscii(b, 0x10, "01");
        PutU16Le(b, 0x15C, 0xCF56);                             // fixed logo CRC
        var id = RomIdentify.Identify(b);
        Assert.Equal("Nintendo DS", id.Platform);
        Assert.Equal("ADAE", id.GameCode);
        Assert.Equal("USA", id.Region);
    }

    // ---- malformed contract -------------------------------------------------

    [Fact]
    public void Too_short_input_is_unrecognised_and_does_not_throw()
    {
        Assert.Equal(RomId.Unknown, RomIdentify.Identify(new byte[8]));
        Assert.False(RomIdentify.Identify(new byte[8]).Recognised);
    }

    [Fact]
    public void An_unrelated_buffer_is_unrecognised()
    {
        var b = New(0x8000);
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(i * 7 + 3);
        Assert.Equal("Unknown", RomIdentify.Identify(b).Platform);
    }

    // ---- hashing ------------------------------------------------------------

    [Fact]
    public void Rom_hashes_match_the_known_crc_md5_and_sha1()
    {
        var b = New(256);
        for (int i = 0; i < 256; i++) b[i] = (byte)i;
        var h = RomHashes.Compute(b, RomId.Unknown);
        Assert.Equal("29058c73", h.Crc32Hex);
        Assert.Equal("e2c865db4162bed963bfaa9ef6ac18f0", h.Md5);
        Assert.Equal("4916d6bdb7f78e6803698cab32d1586ea457dfc8", h.Sha1);
    }

    [Fact]
    public void Snes_smc_copier_header_is_excluded_from_the_hash()
    {
        var withHeader = BuildSnes(0x7FC0, 0x8000, prefix: 512);
        var body = new byte[withHeader.Length - 512];
        System.Array.Copy(withHeader, 512, body, 0, body.Length);

        var id = RomIdentify.Identify(withHeader);
        var hWith = RomHashes.Compute(withHeader, id);
        var hBody = RomHashes.Compute(body, RomId.Unknown);     // Unknown -> nothing stripped

        Assert.Equal(hBody.Crc32, hWith.Crc32);                 // header excluded => same hash as raw body
        Assert.Equal(hBody.Sha1, hWith.Sha1);
    }

    [Fact]
    public void Rom_verify_reports_a_full_match()
    {
        var b = New(256);
        for (int i = 0; i < 256; i++) b[i] = (byte)i;
        var r = RomVerify.Check(b, RomId.Unknown, "Test (USA)",
            expectedCrc: 0x29058c73, expectedMd5: "e2c865db4162bed963bfaa9ef6ac18f0");
        Assert.True(r.Verified);
    }

    // ---- FormatIdentifier hook ----------------------------------------------

    [Fact]
    public void Format_identifier_recognises_rom_magics()
    {
        var nes = New(16);
        PutAscii(nes, 0, "NES"); nes[3] = 0x1A;
        Assert.Equal("NES", FormatIdentifier.Identify(nes).Name);

        Assert.Equal("Nintendo 64", FormatIdentifier.Identify(BuildN64Z64()).Name);

        var lynx = New(0x40);
        PutAscii(lynx, 0, "LYNX");
        Assert.Equal("Atari Lynx", FormatIdentifier.Identify(lynx).Name);
    }
}
