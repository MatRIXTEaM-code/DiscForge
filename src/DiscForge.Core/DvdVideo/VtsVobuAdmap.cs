// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.DvdVideo;

/// <summary>
/// Reads the VTS_VOBU_ADMAP of a muxed DVD-Video title set — the flat table of VOBU start sectors a
/// layer-break recommender needs. The pointer lives in the VTSI_MAT at 0xE4 (a sector number,
/// big-endian, relative to the start of the IFO); the table itself is a 4-byte end-address followed
/// by 4-byte VOBU start sectors, each relative to the start of VTSTT_VOBS (the title VOB). This is
/// the read counterpart to <c>IfoWriter</c>, which emits the same table with the per-VOBU addresses
/// left zero (they only exist once real video is muxed) — so this only yields useful boundaries on a
/// real authored DVD, which is exactly where a layer break has to be placed.
/// </summary>
public static class VtsVobuAdmap
{
    private const int Sector = 2048;
    private const int VtsVobuAdmapPointer = 0xE4;   // VTSI_MAT offset of the VTS_VOBU_ADMAP sector pointer

    /// <summary>
    /// The title VOBU start sectors, each relative to VTSTT_VOBS. Empty when the IFO carries no
    /// populated map (e.g. a freshly-authored IFO before muxing, or a non-VTS file).
    /// </summary>
    public static IReadOnlyList<uint> ReadTitleVobuStarts(byte[] vtsIfo)
    {
        ArgumentNullException.ThrowIfNull(vtsIfo);
        if (vtsIfo.Length < VtsVobuAdmapPointer + 4) return Array.Empty<uint>();

        uint admapSector = U32(vtsIfo, VtsVobuAdmapPointer);
        if (admapSector == 0) return Array.Empty<uint>();

        long baseOff = (long)admapSector * Sector;
        if (baseOff + 4 > vtsIfo.Length) return Array.Empty<uint>();

        uint endAddress = U32(vtsIfo, (int)baseOff);        // last byte of the table, relative to its start
        long entryBytes = (long)endAddress + 1 - 4;         // bytes of 4-byte VOBU entries after the header
        if (entryBytes < 4) return Array.Empty<uint>();

        long available = (vtsIfo.Length - (baseOff + 4)) / 4;
        int count = (int)Math.Min(entryBytes / 4, available);
        var list = new List<uint>(count);
        for (int i = 0; i < count; i++)
            list.Add(U32(vtsIfo, (int)baseOff + 4 + i * 4));
        return list;
    }

    private static uint U32(byte[] b, int off) =>
        (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);
}
