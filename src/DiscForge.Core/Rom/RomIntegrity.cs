// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Rom;

/// <summary>Outcome of one integrity check.</summary>
public enum RomCheckStatus
{
    /// <summary>The value on the cartridge matches what its data actually computes to.</summary>
    Pass,
    /// <summary>Mismatch — evidence of a bad dump or corruption.</summary>
    Fail,
    /// <summary>Informational — a mismatch that is expected on homebrew (e.g. a missing boot logo).</summary>
    Info,
}

/// <summary>A single named integrity check and its result.</summary>
public sealed record RomCheck(string Name, RomCheckStatus Status, string Detail);

/// <summary>The result of verifying a cartridge ROM's internal integrity fields.</summary>
public sealed record RomIntegrityResult
{
    public required string Platform { get; init; }
    public required IReadOnlyList<RomCheck> Checks { get; init; }

    /// <summary>True when no check failed (Info-level mismatches do not fail a ROM).</summary>
    public bool Ok => Checks.All(c => c.Status != RomCheckStatus.Fail);

    public string Summary()
    {
        if (Checks.Count == 0)
            return $"{Platform}: no internal checksum/logo fields to verify.";
        int fails = Checks.Count(c => c.Status == RomCheckStatus.Fail);
        return fails == 0
            ? $"{Platform}: all {Checks.Count} integrity check(s) pass."
            : $"{Platform}: {fails} of {Checks.Count} integrity check(s) FAILED — likely a bad dump.";
    }
}

/// <summary>
/// rom-integrity — verify a cartridge ROM's own internal integrity fields by <b>recomputing</b> them from the
/// ROM body and comparing, rather than merely reading what the header claims. That distinction matters: a bad
/// dump often keeps plausible-looking header values while its content is wrong, and only a recomputation
/// catches it. Game Boy: the 8-bit header checksum and the 16-bit global checksum (sum of every byte bar the
/// two checksum bytes). Sega Genesis/Mega Drive: the 16-bit content checksum over 0x200..end. Game Boy Advance:
/// the header checksum and the boot-logo match. Pure integrity verification — it reads and recomputes only,
/// changes nothing, and defeats no protection.
/// </summary>
public static class RomIntegrity
{
    public static RomIntegrityResult Verify(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);

        if (IsGenesis(rom)) return VerifyGenesis(rom);
        if (rom.Length >= 0xC0 && GbaRom.LogoMatches(rom)) return VerifyGba(rom);
        if (rom.Length >= 0x150 && GameBoyRom.LogoMatches(rom)) return VerifyGameBoy(rom);

        // GBA/GB with a damaged logo can still be checked by header-field shape.
        if (rom.Length >= 0x150 && LooksLikeGameBoy(rom)) return VerifyGameBoy(rom);
        if (rom.Length >= 0xC0 && rom[0xB2] == 0x96) return VerifyGba(rom);   // fixed byte 0x96 at 0xB2

