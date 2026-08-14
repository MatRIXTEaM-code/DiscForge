// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.VideoCd;

/// <summary>Disc/album identity for a Video CD.</summary>
public sealed record VideoCdInfoPlan
{
    /// <summary>VCD 2.0 (2) or VCD 1.1 (1).</summary>
    public int Version { get; init; } = 2;
    /// <summary>Up to 16 chars; longer is truncated, shorter padded with NUL.</summary>
    public string AlbumId { get; init; } = "";
    public int VolumeCount { get; init; } = 1;
    public int VolumeNumber { get; init; } = 1;
    /// <summary>Standard: SVCD/HQ-VCD change the ID string; false = plain VCD.</summary>
    public bool SuperVcd { get; init; }
}

/// <summary>One playable entry point: a track and its start position (MSF).</summary>
public sealed record VideoCdEntry
{
    public required int TrackNumber { get; init; }   // 1..99
    public required int Minute { get; init; }
    public required int Second { get; init; }
    public required int Frame { get; init; }
}

/// <summary>
/// Writes the two control sectors at the heart of a Video CD's <c>VCD/</c>
/// directory: <c>INFO.VCD</c> (album/disc identity and capability flags) and
/// <c>ENTRIES.VCD</c> (the entry-point list a player's "next/prev" uses). Each is a
/// single 2048-byte Mode 2 Form 1 sector with a fixed White Book layout.
///
/// Scope, stated plainly: this emits the control layer for a straightforward VCD
/// WITHOUT play-back control (no PSD/LOT scripting) — the common case, and the part
/// whose byte layout is unambiguous and round-trip verifiable here. Assembling a
/// complete, player-checked VCD *image* additionally needs the MPEG track laid into
/// Mode 2 Form 2 sectors and referenced from the ISO 9660 tree; that image-assembly
/// step should be validated against a reference VCD before it is relied on, so it is
/// kept separate rather than guessed at. Nothing here decodes or encrypts anything.
/// </summary>
public static class VideoCdControl
{
    public const int SectorSize = 2048;
    public const int MaxEntries = 500;

    // ---- INFO.VCD ----------------------------------------------------------

    public static byte[] BuildInfo(VideoCdInfoPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Version is not (1 or 2))
            throw new ArgumentException($"Unsupported VCD version {plan.Version} (expected 1 or 2).");

        var s = new byte[SectorSize];
        string id = plan.SuperVcd ? "SUPERVCD" : "VIDEO_CD";
        Encoding.ASCII.GetBytes(id).CopyTo(s, 0x00);          // 8-byte ID
        s[0x08] = (byte)plan.Version;                          // version
        s[0x09] = 0x00;                                        // system profile tag

        // album_desc[16] at 0x0A
        var album = Encoding.ASCII.GetBytes(Trunc(plan.AlbumId, 16));
        album.CopyTo(s, 0x0A);

        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(0x1A), (ushort)Math.Max(1, plan.VolumeCount));
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(0x1C), (ushort)Math.Max(1, plan.VolumeNumber));
        // 0x1E pal_flags[13]  — NTSC leaves them zero
        // 0x2B flags u16      = 0 (no restrictions, no PBC)
        // 0x2D psd_size u32   = 0 (no play-sequence descriptor)
        // 0x31 first_seg_addr = 0
        s[0x34] = 8;                                           // offset multiplier (standard 8)
        // 0x35 lot_entries u16 = 0, 0x37 item_count u16 = 0
        return s;
    }

    // ---- ENTRIES.VCD -------------------------------------------------------

    public static byte[] BuildEntries(IReadOnlyList<VideoCdEntry> entries, int version = 2, bool superVcd = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count is 0 or > MaxEntries)
            throw new ArgumentException($"A VCD needs 1–{MaxEntries} entries; got {entries.Count}.");

        var s = new byte[SectorSize];
        string id = superVcd ? "ENTRYSVD" : "ENTRYVCD";
        Encoding.ASCII.GetBytes(id).CopyTo(s, 0x00);
        s[0x08] = (byte)version;
        s[0x09] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(0x0A), (ushort)entries.Count);

        int at = 0x0C;
        foreach (var e in entries)
        {
            if (e.TrackNumber is < 1 or > 99)
                throw new ArgumentException($"Entry track number {e.TrackNumber} out of range (1–99).");
            s[at] = Bcd(e.TrackNumber);
            s[at + 1] = Bcd(e.Minute);
            s[at + 2] = Bcd(e.Second);
            s[at + 3] = Bcd(e.Frame);
            at += 4;
        }
        return s;
    }

    // ---- read-back (for verification / inspection) -------------------------

    public sealed record ParsedInfo(string Id, int Version, string AlbumId, int VolumeCount, int VolumeNumber);

    public static ParsedInfo ReadInfo(ReadOnlySpan<byte> s)
    {
        if (s.Length < SectorSize) throw new ArgumentException("INFO.VCD must be a 2048-byte sector.");
        string id = Encoding.ASCII.GetString(s.Slice(0, 8)).TrimEnd('\0', ' ');
        int ver = s[0x08];
        string album = Encoding.ASCII.GetString(s.Slice(0x0A, 16)).TrimEnd('\0', ' ');
        int volCount = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(0x1A));
        int volNum = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(0x1C));
        return new ParsedInfo(id, ver, album, volCount, volNum);
    }

    public static IReadOnlyList<VideoCdEntry> ReadEntries(ReadOnlySpan<byte> s)
    {
        if (s.Length < SectorSize) throw new ArgumentException("ENTRIES.VCD must be a 2048-byte sector.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(0x0A));
        if (count is < 0 or > MaxEntries) throw new ArgumentException($"ENTRIES.VCD entry count {count} is out of range.");
        var list = new List<VideoCdEntry>(count);
        int at = 0x0C;
        for (int i = 0; i < count; i++, at += 4)
        {
            list.Add(new VideoCdEntry
            {
                TrackNumber = UnBcd(s[at]),
                Minute = UnBcd(s[at + 1]),
                Second = UnBcd(s[at + 2]),
                Frame = UnBcd(s[at + 3]),
            });
        }
        return list;
    }

    // ---- helpers -----------------------------------------------------------

    private static string Trunc(string v, int max)
    {
        v ??= "";
        return v.Length <= max ? v : v[..max];
    }
    private static byte Bcd(int v) => (byte)(((v / 10) << 4) | (v % 10));
    private static int UnBcd(byte b) => ((b >> 4) * 10) + (b & 0x0F);
}
