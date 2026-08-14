// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Cdi;

/// <summary>
/// CDI container format version, identified by the 4-byte magic at EOF-8.
/// </summary>
public enum CdiVersion : uint
{
    Unknown = 0,

    /// <summary>DiscJuggler 2.x images.</summary>
    V2 = 0x80000004,

    /// <summary>DiscJuggler 3.x images.</summary>
    V3 = 0x80000005,

    /// <summary>DiscJuggler 3.5/4.x/5.x/6.x images. Trailer locator is a
    /// descriptor <b>length</b> (from EOF), not an absolute offset.</summary>
    V35 = 0x80000006,
}

/// <summary>Track data mode as stored in the CDI track block.</summary>
public enum CdiTrackMode : uint
{
    Audio = 0,
    Mode1 = 1,
    /// <summary>Mode 2 (includes Mode2/Mixed as used by Dreamcast data tracks).</summary>
    Mode2 = 2,
}

/// <summary>Stored sector size, from the CDI sector-size code (0/1/2).</summary>
public enum CdiSectorSize
{
    S2048 = 2048,
    S2336 = 2336,
    S2352 = 2352,
}

/// <summary>A single track within a CDI session.</summary>
public sealed record CdiTrack
{
    /// <summary>1-based track number across the whole disc.</summary>
    public required int Number { get; init; }

    /// <summary>0-based session index this track belongs to.</summary>
    public required int SessionIndex { get; init; }

    public required CdiTrackMode Mode { get; init; }
    public required CdiSectorSize SectorSize { get; init; }

    /// <summary>Pregap length in sectors. Pregap data IS stored in the file.</summary>
    public required uint PregapSectors { get; init; }

    /// <summary>Track length in sectors, excluding pregap.</summary>
    public required uint LengthSectors { get; init; }

    /// <summary>Absolute disc LBA where the track (post-pregap) starts.</summary>
    public required uint StartLba { get; init; }

    /// <summary>Pregap + track length in sectors (as stored on disc/file).</summary>
    public required uint TotalSectors { get; init; }

    /// <summary>Absolute byte offset of this track's stored data (incl. pregap)
    /// within the CDI file. Computed, not stored in the descriptor.</summary>
    public required long FileOffset { get; init; }

    /// <summary>Original source filename recorded by DiscJuggler, if present.</summary>
    public string? SourceFilename { get; init; }

    /// <summary>Stored bytes this track occupies in the file.</summary>
    public long StoredByteLength => (long)TotalSectors * (int)SectorSize;
}

/// <summary>A session: an ordered list of tracks.</summary>
public sealed record CdiSession
{
    public required int Index { get; init; }
    public required IReadOnlyList<CdiTrack> Tracks { get; init; }
}

/// <summary>Parsed representation of a CDI image.</summary>
public sealed record CdiImage
{
    public required CdiVersion Version { get; init; }
    public required long FileLength { get; init; }
    public required long DescriptorOffset { get; init; }
    public required IReadOnlyList<CdiSession> Sessions { get; init; }

    public IEnumerable<CdiTrack> AllTracks => Sessions.SelectMany(s => s.Tracks);
    public int TrackCount => Sessions.Sum(s => s.Tracks.Count);
}
