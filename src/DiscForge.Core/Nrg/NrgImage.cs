// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Nrg;

/// <summary>Track data mode, as Nero records it.</summary>
public enum NrgTrackMode { Audio, Mode1, Mode2 }

/// <summary>Which NRG revision — v1 uses the "NERO" footer, 32-bit offsets and
/// CUES/DAOI chunks; v2 uses "NER5", 64-bit offsets and CUEX/DAOX.</summary>
public enum NrgVersion { V1, V2 }

/// <summary>One track in a Nero NRG image.</summary>
public sealed record NrgTrack
{
    public required int Number { get; init; }
    public required NrgTrackMode Mode { get; init; }
    public required int SectorSize { get; init; }
    /// <summary>Absolute start LBA on the disc (from the cue table).</summary>
    public required long StartLba { get; init; }
    public required uint LengthSectors { get; init; }
    /// <summary>Byte offset of the track's data within the NRG file.</summary>
    public required long DataOffset { get; init; }

    public bool IsData => Mode != NrgTrackMode.Audio;
    public long StoredBytes => (long)LengthSectors * SectorSize;
}

/// <summary>A parsed Nero NRG image.</summary>
public sealed record NrgImage
{
    public required bool IsV2 { get; init; }
    public required IReadOnlyList<NrgTrack> Tracks { get; init; }
    public long TotalBytes => Tracks.Sum(t => t.StoredBytes);
}

public sealed class NrgFormatException(string message) : Exception(message);

/// <summary>
/// Shared knowledge of the NRG container: the Nero mode codes and the chunk /
/// footer tags. Nero stores the track data at the front of the file and a
/// chunk-based table of contents at the back, reached through a footer at the
/// very end.
/// </summary>
internal static class NrgFormat
{
    public static readonly byte[] FooterV2 = Encoding.ASCII.GetBytes("NER5");
    public static readonly byte[] FooterV1 = Encoding.ASCII.GetBytes("NERO");
    public static readonly byte[] TagCuex = Encoding.ASCII.GetBytes("CUEX");
    public static readonly byte[] TagCues = Encoding.ASCII.GetBytes("CUES");
    public static readonly byte[] TagDaox = Encoding.ASCII.GetBytes("DAOX");
    public static readonly byte[] TagDaoi = Encoding.ASCII.GetBytes("DAOI");
    public static readonly byte[] TagEnd = Encoding.ASCII.GetBytes("END!");

    /// <summary>Map a mode + sector size to Nero's one-byte mode code.</summary>
    public static byte ModeCode(NrgTrackMode mode, int sectorSize) => (mode, sectorSize) switch
    {
        (NrgTrackMode.Mode1, 2048) => 0x00,
        (NrgTrackMode.Mode2, 2336) => 0x03,
        (NrgTrackMode.Mode1, 2352) => 0x05,
        (NrgTrackMode.Mode2, 2352) => 0x06,
        (NrgTrackMode.Audio, 2352) => 0x07,
        _ => throw new NrgFormatException(
            $"No Nero mode code for {mode} at {sectorSize} bytes/sector."),
    };

    /// <summary>Interpret a Nero mode code. The sector size the DAOX entry also
    /// carries is authoritative; this gives the mode and the canonical size.</summary>
    public static (NrgTrackMode Mode, int SectorSize) FromModeCode(byte code) => code switch
    {
        0x00 => (NrgTrackMode.Mode1, 2048),
        0x02 => (NrgTrackMode.Mode1, 2048),   // Mode 2 Form 1, cooked
        0x03 => (NrgTrackMode.Mode2, 2336),
        0x05 => (NrgTrackMode.Mode1, 2352),
        0x06 => (NrgTrackMode.Mode2, 2352),
        0x07 => (NrgTrackMode.Audio, 2352),
        _ => throw new NrgFormatException($"Unknown Nero mode code 0x{code:X2}."),
    };

    public static bool Match(byte[] data, int at, byte[] tag)
    {
        if (at < 0 || at + tag.Length > data.Length) return false;
        for (int i = 0; i < tag.Length; i++)
            if (data[at + i] != tag[i]) return false;
        return true;
    }

    public static uint ReadU32Be(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32BigEndian(s);
    public static ulong ReadU64Be(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt64BigEndian(s);
    public static int ReadI32Be(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadInt32BigEndian(s);
}
