// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Iso;

/// <summary>
/// A read-only, seekable view of the cooked user data inside a raw disc track —
/// the same idea as CdiUserDataStream, but for a plain .bin/.cue track rather
/// than a CDI container. A PlayStation or other raw image stores 2352-byte
/// sectors whose 2048 user bytes are wrapped in sync/header/subheader/EDC/ECC;
/// this presents just those user bytes as one contiguous stream so IsoReader can
/// walk the ISO 9660 filesystem straight out of a bin without extracting it
/// first.
///
/// Does not own the underlying stream.
/// </summary>
public sealed class RawTrackUserDataStream : Stream
{
    private readonly Stream _base;
    private readonly long _trackByteOffset;  // byte offset of the track's first sector
    private readonly int _sectorSize;        // raw stored bytes per sector (2352, 2336, 2048)
    private readonly int _userOffset;        // where user data starts within a sector
    private readonly int _userLength;        // user bytes per sector
    private readonly long _sectors;
    private long _position;

    public RawTrackUserDataStream(Stream baseStream, long trackByteOffset,
                                  int sectorSize, int userOffset, int userLength, long sectors)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        if (!baseStream.CanSeek)
            throw new ArgumentException("A seekable stream is required.", nameof(baseStream));
        if (sectorSize <= 0 || userLength <= 0 || userOffset < 0 || userOffset + userLength > sectorSize)
            throw new ArgumentOutOfRangeException(nameof(userLength), "Bad sector layout.");

        _base = baseStream;
        _trackByteOffset = trackByteOffset;
        _sectorSize = sectorSize;
        _userOffset = userOffset;
        _userLength = userLength;
        _sectors = Math.Max(0, sectors);
    }

    public override long Length => _sectors * _userLength;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        long remaining = Length - _position;
        if (remaining <= 0 || buffer.Length == 0) return 0;

        int want = (int)Math.Min(buffer.Length, remaining);
        int done = 0;
        while (done < want)
        {
            long sector = (_position + done) / _userLength;
            int within = (int)((_position + done) % _userLength);
            int chunk = Math.Min(_userLength - within, want - done);

            long physical = _trackByteOffset + sector * _sectorSize + _userOffset + within;
            _base.Seek(physical, SeekOrigin.Begin);

            int got = _base.Read(buffer.Slice(done, chunk));
            if (got <= 0) break;
            done += got;
        }
        _position += done;
        return done;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("Cannot seek before the start of the track.");
        _position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
