// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.DvdVideo;

/// <summary>The VTS-relative sector pointers from a Video Title Set IFO header.</summary>
public sealed record VtsiPointers(uint VtsLastSector, uint VtsiLastSector, uint VtsmVobs, uint VtsttVobs);

/// <summary>The VMG-relative sector pointers from the Video Manager IFO header.</summary>
public sealed record VmgiPointers(uint VmgLastSector, uint VmgiLastSector, uint VmgmVobs);

/// <summary>
/// Reads the sector pointers a DVD-Video IFO carries and checks them against the actual
/// file layout — the consistency ImgBurn's "Fix VTS Sectors" guards. A faithfully
/// assembled title set (files placed unchanged and contiguous, IFO leading, BUP trailing)
/// keeps these pointers valid; a mismatch means the folder was edited without updating the
/// IFO, and the disc would mis-navigate. All big-endian, per the DVD-Video spec.
///
/// VTSI header (VTS_nn_0.IFO):
///   0x00 "DVDVIDEO-VTS"   0x0C VTS_LAST_SECTOR    0x1C VTSI_LAST_SECTOR
///   0xC0 VTSM_VOBS (menu VOB start, VTS-relative; 0 = none)   0xC4 VTSTT_VOBS (title VOB start)
/// VMGI header (VIDEO_TS.IFO):
///   0x00 "DVDVIDEO-VMG"   0x0C VMG_LAST_SECTOR    0x1C VMGI_LAST_SECTOR   0xC0 VMGM_VOBS
/// </summary>
public static class DvdVideoIfo
{
    public const int Sector = 2048;

    /// <summary>Sectors a byte length occupies (2048-byte sectors, rounded up).</summary>
    public static int Sectors(long bytes) => (int)((bytes + Sector - 1) / Sector);

