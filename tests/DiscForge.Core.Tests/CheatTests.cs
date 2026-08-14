// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cheat;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for Game Genie decode/encode (NES, SNES, Genesis, Game Boy), raw
/// GameShark parsing and best-effort NES ROM patching.
///
/// The primary guarantee is round-trip: encode a triple, decode it back, recover
/// the triple; and decode a canonical code, re-encode it, get the same string.
/// The NES layout is additionally pinned to the published "GOSSIP" worked example
/// (address 0xD1DD, data 0x14), the one externally-verifiable fixed vector.
/// </summary>
public class CheatTests
{
    // ---- NES: fixed published vector ------------------------------------

    [Fact]
    public void Nes_Gossip_DecodesToKnownAddressAndData()
    {
        // The canonical worked example from the public NES Game Genie algorithm.
        var c = GameGenie.DecodeNes("GOSSIP");
        Assert.Equal(0xD1DDL, c.Address);
        Assert.Equal(0x14L, c.Value);
        Assert.Null(c.Compare);
    }

    [Fact]
    public void Nes_RoundTrip_SixLetter()
    {
        (long addr, long val)[] cases =
        {
            (0xD1DD, 0x14), (0x8000, 0x00), (0xFFFF, 0xFF), (0x9E5A, 0xA7), (0x8000, 0xFF),
        };
        foreach (var (addr, val) in cases)
        {
            var code = GameGenie.EncodeNes(new CheatCode { Platform = CheatPlatform.Nes, Address = addr, Value = val });
            Assert.Equal(6, code.Length);
            var back = GameGenie.DecodeNes(code);
            Assert.Equal(addr, back.Address);
            Assert.Equal(val, back.Value);
            Assert.Null(back.Compare);
        }
    }

    [Fact]
    public void Nes_RoundTrip_EightLetter_WithCompare()
    {
        (long addr, long val, long cmp)[] cases =
        {
            (0xACB3, 0x07, 0x00), (0x8000, 0x00, 0x00), (0xFFFF, 0xFF, 0xFF), (0xC1A5, 0x3C, 0x99),
        };
        foreach (var (addr, val, cmp) in cases)
        {
            var code = GameGenie.EncodeNes(new CheatCode
            { Platform = CheatPlatform.Nes, Address = addr, Value = val, Compare = cmp });
            Assert.Equal(8, code.Length);
            var back = GameGenie.DecodeNes(code);
            Assert.Equal(addr, back.Address);
            Assert.Equal(val, back.Value);
            Assert.Equal(cmp, back.Compare);
        }
    }

    [Fact]
    public void Nes_CanonicalCode_ReEncodes()
    {
        // A code produced by our encoder is canonical, so decode→encode is identity.
        var canonical = GameGenie.EncodeNes(new CheatCode { Platform = CheatPlatform.Nes, Address = 0xD1DD, Value = 0x14 });
        var re = GameGenie.EncodeNes(GameGenie.DecodeNes(canonical));
        Assert.Equal(canonical, re);
    }

    // ---- SNES -----------------------------------------------------------

    [Fact]
    public void Snes_RoundTrip_TripleToCodeToTriple()
    {
        (long addr, long val)[] cases =
        {
            (0x000000, 0x00), (0xFFFFFF, 0xFF), (0x7E0000, 0x42), (0x00ABCD, 0x99), (0xC0FFEE, 0x01),
        };
        foreach (var (addr, val) in cases)
        {
            var code = GameGenie.EncodeSnes(new CheatCode { Platform = CheatPlatform.Snes, Address = addr, Value = val });
            Assert.Equal(8, code.Length);
            var back = GameGenie.DecodeSnes(code);
            Assert.Equal(addr, back.Address);
            Assert.Equal(val, back.Value);
        }
    }

    [Fact]
    public void Snes_CanonicalCode_ReEncodes()
    {
        var canonical = GameGenie.EncodeSnes(new CheatCode { Platform = CheatPlatform.Snes, Address = 0x7E1234, Value = 0x5A });
        var re = GameGenie.EncodeSnes(GameGenie.DecodeSnes(canonical));
        Assert.Equal(canonical, re);
    }

    // ---- Genesis --------------------------------------------------------

