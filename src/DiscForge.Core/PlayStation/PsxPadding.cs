// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.PlayStation;

/// <summary>
/// Padding helpers — the "PSX Padding Tool" job. Two cases matter on the
/// PlayStation: padding a file to a sector (2048-byte) boundary, and padding a
/// PS-EXE so its TEXT image is a whole multiple of 0x800 (which the loader
/// requires) with the header's <c>t_size</c> kept consistent. Deterministic and
/// pure — it only appends fill bytes and, for a PS-EXE, rewrites one header field.
/// </summary>
public static class PsxPadding
{
    public const int SectorSize = 2048;
    public const int PsExeAlign = 0x800;

    /// <summary>Pad to the next multiple of <paramref name="multiple"/> with
    /// <paramref name="fill"/> (default 0). Already-aligned input is returned
    /// unchanged (a fresh copy).</summary>
    public static byte[] PadToMultiple(byte[] data, int multiple = SectorSize, byte fill = 0x00)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (multiple <= 0) throw new ArgumentOutOfRangeException(nameof(multiple));

        long target = ((data.LongLength + multiple - 1) / multiple) * multiple;
        var padded = new byte[target];
        Array.Copy(data, padded, data.Length);
        if (fill != 0)
            for (long i = data.LongLength; i < target; i++) padded[i] = fill;
        return padded;
    }

    /// <summary>Pad a PS-EXE's payload to a whole multiple of 0x800 and rewrite the
    /// header's <c>t_size</c> to match, so the padded EXE stays loadable. The whole
    /// file (2 KB header + payload) is padded to the same boundary.</summary>
    public static byte[] PadPsExe(byte[] exe, byte fill = 0x00)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!PsExe.IsPsExe(exe) || exe.Length < PsExe.HeaderSize)
            throw new PsExeFormatException("Not a PS-EXE — cannot pad its payload.");

        long payload = exe.LongLength - PsExe.HeaderSize;
        long paddedPayload = ((payload + PsExeAlign - 1) / PsExeAlign) * PsExeAlign;

        var result = new byte[PsExe.HeaderSize + paddedPayload];
        Array.Copy(exe, result, exe.Length);
        if (fill != 0)
            for (long i = exe.LongLength; i < result.LongLength; i++) result[i] = fill;

        // Keep t_size (0x1C) consistent with the padded payload length.
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x1C, 4), (uint)paddedPayload);
        return result;
    }
}
