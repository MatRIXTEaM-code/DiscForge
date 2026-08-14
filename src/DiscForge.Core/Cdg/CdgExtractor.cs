// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;

namespace DiscForge.Core.Cdg;

/// <summary>
/// Recover a <c>.cdg</c> packet stream from a raw sub-channel sidecar. The
/// sidecar is 96 bytes per sector (see <see cref="RawSubchannel.FrameSize"/>),
/// with the eight sub-channels interleaved as the eight bits of each byte
/// (P = 0x80 … W = 0x01). CD+G lives in the R–W channels — the low 6 bits of
/// every byte — so masking each byte to <c>b &amp; 0x3F</c> turns one 96-byte
/// frame into 96 R–W symbols, i.e. four 24-byte CD+G packets. Concatenating
/// across frames yields the <c>.cdg</c> stream.
/// </summary>
public static class CdgExtractor
{
    /// <summary>Extract the CD+G packet stream from a raw sub-channel sidecar
    /// stream. A trailing partial frame (fewer than 96 bytes) is ignored.</summary>
    public static byte[] Extract(Stream subSidecar)
    {
        ArgumentNullException.ThrowIfNull(subSidecar);
        using var ms = new MemoryStream();
        subSidecar.CopyTo(ms);
        return Extract(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    /// <summary>Extract the CD+G packet stream from a raw sub-channel sidecar
    /// buffer. A trailing partial frame is ignored.</summary>
    public static byte[] Extract(byte[] subSidecar)
    {
        ArgumentNullException.ThrowIfNull(subSidecar);
        return Extract((ReadOnlySpan<byte>)subSidecar);
    }

    /// <summary>Extract the CD+G packet stream from a raw sub-channel sidecar
    /// span. A trailing partial frame is ignored.</summary>
    public static byte[] Extract(ReadOnlySpan<byte> subSidecar)
    {
        int frames = subSidecar.Length / RawSubchannel.FrameSize;
        var cdg = new byte[frames * RawSubchannel.FrameSize];
        for (int i = 0; i < cdg.Length; i++)
            cdg[i] = (byte)(subSidecar[i] & 0x3F);
        return cdg;
    }
}