    [Fact]
    public void Genesis_RoundTrip_TripleToCodeToTriple()
    {
        (long addr, long val)[] cases =
        {
            (0x000000, 0x0000), (0xFFFFFF, 0xFFFF), (0xFF0000, 0x1234), (0x00A5C3, 0xBEEF), (0x123456, 0x00FF),
        };
        foreach (var (addr, val) in cases)
        {
            var code = GameGenie.EncodeGenesis(new CheatCode { Platform = CheatPlatform.Genesis, Address = addr, Value = val });
            Assert.Equal(9, code.Length);          // "XXXX-YYYY"
            Assert.Equal('-', code[4]);
            var back = GameGenie.DecodeGenesis(code);
            Assert.Equal(addr, back.Address);
            Assert.Equal(val, back.Value);
        }
    }

    [Fact]
    public void Genesis_CanonicalCode_ReEncodes()
    {
        var canonical = GameGenie.EncodeGenesis(new CheatCode { Platform = CheatPlatform.Genesis, Address = 0xFF00A2, Value = 0x0100 });
        var re = GameGenie.EncodeGenesis(GameGenie.DecodeGenesis(canonical));
        Assert.Equal(canonical, re);
    }

    // ---- Game Boy -------------------------------------------------------

    [Fact]
    public void GameBoy_RoundTrip_SixDigit_NoCompare()
    {
        (long addr, long val)[] cases =
        {
            (0x0000, 0x00), (0xFFFF, 0xFF), (0xC1A5, 0x3C), (0x1234, 0x99),
        };
        foreach (var (addr, val) in cases)
        {
            var code = GameGenie.EncodeGameBoy(new CheatCode { Platform = CheatPlatform.GameBoy, Address = addr, Value = val });
            var back = GameGenie.DecodeGameBoy(code);
            Assert.Equal(addr, back.Address);
            Assert.Equal(val, back.Value);
            Assert.Null(back.Compare);
        }
    }

    [Fact]
    public void GameBoy_RoundTrip_NineDigit_WithCompare()
    {
        (long addr, long val, long cmp)[] cases =
        {
            (0x0000, 0x00, 0x00), (0xFFFF, 0xFF, 0xFF), (0xC1A5, 0x3C, 0x7E), (0x1234, 0x99, 0xBA),
        };
        foreach (var (addr, val, cmp) in cases)
        {
            var code = GameGenie.EncodeGameBoy(new CheatCode
            { Platform = CheatPlatform.GameBoy, Address = addr, Value = val, Compare = cmp });
            var back = GameGenie.DecodeGameBoy(code);
            Assert.Equal(addr, back.Address);
            Assert.Equal(val, back.Value);
            Assert.Equal(cmp, back.Compare);
        }
    }

    [Fact]
    public void GameBoy_CanonicalCode_ReEncodes()
    {
        var canonical = GameGenie.EncodeGameBoy(new CheatCode
        { Platform = CheatPlatform.GameBoy, Address = 0xC1A5, Value = 0x3C, Compare = 0x7E });
        var re = GameGenie.EncodeGameBoy(GameGenie.DecodeGameBoy(canonical));
        Assert.Equal(canonical, re);
    }

    // ---- Dispatcher -----------------------------------------------------

    [Fact]
    public void Dispatcher_RoutesByPlatform()
    {
        var nes = GameGenie.EncodeNes(new CheatCode { Platform = CheatPlatform.Nes, Address = 0x9E5A, Value = 0xA7 });
        var viaDispatch = GameGenie.Decode(CheatPlatform.Nes, nes);
        Assert.Equal(0x9E5AL, viaDispatch.Address);
        Assert.Equal(0xA7L, viaDispatch.Value);
    }

    // ---- GameShark ------------------------------------------------------

    [Fact]
    public void GameShark_16BitWrite_80()
    {
        var c = GameShark.Parse("800C4318 0063", CheatPlatform.GameSharkPs1);
        Assert.Equal(0x0C4318L, c.Address);
        Assert.Equal(0x0063L, c.Value);
        Assert.Equal(0x80, GameShark.TypeCode(0x800C4318));
        Assert.Contains("16-bit", c.Description);
    }

    [Fact]
    public void GameShark_8BitWrite_30()
    {
        var c = GameShark.Parse("300C4318 0007", CheatPlatform.GameSharkPs1);
        Assert.Equal(0x0C4318L, c.Address);
        Assert.Equal(0x0007L, c.Value);
        Assert.Contains("8-bit", c.Description);
    }

    [Fact]
    public void GameShark_EqualConditional_D0()
    {
        var c = GameShark.Parse("D00C4318 0100", CheatPlatform.GameSharkPs1);
        Assert.Equal(0x0C4318L, c.Address);
        Assert.Equal(0x0100L, c.Value);
        Assert.Equal(0x0100L, c.Compare);
        Assert.Contains("==", c.Description);
    }