        return new RomIntegrityResult
        {
            Platform = "Unknown",
            Checks = new List<RomCheck>
            {
                new("format", RomCheckStatus.Info,
                    "not a recognised checksummed cartridge (Game Boy, GBA or Sega Genesis) — use rom-info to identify it"),
            },
        };
    }

    // ---- Game Boy / Game Boy Color -----------------------------------------

    private static RomIntegrityResult VerifyGameBoy(byte[] rom)
    {
        var checks = new List<RomCheck>();

        byte storedHeader = rom[0x14D];
        byte computedHeader = GameBoyRom.ComputeHeaderChecksum(rom);
        checks.Add(new RomCheck("header checksum",
            storedHeader == computedHeader ? RomCheckStatus.Pass : RomCheckStatus.Fail,
            $"stored 0x{storedHeader:X2}, computed 0x{computedHeader:X2}"));

        ushort storedGlobal = (ushort)((rom[0x14E] << 8) | rom[0x14F]);
        ushort computedGlobal = ComputeGameBoyGlobalChecksum(rom);
        checks.Add(new RomCheck("global checksum",
            storedGlobal == computedGlobal ? RomCheckStatus.Pass : RomCheckStatus.Fail,
            $"stored 0x{storedGlobal:X4}, computed 0x{computedGlobal:X4}"));

        checks.Add(new RomCheck("boot logo",
            GameBoyRom.LogoMatches(rom) ? RomCheckStatus.Pass : RomCheckStatus.Info,
            GameBoyRom.LogoMatches(rom) ? "matches the Nintendo logo" : "does not match — homebrew or a bad dump"));

        bool gbc = rom[0x143] is 0x80 or 0xC0;
        return new RomIntegrityResult { Platform = gbc ? "Game Boy Color" : "Game Boy", Checks = checks };
    }

    /// <summary>Sum of every byte except the two global-checksum bytes at 0x14E/0x14F, masked to 16 bits.</summary>
    public static ushort ComputeGameBoyGlobalChecksum(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        int sum = 0;
        for (int i = 0; i < rom.Length; i++)
        {
            if (i == 0x14E || i == 0x14F) continue;
            sum += rom[i];
        }
        return (ushort)(sum & 0xFFFF);
    }

    private static bool LooksLikeGameBoy(byte[] rom)
    {
        // The header checksum is a strong shape signal even when the logo is damaged.
        return GameBoyRom.ComputeHeaderChecksum(rom) == rom[0x14D];
    }

    // ---- Game Boy Advance ---------------------------------------------------

    private static RomIntegrityResult VerifyGba(byte[] rom)
    {
        var checks = new List<RomCheck>();

        byte stored = rom[0xBD];
        byte computed = GbaRom.ComputeHeaderChecksum(rom);
        checks.Add(new RomCheck("header checksum",
            stored == computed ? RomCheckStatus.Pass : RomCheckStatus.Fail,
            $"stored 0x{stored:X2}, computed 0x{computed:X2}"));

        checks.Add(new RomCheck("boot logo",
            GbaRom.LogoMatches(rom) ? RomCheckStatus.Pass : RomCheckStatus.Info,
            GbaRom.LogoMatches(rom) ? "matches the Nintendo logo" : "does not match — homebrew or a bad dump"));

        return new RomIntegrityResult { Platform = "Game Boy Advance", Checks = checks };
    }

    // ---- Sega Genesis / Mega Drive -----------------------------------------

    private static bool IsGenesis(byte[] rom) =>
        rom.Length >= 0x200 &&
        rom[0x100] == (byte)'S' && rom[0x101] == (byte)'E' &&
        rom[0x102] == (byte)'G' && rom[0x103] == (byte)'A';

    private static RomIntegrityResult VerifyGenesis(byte[] rom)
    {
        ushort stored = (ushort)((rom[0x18E] << 8) | rom[0x18F]);
        ushort computed = ComputeGenesisChecksum(rom);
        var checks = new List<RomCheck>
        {
            new("content checksum",
                stored == computed ? RomCheckStatus.Pass : RomCheckStatus.Fail,
                $"stored 0x{stored:X4}, computed 0x{computed:X4} (16-bit sum over 0x200..end)"),
        };
        return new RomIntegrityResult { Platform = "Sega Genesis / Mega Drive", Checks = checks };
    }

    /// <summary>The Sega Mega Drive / Genesis header checksum: the sum of the ROM as 16-bit BIG-ENDIAN
    /// words from 0x200 to the end, masked to 16 bits (this is what the value stored at 0x18E is computed
    /// over). A trailing odd byte, if any, contributes its high-byte position.</summary>
    public static ushort ComputeGenesisChecksum(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        int sum = 0;
        int i = 0x200;
        for (; i + 1 < rom.Length; i += 2) sum += (rom[i] << 8) | rom[i + 1];
        if (i < rom.Length) sum += rom[i] << 8;   // dangling odd byte (real ROMs are word-aligned)
        return (ushort)(sum & 0xFFFF);
    }
}
