// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Vcd;

/// <summary>The two disc profiles this control-file layer covers.</summary>
public enum VcdKind { Vcd, Svcd }

public sealed class VcdFormatException(string message) : Exception(message);

/// <summary>The header of a Video CD <c>INFO.VCD</c> (or Super Video CD
/// <c>INFO.SVD</c>): the disc identification the player reads first.</summary>
public sealed record VcdInfo
{
    public required VcdKind Kind { get; init; }
    /// <summary>Standard version — 1 (VCD 1.1) or 2 (VCD 2.0 / SVCD 1.0).</summary>
    public int Version { get; init; } = 2;
    /// <summary>Album identifier, up to 16 ASCII characters.</summary>
    public string AlbumId { get; init; } = "";
    public int VolumeCount { get; init; } = 1;
    public int VolumeNumber { get; init; } = 1;
}

/// <summary>One entry-point in <c>ENTRIES.VCD</c>: a track and the MSF address a
/// player jumps to for it.</summary>
public sealed record VcdEntry
{
    public required int TrackNumber { get; init; }
    public required int Minute { get; init; }
    public required int Second { get; init; }   // 0–59
    public required int Frame { get; init; }     // 0–74
}

/// <summary>The <c>ENTRIES.VCD</c> table — the disc's entry-point list.</summary>
public sealed record VcdEntries
{
    public required VcdKind Kind { get; init; }
    public int Version { get; init; } = 2;
    public required IReadOnlyList<VcdEntry> Entries { get; init; }
}

/// <summary>
/// A clean-room reader/writer for the two mandatory Video CD / Super Video CD
/// control files — <c>INFO.VCD</c> and <c>ENTRIES.VCD</c> (the SVCD spellings are
/// <c>INFO.SVD</c> / <c>ENTRIES.SVD</c>). These are small, fixed-layout binary
/// files a VCD player reads to identify the disc and find each sequence's entry
/// point; they are unencrypted and carry no protection, so this stays well inside
/// the clean-room boundary.
///
/// Provenance — important: the field layout here is written from the public
/// description of the Video CD control-file format (the ASCII magic, the version
/// byte, the album/volume fields, and the BCD-MSF entry table). It is **not**
/// derived from vcdimager's GPL source, in keeping with DiscForge's rule that no
/// code is ported from GPL implementations (docs/COMPARISON.md §13).
///
/// Scope — honest, like the NRG reader before a real sample: this emits the
/// **pbc-less** profile — the header identification and the entry-point table
/// that <c>vcdimager -t</c> ("simple pbc-less VCD/SVCD") produces. The playback-
/// control structures (PSD, LOT) and the segment-item table are not emitted;
/// their reserved regions are left zero. It is validated by round trip (writer ↔
/// reader agree), and awaits validation against a control file produced by a real
/// VCD authoring tool — at which point any field this reads differently is a bug
/// to fix against the sample, not a design change. See docs/VCD_AUTHORING.md.
/// </summary>
public static class VcdControl
{
    public const int SectorSize = 2048;

    private const int MaxEntries = 500;   // the Video CD limit

    // ---- INFO.VCD -----------------------------------------------------------

    public static byte[] WriteInfo(VcdInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Version is < 1 or > 2)
            throw new ArgumentException("VCD version must be 1 or 2.", nameof(info));

        var b = new byte[SectorSize];
        Encoding.ASCII.GetBytes(InfoMagic(info.Kind)).CopyTo(b, 0);   // 0x00 [8]
        b[8] = (byte)info.Version;                                     // 0x08 version
        b[9] = (byte)(info.Version >= 2 ? 0x01 : 0x00);               // 0x09 system profile tag