    [Fact]
    public void GameShark_AcceptsSeparators()
    {
        var c = GameShark.Parse("800C4318,0063", CheatPlatform.GameSharkPs1);
        Assert.Equal(0x0C4318L, c.Address);
        Assert.Equal(0x0063L, c.Value);
    }

    // ---- Apply to ROM ---------------------------------------------------

    [Fact]
    public void ApplyNes_MatchingCompare_PatchesOnlyMatchingByte()
    {
        // iNES header + 16 KiB PRG. Put a known byte at PRG offset 0x0100.
        byte[] rom = BuildNrom(prgBanks: 1);
        int prgStart = 16;
        rom[prgStart + 0x0100] = 0x5A;
        long cpuAddr = 0x8000 + 0x0100;

        var code = new CheatCode { Platform = CheatPlatform.Nes, Address = cpuAddr, Value = 0x99, Compare = 0x5A };
        var result = CheatApply.ApplyNes(rom, code);

        Assert.Single(result.PatchedOffsets);
        Assert.Equal(prgStart + 0x0100, result.PatchedOffsets[0]);
        Assert.Equal(0x99, rom[prgStart + 0x0100]);
        Assert.False(result.CompareMismatch);
    }

    [Fact]
    public void ApplyNes_NonMatchingCompare_LeavesRomUnchanged()
    {
        byte[] rom = BuildNrom(prgBanks: 1);
        int prgStart = 16;
        rom[prgStart + 0x0100] = 0x5A;
        long cpuAddr = 0x8000 + 0x0100;

        var code = new CheatCode { Platform = CheatPlatform.Nes, Address = cpuAddr, Value = 0x99, Compare = 0x11 };
        var result = CheatApply.ApplyNes(rom, code);

        Assert.Empty(result.PatchedOffsets);
        Assert.True(result.CompareMismatch);
        Assert.Equal(0x5A, rom[prgStart + 0x0100]);       // untouched
    }

    [Fact]
    public void ApplyNes_NoCompare_PatchesUnconditionally()
    {
        byte[] rom = BuildNrom(prgBanks: 2);               // 32 KiB, 1:1 map
        int prgStart = 16;
        long cpuAddr = 0xC000 + 0x0055;                    // maps to PRG offset 0x4055
        var code = new CheatCode { Platform = CheatPlatform.Nes, Address = cpuAddr, Value = 0x42 };
        var result = CheatApply.ApplyNes(rom, code);

        Assert.Single(result.PatchedOffsets);
        Assert.Equal(0x42, rom[prgStart + 0x4055]);
    }

    // ---- Invalid input --------------------------------------------------

    [Fact]
    public void Invalid_Nes_WrongLength_Throws()
    {
        Assert.Throws<CheatFormatException>(() => GameGenie.DecodeNes("GOSSI"));   // 5 letters
    }

    [Fact]
    public void Invalid_Nes_IllegalChar_Throws()
    {
        Assert.Throws<CheatFormatException>(() => GameGenie.DecodeNes("GOSSIB")); // 'B' not in alphabet
    }

    [Fact]
    public void Invalid_Snes_WrongLength_Throws()
    {
        Assert.Throws<CheatFormatException>(() => GameGenie.DecodeSnes("DD24DF0")); // 7 chars
    }

    [Fact]
    public void Invalid_Genesis_IllegalChar_Throws()
    {
        Assert.Throws<CheatFormatException>(() => GameGenie.DecodeGenesis("IIII-OOOO")); // I,O not allowed
    }

    [Fact]
    public void Invalid_GameShark_MissingValueWord_Throws()
    {
        Assert.Throws<CheatFormatException>(() => GameShark.Parse("800C4318", CheatPlatform.GameSharkPs1));
    }

    [Fact]
    public void Invalid_GameShark_BadHex_Throws()
    {
        Assert.Throws<CheatFormatException>(() => GameShark.Parse("800C43ZZ 0063", CheatPlatform.GameSharkPs1));
    }

    // ---- helpers --------------------------------------------------------

    private static byte[] BuildNrom(int prgBanks)
    {
        int prgLen = prgBanks * 16 * 1024;
        byte[] rom = new byte[16 + prgLen];
        rom[0] = 0x4E; rom[1] = 0x45; rom[2] = 0x53; rom[3] = 0x1A;   // "NES\x1A"
        rom[4] = (byte)prgBanks;                                      // PRG size in 16 KiB units
        rom[5] = 1;                                                   // 8 KiB CHR
        return rom;
    }
}
