// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Cheat;

/// <summary>
/// Best-effort application of a decoded cheat to a ROM image. Only the simplest
/// NES case (NROM-style direct PRG mapping) is resolved: the CPU address
/// 0x8000-0xFFFF is mapped straight to a PRG-ROM offset. Mapper banking (MMC1/3,
/// etc.) is NOT modelled — a Game Genie code for a banked address may land on the
/// wrong physical byte, so this is a convenience for simple mappers only.
/// </summary>
public static class CheatApply
{
    /// <summary>The result of an apply attempt: which file offsets changed.</summary>
    public sealed record ApplyResult
    {
        /// <summary>File offsets whose byte was written.</summary>
        public required IReadOnlyList<int> PatchedOffsets { get; init; }

        /// <summary>True if the code had a compare byte that did not match (nothing patched).</summary>
        public required bool CompareMismatch { get; init; }
    }

    /// <summary>
    /// Apply an NES Game Genie code to a ROM buffer in place (NROM mapping).
    /// Honours a 16-byte iNES header (and a 512-byte trainer) when present, mirrors a
    /// 16 KiB PRG across both halves of the 0x8000-0xFFFF window, and — when the code
    /// carries a compare byte — only writes offsets whose current value equals it.
    /// </summary>
    public static ApplyResult ApplyNes(byte[] rom, CheatCode code)
    {
        if (rom is null) throw new ArgumentNullException(nameof(rom));
        if (code.Platform != CheatPlatform.Nes)
            throw new CheatFormatException($"ApplyNes needs an NES code, got {code.Platform}.");
        if (code.Address is < 0x8000 or > 0xFFFF)
            throw new CheatFormatException(
                $"NES address must be 0x8000-0xFFFF, got 0x{code.Address:X}.");

        // Locate PRG-ROM within the file.
        int prgStart = 0;
        long prgSize = rom.Length;
        bool ines = rom.Length >= 16 && rom[0] == 0x4E && rom[1] == 0x45 && rom[2] == 0x53 && rom[3] == 0x1A;
        if (ines)
        {
            prgStart = 16;
            if ((rom[6] & 0x04) != 0) prgStart += 512;      // trainer
            prgSize = rom[4] * 16L * 1024;
            if (prgSize == 0) prgSize = rom.Length - prgStart;
        }

        int cpuOffset = (int)(code.Address - 0x8000);       // 0..0x7FFF

        // Mirror: a PRG smaller than the 32 KiB window repeats. For power-of-two sizes
        // (16 KiB NROM) mask; a 32 KiB PRG maps 1:1.
        var offsets = new List<int>();
        if (prgSize > 0 && (prgSize & (prgSize - 1)) == 0 && prgSize <= 0x8000)
        {
            int physical = cpuOffset & (int)(prgSize - 1);
            AddOffset(offsets, prgStart + physical, rom.Length);
        }
        else
        {
            // Non-power-of-two / banked: best effort, direct map if it lands in PRG.
            if (cpuOffset < prgSize) AddOffset(offsets, prgStart + cpuOffset, rom.Length);
        }

        byte value = (byte)(code.Value & 0xFF);
        var patched = new List<int>();
        bool anyCandidate = offsets.Count > 0;
        bool mismatch = false;

        foreach (int off in offsets)
        {
            if (code.Compare is { } cmp)
            {
                if (rom[off] == (byte)(cmp & 0xFF))
                {
                    rom[off] = value;
                    patched.Add(off);
                }
                else
                {
                    mismatch = true;
                }
            }
            else
            {
                rom[off] = value;
                patched.Add(off);
            }
        }

        return new ApplyResult
        {
            PatchedOffsets = patched,
            CompareMismatch = anyCandidate && patched.Count == 0 && mismatch,
        };
    }

    private static void AddOffset(List<int> list, int off, int romLen)
    {
        if (off >= 0 && off < romLen && !list.Contains(off)) list.Add(off);
    }
}
