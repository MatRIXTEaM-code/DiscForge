// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;

namespace DiscForge.Core.Raw;

/// <summary>
/// Builds full 2352-byte raw sectors from whatever the source image stored —
/// the inverse of "cooking". A RAW burn needs every sector in surface form:
///   stored 2352 → already raw, passed through;
///   stored 2048 (Mode 1) → sync + header + data + EDC + RSPC synthesised;
///   stored 2336 (Mode 2) → sync + header prepended (XA subheader and any
///                          Form 1 EDC/ECC are part of the stored 2336).
/// Scrambling is a separate, later step (<see cref="CdScrambler"/>) because
/// verification and inspection want the unscrambled form.
/// </summary>
public static class RawSectorBuilder
{
    /// <summary>Write the 12-byte sync pattern (00, FF×10, 00).</summary>
    public static void WriteSync(Span<byte> sector)
    {
        sector[0] = 0x00;
        for (int i = 1; i <= 10; i++) sector[i] = 0xFF;
        sector[11] = 0x00;
    }

    /// <summary>Header: absolute MSF in BCD + mode byte at bytes 12..15.</summary>
    public static void WriteHeader(Span<byte> sector, Msf absolute, byte mode)
    {
        sector[12] = Bcd.From(absolute.Minutes);
        sector[13] = Bcd.From(absolute.Seconds);
        sector[14] = Bcd.From(absolute.Frames);
        sector[15] = mode;
    }

    /// <summary>Synthesise a raw Mode 1 sector from 2048 bytes of user data.</summary>
    public static void BuildMode1(ReadOnlySpan<byte> user2048, Msf absolute, Span<byte> sector2352)
    {
        if (user2048.Length != 2048) throw new ArgumentException("Mode 1 user data is 2048 bytes.");
        if (sector2352.Length != 2352) throw new ArgumentException("A raw sector is 2352 bytes.");

        WriteSync(sector2352);
        WriteHeader(sector2352, absolute, mode: 1);
        user2048.CopyTo(sector2352.Slice(16, 2048));
        EdcEcc.FillMode1(sector2352);
    }

    /// <summary>Raw Mode 2 sector from a stored 2336-byte body (subheader+data).</summary>
    public static void BuildMode2(ReadOnlySpan<byte> body2336, Msf absolute, Span<byte> sector2352)
    {
        if (body2336.Length != 2336) throw new ArgumentException("Mode 2 stored body is 2336 bytes.");
        if (sector2352.Length != 2352) throw new ArgumentException("A raw sector is 2352 bytes.");

        WriteSync(sector2352);
        WriteHeader(sector2352, absolute, mode: 2);
        body2336.CopyTo(sector2352[16..]);
    }
}

/// <summary>Binary-coded decimal, as every MSF field on a CD is stored.</summary>
public static class Bcd
{
    public static byte From(int value)
    {
        if (value is < 0 or > 99) throw new ArgumentOutOfRangeException(nameof(value));
        return (byte)(((value / 10) << 4) | (value % 10));
    }

    public static int To(byte bcd) => ((bcd >> 4) * 10) + (bcd & 0x0F);
}
