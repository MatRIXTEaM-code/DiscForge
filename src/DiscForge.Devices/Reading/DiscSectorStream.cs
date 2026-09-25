// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// A read-only stream over a disc's sectors, so a burned disc can be compared
/// against the image it came from without dumping it to a file first.
///
/// Reads via READ(10), the cooked 2048-byte path — the same command and reasoning
/// as <see cref="DiscReader"/>. Sequential access is the norm here (verification
/// walks the disc once), so a single-sector cache is enough to make byte-wise
/// comparison practical.
///
/// Owns its device handle; dispose it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiscSectorStream : Stream
{
    private readonly SptiDevice _dev;
    private readonly uint _startLba;
    private readonly int _sectorBytes;
    private readonly long _length;

    private readonly byte[] _cache;
    private long _cachedSector = -1;
    private long _position;

    /// <param name="driveLetter">Drive to read.</param>
    /// <param name="startLba">First sector of the region.</param>
    /// <param name="sectorBytes">Bytes per sector (2048 for cooked data).</param>
    /// <param name="lengthBytes">How much to expose; 0 means to the end of the track.</param>
    public DiscSectorStream(char driveLetter, uint startLba, int sectorBytes, long lengthBytes = 0)
    {
        if (sectorBytes <= 0) throw new ArgumentOutOfRangeException(nameof(sectorBytes));

        _dev = new SptiDevice(driveLetter);
        _startLba = startLba;
        _sectorBytes = sectorBytes;
        _cache = new byte[sectorBytes];

        // Without a stated length, expose a large region and let reads fail at
        // the real end — the caller compares a known number of bytes anyway.
        _length = lengthBytes > 0 ? lengthBytes : long.MaxValue;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

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
        if (buffer.Length == 0) return 0;

        int done = 0;
        while (done < buffer.Length)
        {
            long sector = _position / _sectorBytes;
            int within = (int)(_position % _sectorBytes);

            if (!EnsureCached(sector)) break;

            int take = Math.Min(_sectorBytes - within, buffer.Length - done);
            _cache.AsSpan(within, take).CopyTo(buffer[done..]);
            done += take;
            _position += take;
        }
        return done;
    }

    private bool EnsureCached(long sector)
    {
        if (_cachedSector == sector) return true;

        var cdb = MmcCommands.Read10((uint)(_startLba + sector), 1);
        var result = _dev.SendCommand(cdb, _cache, SptiDataDirection.In, timeoutSeconds: 30);
        if (!result.Success)
        {
            // Past the end of the recorded area, or an unreadable sector: a short
            // read is the honest answer, and the caller reports the mismatch.
            _cachedSector = -1;
            return false;
        }

        _cachedSector = sector;
        return true;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("Cannot seek before the start of the disc region.");
        _position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dev.Dispose();
        base.Dispose(disposing);
    }
}
