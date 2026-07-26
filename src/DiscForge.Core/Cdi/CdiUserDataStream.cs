// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Cdi;

/// <summary>
/// A read-only, seekable view of one CDI track's *cooked* user data, mapped onto
/// the underlying image on the fly.
///
/// A data track's user bytes are what an ISO 9660 filesystem sits in, but they're
/// interleaved with sync/header/EDC depending on the stored sector size. This
/// presents just the user bytes as one contiguous stream, so
/// <c>IsoReader.Read(new CdiUserDataStream(cdi, track))</c> can browse a disc
/// image directly — no extraction to memory or a temp file, which matters when
/// the track is a 4.7 GB DVD.
///
/// Does not own the underlying stream.
/// </summary>
public sealed class CdiUserDataStream : Stream
{
    private readonly Stream _cdi;
    private readonly long _trackOffset;      // byte offset of the track in the CDI
    private readonly int _sectorSize;        // stored bytes per sector
    private readonly int _userOffset;        // where user data starts within a sector
    private readonly int _userLength;        // user bytes per sector
    private readonly long _sectors;
    private long _position;

    public CdiUserDataStream(Stream cdi, CdiTrack track)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        ArgumentNullException.ThrowIfNull(track);
        if (!cdi.CanSeek)
            throw new ArgumentException("A seekable CDI stream is required.", nameof(cdi));

        var (offset, length) = CdiExtractor.UserDataWindow(track.SectorSize, track.Mode);

        _cdi = cdi;
        _sectorSize = (int)track.SectorSize;
        _userOffset = offset;
        _userLength = length;
        _sectors = track.LengthSectors;

        // Track data begins after any stored pregap.
        _trackOffset = track.FileOffset + (long)track.PregapSectors * _sectorSize;
    }

    /// <summary>Total cooked user bytes in the track.</summary>
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

            long physical = _trackOffset + sector * _sectorSize + _userOffset + within;
            _cdi.Seek(physical, SeekOrigin.Begin);

            int got = _cdi.Read(buffer.Slice(done, chunk));
            if (got <= 0) break;     // image truncated: report a short read
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
