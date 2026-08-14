// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Util;

/// <summary>
/// CRC-16/CCITT (polynomial 0x1021, init 0x0000, no reflection) — the checksum
/// the CD world runs on. The Q sub-channel frame and every CD-TEXT pack carry
/// this CRC with all bits INVERTED (ones' complement), per Red Book convention;
/// <see cref="ComputeInverted"/> gives that form directly.
///
/// The non-inverted form is the well-known CRC-16/XMODEM, which is what makes
/// this testable against published vectors ("123456789" → 0x31C3).
/// </summary>
public static class Crc16
{
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort c = (ushort)(i << 8);
            for (int b = 0; b < 8; b++)
                c = (ushort)((c & 0x8000) != 0 ? (c << 1) ^ 0x1021 : c << 1);
            t[i] = c;
        }
        return t;
    }

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
            crc = (ushort)((crc << 8) ^ Table[((crc >> 8) ^ b) & 0xFF]);
        return crc;
    }

    /// <summary>The CRC as stored on disc: computed, then all bits inverted.</summary>
    public static ushort ComputeInverted(ReadOnlySpan<byte> data)
        => (ushort)~Compute(data);
}
