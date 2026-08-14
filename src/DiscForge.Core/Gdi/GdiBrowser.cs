// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Iso;

namespace DiscForge.Core.Gdi;

/// <summary>
/// Browses the ISO 9660 filesystem on a Dreamcast GD-ROM's high-density game
/// track. Two things make this different from browsing an ordinary image: the
/// game track's sectors may be raw (2352-byte Mode 1, so the 2048-byte user data
/// sits 16 bytes in), and its ISO is addressed from the track's start LBA
/// (45000), not from zero. This cooks the track's user data on the fly and reads
/// it with the base-LBA ISO reader, so the game's files list and extract exactly
/// as on any other disc.
///
/// Structure only — it lists and copies files a person's own backup already
/// holds; a GD-ROM carries no encryption, so there is nothing to decrypt.
/// </summary>
public static class GdiBrowser
{
    /// <summary>List the files on a GD-ROM's game track, given the .gdi and the
    /// directory its track files live in.</summary>
    public static IsoDirectory Browse(GdiDisc disc, string gdiDirectory)
    {
        var (track, path) = ResolveBootTrack(disc, gdiDirectory);
        using var cooked = OpenCooked(path, track);
        return IsoReader.Read(cooked, track.StartLba);
    }

    /// <summary>Browse via a .gdi file path.</summary>
    public static IsoDirectory BrowseFile(string gdiPath)
    {
        ArgumentNullException.ThrowIfNull(gdiPath);
        var disc = GdiParser.ParseFile(gdiPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(gdiPath)) ?? ".";
        return Browse(disc, dir);
    }

    /// <summary>Copy one game file's bytes out to a stream.</summary>
    public static void ExtractFile(GdiDisc disc, string gdiDirectory, IsoEntry entry, Stream output)
    {
        var (track, path) = ResolveBootTrack(disc, gdiDirectory);
        using var cooked = OpenCooked(path, track);
        IsoReader.ExtractFile(cooked, track.StartLba, entry, output);
    }

    private static (GdiTrack Track, string Path) ResolveBootTrack(GdiDisc disc, string gdiDirectory)
    {
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(gdiDirectory);

        var track = disc.BootDataTrack
            ?? throw new GdiFormatException(
                "This image has no high-density data track, so it has no game filesystem to browse.");

        string path = Path.IsPathRooted(track.FileName)
            ? track.FileName
            : Path.Combine(gdiDirectory, track.FileName);
        if (!File.Exists(path))
            throw new GdiFormatException($"The game track file '{track.FileName}' is not beside the index.");

        return (track, path);
    }

    private static Stream OpenCooked(string trackPath, GdiTrack track)
    {
        var fs = File.OpenRead(trackPath);
        // A 2048-byte track is already cooked user data; a raw 2352 track is
        // cooked on the fly by CookedTrackStream.
        return track.SectorSize == 2048
            ? new SubStream(fs, track.Offset)
            : new CookedTrackStream(fs, track.Offset, track.SectorSize);
    }
}

/// <summary>A read-only window over a stream starting at a fixed byte offset —
/// used when a track's data begins partway into its file.</summary>
internal sealed class SubStream(Stream inner, long start) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => inner.Length - start;
    public override long Position { get => inner.Position - start; set => inner.Position = start + value; }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) =>
        inner.Seek(origin == SeekOrigin.Begin ? start + offset : offset, origin) - start;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}

/// <summary>
/// Presents a raw (2352-byte Mode 1) track as cooked 2048-byte user data: cooked
/// sector i's bytes live at raw offset i×2352 + 16. Read-only, seekable, O(1)
/// memory — so a gigabyte game track browses without being copied.
/// </summary>
internal sealed class CookedTrackStream : Stream
{
    private const int Cooked = 2048;
    private const int UserDataOffset = 16;   // sync (12) + header (4) of a Mode 1 sector
    private readonly Stream _raw;
    private readonly long _trackStart;
    private readonly int _rawSectorSize;
    private readonly long _length;
    private long _position;

    public CookedTrackStream(Stream raw, long trackStart, int rawSectorSize)
    {
        _raw = raw;
        _trackStart = trackStart;
        _rawSectorSize = rawSectorSize;
        long rawBytes = raw.Length - trackStart;
        long sectors = rawBytes / rawSectorSize;
        _length = sectors * Cooked;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0 || _position >= _length) return 0;
        count = (int)Math.Min(count, _length - _position);

        int produced = 0;
        while (count > 0)
        {
            long sector = _position / Cooked;
            int within = (int)(_position % Cooked);
            int chunk = Math.Min(count, Cooked - within);

            long rawAt = _trackStart + sector * _rawSectorSize + UserDataOffset + within;
            _raw.Seek(rawAt, SeekOrigin.Begin);
            int got = _raw.Read(buffer, offset, chunk);
            if (got <= 0) break;

            _position += got;
            produced += got;
            offset += got;
            count -= got;
        }
        return produced;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => _position,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _raw.Dispose(); base.Dispose(disposing); }
}
