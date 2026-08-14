// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Cdg;

/// <summary>
/// A parsed <c>.cdg</c> file: nothing more than the packet stream chunked into
/// 24-byte records. A trailing partial chunk (fewer than 24 bytes) is ignored.
/// </summary>
public sealed class CdgFile
{
    /// <summary>The 24-byte packets, in order.</summary>
    public IReadOnlyList<byte[]> Packets { get; }

    private CdgFile(List<byte[]> packets) => Packets = packets;

    /// <summary>Parse a <c>.cdg</c> byte buffer into its packets.</summary>
    public static CdgFile Parse(byte[] cdg)
    {
        ArgumentNullException.ThrowIfNull(cdg);
        return Parse((ReadOnlySpan<byte>)cdg);
    }

    /// <summary>Parse a <c>.cdg</c> buffer into its packets.</summary>
    public static CdgFile Parse(ReadOnlySpan<byte> cdg)
    {
        int count = cdg.Length / CdgDecoder.PacketSize;
        var packets = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
            packets.Add(cdg.Slice(i * CdgDecoder.PacketSize, CdgDecoder.PacketSize).ToArray());
        return new CdgFile(packets);
    }

    /// <summary>Parse a <c>.cdg</c> stream into its packets.</summary>
    public static CdgFile Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }
}