        // 0x0A [16] album id, space-padded ASCII.
        var album = (info.AlbumId ?? "").PadRight(16).Substring(0, 16);
        Encoding.ASCII.GetBytes(album).CopyTo(b, 0x0A);

        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x1A), (ushort)Math.Max(1, info.VolumeCount));  // 0x1A
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x1C), (ushort)Math.Max(1, info.VolumeNumber)); // 0x1C
        // Remaining fields (flags, PSD size, segment table) left zero — pbc-less.
        return b;
    }

    public static VcdInfo ReadInfo(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 0x1E)
            throw new VcdFormatException("INFO file is too short to hold a Video CD header.");

        var kind = KindFromMagic(data, InfoMagic(VcdKind.Vcd), InfoMagic(VcdKind.Svcd))
            ?? throw new VcdFormatException(
                "No \"VIDEO_CD\" or \"SUPERVCD\" signature — this is not a VCD/SVCD INFO file.");

        return new VcdInfo
        {
            Kind = kind,
            Version = data[8],
            AlbumId = Encoding.ASCII.GetString(data, 0x0A, 16).TrimEnd(),
            VolumeCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x1A)),
            VolumeNumber = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x1C)),
        };
    }

    // ---- ENTRIES.VCD --------------------------------------------------------

    public static byte[] WriteEntries(VcdEntries entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Entries.Count is 0 or > MaxEntries)
            throw new ArgumentException(
                $"A VCD entry table holds 1–{MaxEntries} entries; got {entries.Entries.Count}.");

        var b = new byte[SectorSize];
        Encoding.ASCII.GetBytes(EntriesMagic(entries.Kind)).CopyTo(b, 0);  // 0x00 [8]
        b[8] = (byte)entries.Version;                                       // 0x08 version
        b[9] = (byte)(entries.Version >= 2 ? 0x01 : 0x00);                 // 0x09 system profile tag
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x0A), (ushort)entries.Entries.Count); // 0x0A count

        int p = 0x0C;
        foreach (var e in entries.Entries)
        {
            if (e.TrackNumber is < 1 or > 99)
                throw new ArgumentException($"Track number {e.TrackNumber} is out of range (1–99).");
            if (e.Second is < 0 or > 59 || e.Frame is < 0 or > 74 || e.Minute < 0)
                throw new ArgumentException(
                    $"MSF {e.Minute}:{e.Second}:{e.Frame} is out of range (sec 0–59, frame 0–74).");

            b[p] = Bcd(e.TrackNumber);
            b[p + 1] = Bcd(e.Minute);
            b[p + 2] = Bcd(e.Second);
            b[p + 3] = Bcd(e.Frame);
            p += 4;
        }
        return b;
    }

    public static VcdEntries ReadEntries(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 0x0C)
            throw new VcdFormatException("ENTRIES file is too short to hold a Video CD entry table.");

        var kind = KindFromMagic(data, EntriesMagic(VcdKind.Vcd), EntriesMagic(VcdKind.Svcd))
            ?? throw new VcdFormatException(
                "No \"ENTRYVCD\" or \"ENTRYSVD\" signature — this is not a VCD/SVCD ENTRIES file.");

        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x0A));
        if (count is < 0 or > MaxEntries) count = Math.Clamp(count, 0, MaxEntries);

        var list = new List<VcdEntry>(count);
        int p = 0x0C;
        for (int i = 0; i < count && p + 4 <= data.Length; i++, p += 4)
        {
            list.Add(new VcdEntry
            {
                TrackNumber = FromBcd(data[p]),
                Minute = FromBcd(data[p + 1]),
                Second = FromBcd(data[p + 2]),
                Frame = FromBcd(data[p + 3]),
            });
        }

        return new VcdEntries { Kind = kind, Version = data[8], Entries = list };
    }

    // ---- helpers ------------------------------------------------------------

    private static string InfoMagic(VcdKind k) => k == VcdKind.Vcd ? "VIDEO_CD" : "SUPERVCD";
    private static string EntriesMagic(VcdKind k) => k == VcdKind.Vcd ? "ENTRYVCD" : "ENTRYSVD";

    private static VcdKind? KindFromMagic(byte[] data, string vcd, string svcd)
    {
        if (Matches(data, vcd)) return VcdKind.Vcd;
        if (Matches(data, svcd)) return VcdKind.Svcd;
        return null;
    }

    private static bool Matches(byte[] data, string magic)
    {
        if (data.Length < magic.Length) return false;
        for (int i = 0; i < magic.Length; i++)
            if (data[i] != (byte)magic[i]) return false;
        return true;
    }

    private static byte Bcd(int n) => (byte)(((n / 10) << 4) | (n % 10));
    private static int FromBcd(byte b) => ((b >> 4) & 0x0F) * 10 + (b & 0x0F);
}