    public static VtsiPointers? ParseVtsi(ReadOnlySpan<byte> ifo)
    {
        if (ifo.Length < 0xC8 || !HasMagic(ifo, "DVDVIDEO-VTS")) return null;
        return new VtsiPointers(
            BinaryPrimitives.ReadUInt32BigEndian(ifo.Slice(0x0C, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(ifo.Slice(0x1C, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(ifo.Slice(0xC0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(ifo.Slice(0xC4, 4)));
    }

    public static VmgiPointers? ParseVmgi(ReadOnlySpan<byte> vmg)
    {
        if (vmg.Length < 0xC4 || !HasMagic(vmg, "DVDVIDEO-VMG")) return null;
        return new VmgiPointers(
            BinaryPrimitives.ReadUInt32BigEndian(vmg.Slice(0x0C, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(vmg.Slice(0x1C, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(vmg.Slice(0xC0, 4)));
    }

    /// <summary>
    /// Check a title set's IFO pointers against its files' sector counts. Returns the list
    /// of inconsistencies (empty = the IFO agrees with the layout).
    /// </summary>
    public static IReadOnlyList<string> VerifyVts(int titleSet, VtsiPointers p,
        int ifoSectors, int menuVobSectors, int titleVobSectors, int bupSectors)
    {
        var issues = new List<string>();
        string t = $"VTS_{titleSet:D2}";

        if (p.VtsiLastSector + 1 != (uint)ifoSectors)
            issues.Add($"{t}: VTSI_LAST_SECTOR ({p.VtsiLastSector}) implies an IFO of " +
                       $"{p.VtsiLastSector + 1} sectors, but VTS_{titleSet:D2}_0.IFO is {ifoSectors}.");
        if (bupSectors != ifoSectors)
            issues.Add($"{t}: the BUP is {bupSectors} sectors but the IFO is {ifoSectors} — the backup must be an exact copy.");

        uint expectedMenu = menuVobSectors > 0 ? (uint)ifoSectors : 0;
        if (p.VtsmVobs != expectedMenu)
            issues.Add($"{t}: VTSM_VOBS ({p.VtsmVobs}) should be {expectedMenu} (menu VOB right after the IFO).");

        uint expectedTitle = (uint)(ifoSectors + menuVobSectors);
        if (p.VtsttVobs != expectedTitle)
            issues.Add($"{t}: VTSTT_VOBS ({p.VtsttVobs}) should be {expectedTitle} (title VOB after IFO + menu).");

        uint expectedLast = (uint)(ifoSectors + menuVobSectors + titleVobSectors + bupSectors) - 1;
        if (p.VtsLastSector != expectedLast)
            issues.Add($"{t}: VTS_LAST_SECTOR ({p.VtsLastSector}) should be {expectedLast} " +
                       "(IFO + menu + title + BUP − 1).");

        return issues;
    }

    /// <summary>Check the Video Manager IFO pointers against its files' sector counts.</summary>
    public static IReadOnlyList<string> VerifyVmg(VmgiPointers p,
        int ifoSectors, int menuVobSectors, int bupSectors)
    {
        var issues = new List<string>();
        if (p.VmgiLastSector + 1 != (uint)ifoSectors)
            issues.Add($"VMG: VMGI_LAST_SECTOR ({p.VmgiLastSector}) implies {p.VmgiLastSector + 1} " +
                       $"sectors, but VIDEO_TS.IFO is {ifoSectors}.");
        if (bupSectors != ifoSectors)
            issues.Add($"VMG: VIDEO_TS.BUP is {bupSectors} sectors but the IFO is {ifoSectors}.");
        uint expectedMenu = menuVobSectors > 0 ? (uint)ifoSectors : 0;
        if (p.VmgmVobs != expectedMenu)
            issues.Add($"VMG: VMGM_VOBS ({p.VmgmVobs}) should be {expectedMenu}.");
        uint expectedLast = (uint)(ifoSectors + menuVobSectors + bupSectors) - 1;
        if (p.VmgLastSector != expectedLast)
            issues.Add($"VMG: VMG_LAST_SECTOR ({p.VmgLastSector}) should be {expectedLast}.");
        return issues;
    }

    // ── Write half of "Fix VTS Sectors" ──────────────────────────────────────────────────
    // The verify half above proves what a title set's file-location pointers *should* be for a
    // contiguous layout (IFO, optional menu VOB, title VOBs, BUP placed back-to-back — exactly
    // how dvd-video-build lays them down). The write half sets those pointers to those values.
    // Only the four whole-file / VOB-location pointers are touched — VTS_LAST_SECTOR,
    // VTSI_LAST_SECTOR, VTSM_VOBS, VTSTT_VOBS (and the VMG trio). The many internal pointers an
    // IFO also carries (VTS_PGCI, VTS_PTT_SRPT, the C_ADT / VOBU_ADMAP tables, …) are relative to
    // the IFO's own start and do not move when a title set's VOBs are resized, so they are left
    // byte-for-byte untouched — the same narrow scope as ImgBurn's "Fix VTS Sectors".

    /// <summary>The pointer values a contiguous title set of these sector counts must carry.</summary>
    public static VtsiPointers ComputeVts(int ifoSectors, int menuVobSectors, int titleVobSectors, int bupSectors)
        => new(
            VtsLastSector: (uint)(ifoSectors + menuVobSectors + titleVobSectors + bupSectors) - 1,
            VtsiLastSector: (uint)ifoSectors - 1,
            VtsmVobs: menuVobSectors > 0 ? (uint)ifoSectors : 0,
            VtsttVobs: (uint)(ifoSectors + menuVobSectors));

    /// <summary>The pointer values a contiguous Video Manager of these sector counts must carry.</summary>
    public static VmgiPointers ComputeVmg(int ifoSectors, int menuVobSectors, int bupSectors)
        => new(
            VmgLastSector: (uint)(ifoSectors + menuVobSectors + bupSectors) - 1,
            VmgiLastSector: (uint)ifoSectors - 1,
            VmgmVobs: menuVobSectors > 0 ? (uint)ifoSectors : 0);

    /// <summary>
    /// Patch a VTS IFO's four file-location pointers in place (big-endian, at 0x0C / 0x1C /
    /// 0xC0 / 0xC4). Returns true if any byte changed. Throws if the buffer is too short or is
    /// not a VTSI — call only on a full IFO you have parsed.
    /// </summary>
    public static bool WriteVtsPointers(Span<byte> ifo, VtsiPointers p)
    {
        if (ifo.Length < 0xC8 || !HasMagic(ifo, "DVDVIDEO-VTS"))
            throw new ArgumentException("Not a VTS IFO (missing DVDVIDEO-VTS magic or too short).", nameof(ifo));
        bool changed = false;
        changed |= PutU32(ifo, 0x0C, p.VtsLastSector);
        changed |= PutU32(ifo, 0x1C, p.VtsiLastSector);
        changed |= PutU32(ifo, 0xC0, p.VtsmVobs);
        changed |= PutU32(ifo, 0xC4, p.VtsttVobs);
        return changed;
    }

    /// <summary>
    /// Patch a VMG IFO's three file-location pointers in place (big-endian, at 0x0C / 0x1C /
    /// 0xC0). Returns true if any byte changed.
    /// </summary>
    public static bool WriteVmgPointers(Span<byte> vmg, VmgiPointers p)
    {
        if (vmg.Length < 0xC4 || !HasMagic(vmg, "DVDVIDEO-VMG"))
            throw new ArgumentException("Not a VMG IFO (missing DVDVIDEO-VMG magic or too short).", nameof(vmg));
        bool changed = false;
        changed |= PutU32(vmg, 0x0C, p.VmgLastSector);
        changed |= PutU32(vmg, 0x1C, p.VmgiLastSector);
        changed |= PutU32(vmg, 0xC0, p.VmgmVobs);
        return changed;
    }

    private static bool PutU32(Span<byte> buf, int off, uint value)
    {
        var slot = buf.Slice(off, 4);
        if (BinaryPrimitives.ReadUInt32BigEndian(slot) == value) return false;
        BinaryPrimitives.WriteUInt32BigEndian(slot, value);
        return true;
    }

    private static bool HasMagic(ReadOnlySpan<byte> data, string magic)
    {
        var m = Encoding.ASCII.GetBytes(magic);
        return data.Length >= m.Length && data[..m.Length].SequenceEqual(m);
    }
}
