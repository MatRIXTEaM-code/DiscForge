// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Saves;

/// <summary>
/// Generic, reversible transforms for cartridge battery saves, to fix the two things
/// that stop a save moving between emulators or onto hardware: byte order and size.
///
/// N64 SRAM / FlashRAM / EEPROM are stored in different word orders by different
/// emulators — a 16- or 32-bit word swap moves between them. GB / SNES / Genesis SRAM
/// files differ only by trailing padding — normalising the size (pad or trim) makes them
/// interchangeable. These are pure byte operations, each the exact inverse of another;
/// which one a given emulator wants is the caller's choice (DiscForge doesn't guess a
/// specific emulator pairing it can't verify), and the transforms are round-trip safe.
/// </summary>
public static class SaveConvert
{
    /// <summary>Canonical N64 save sizes, for <see cref="Resize"/> normalisation.</summary>
    public const int EepromSmall = 512;      // 4 kbit
    public const int EepromLarge = 2048;     // 16 kbit
    public const int Sram = 32 * 1024;       // 256 kbit
    public const int FlashRam = 128 * 1024;  // 1 Mbit
    public const int ControllerPak = 32 * 1024;

    /// <summary>
    /// Swap the byte order within each <paramref name="width"/>-byte word (2 or 4). This is
    /// its own inverse for a length that is a whole number of words; a trailing partial word
    /// is left untouched.
    /// </summary>
    public static byte[] WordSwap(byte[] data, int width)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (width is not (2 or 4)) throw new ArgumentOutOfRangeException(nameof(width), "Width must be 2 or 4.");

        var outp = (byte[])data.Clone();
        int whole = outp.Length - (outp.Length % width);
        for (int i = 0; i < whole; i += width)
            Array.Reverse(outp, i, width);
        return outp;
    }

    /// <summary>
    /// Return the save at exactly <paramref name="size"/> bytes: padded with
    /// <paramref name="fill"/> if shorter, truncated if longer.
    /// </summary>
    public static byte[] Resize(byte[] data, int size, byte fill = 0x00)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        var outp = new byte[size];
        if (fill != 0) Array.Fill(outp, fill);
        Array.Copy(data, outp, Math.Min(size, data.Length));
        return outp;
    }

    /// <summary>Remove a run of trailing <paramref name="fill"/> bytes (e.g. 0x00 or 0xFF padding).</summary>
    public static byte[] TrimTrailing(byte[] data, byte fill = 0x00)
    {
        ArgumentNullException.ThrowIfNull(data);
        int end = data.Length;
        while (end > 0 && data[end - 1] == fill) end--;
        return data.AsSpan(0, end).ToArray();
    }
}
