// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Util;

/// <summary>
/// Standard IEEE 802.3 CRC-32 (reflected, polynomial 0xEDB88320) — identical to
/// zlib's crc32, so DiscForge checksums are directly comparable with common
/// tooling. Streaming: seed with 0, feed chunks, read <see cref="Value"/>.
/// </summary>
public sealed class Crc32
{
    private static readonly uint[] Table = BuildTable();
    private uint _crc = 0xFFFFFFFF;

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        uint crc = _crc;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        _crc = crc;
    }

    /// <summary>Current CRC-32 value.</summary>
    public uint Value => _crc ^ 0xFFFFFFFF;

    /// <summary>One-shot convenience.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var c = new Crc32();
        c.Update(data);
        return c.Value;
    }
}
