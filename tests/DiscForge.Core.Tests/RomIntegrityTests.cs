// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Rom;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for cartridge internal-checksum verification. A Game Boy ROM whose header and global checksums are
/// set correctly passes; flipping a body byte fails the global checksum. A Sega Genesis ROM whose content
/// checksum is set correctly passes; a body change fails it. The recomputations are checked against values
/// computed independently in the test.
/// </summary>
public class RomIntegrityTests
{
    private static byte[] BuildGameBoy()
    {
        var rom = new byte[0x8000];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)(i * 7 + 3);
        for (int i = 0x134; i < 0x144; i++) rom[i] = 0;
        "TESTROM"u8.CopyTo(rom.AsSpan(0x134));
        rom[0x143] = 0x00;

        // Header checksum over 0x134..0x14C: x = x - rom[i] - 1.
        byte hc = 0;
        for (int i = 0x134; i <= 0x14C; i++) hc = (byte)(hc - rom[i] - 1);
        rom[0x14D] = hc;

        // Global checksum: sum of all bytes bar 0x14E/0x14F.
        int sum = 0;
        for (int i = 0; i < rom.Length; i++) if (i != 0x14E && i != 0x14F) sum += rom[i];
        rom[0x14E] = (byte)((sum >> 8) & 0xFF);
        rom[0x14F] = (byte)(sum & 0xFF);
        return rom;
    }

    private static byte[] BuildGenesis()
    {
        var rom = new byte[0x40000];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)(i * 5 + 1);
        "SEGA"u8.CopyTo(rom.AsSpan(0x100));
        // Real Genesis checksum: sum of 16-bit BIG-ENDIAN words from 0x200 to end (not a byte sum).
        int sum = 0;
        for (int i = 0x200; i + 1 < rom.Length; i += 2) sum += (rom[i] << 8) | rom[i + 1];
        rom[0x18E] = (byte)((sum >> 8) & 0xFF);
        rom[0x18F] = (byte)(sum & 0xFF);
        return rom;
    }

    [Fact]
    public void A_consistent_game_boy_rom_passes()
    {
        var r = RomIntegrity.Verify(BuildGameBoy());
        Assert.Equal("Game Boy", r.Platform);
        Assert.True(r.Ok);
        Assert.All(r.Checks.Where(c => c.Name.Contains("checksum")),
                   c => Assert.Equal(RomCheckStatus.Pass, c.Status));
    }

    [Fact]
    public void A_flipped_body_byte_fails_the_game_boy_global_checksum()
    {
        var rom = BuildGameBoy();
        rom[0x2000] ^= 0xFF;                 // stored checksums untouched
        var r = RomIntegrity.Verify(rom);
        Assert.False(r.Ok);
        Assert.Contains(r.Checks, c => c.Name == "global checksum" && c.Status == RomCheckStatus.Fail);
        // The header checksum (over the title area) is unaffected.
        Assert.Contains(r.Checks, c => c.Name == "header checksum" && c.Status == RomCheckStatus.Pass);
    }

    [Fact]
    public void A_consistent_genesis_rom_passes()
    {
        var r = RomIntegrity.Verify(BuildGenesis());
        Assert.Equal("Sega Genesis / Mega Drive", r.Platform);
        Assert.True(r.Ok);
    }

    [Fact]
    public void A_flipped_body_byte_fails_the_genesis_content_checksum()
    {
        var rom = BuildGenesis();
        rom[0x3000] ^= 0xFF;
        var r = RomIntegrity.Verify(rom);
        Assert.False(r.Ok);
        Assert.Contains(r.Checks, c => c.Name == "content checksum" && c.Status == RomCheckStatus.Fail);
    }

    [Fact]
    public void An_unrecognised_blob_is_reported_not_crashed()
    {
        var r = RomIntegrity.Verify(new byte[0x400]);
        Assert.Equal("Unknown", r.Platform);
        Assert.True(r.Ok); // nothing failed; it just isn't a checksummed cartridge
    }
}
