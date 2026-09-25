// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.Nrg;

/// <summary>
/// Writes a Nero NRG v2 (NER5) image: the track data at the front, then a CUEX
/// cue table and a DAOX track table, an END! marker, and the "NER5" footer that
/// points back to the first chunk. The layout mirrors <see cref="NrgParser"/>,
/// and the two round-trip.
/// </summary>
public static class NrgWriter
{
    private const int DaoxTrackEntrySize = 42;

    public sealed record TrackInput
    {
        public required NrgTrackMode Mode { get; init; }
        public required int SectorSize { get; init; }
        public required long StartLba { get; init; }
        public required uint LengthSectors { get; init; }
        public string Filename { get; init; } = "TRACK.DAT";
        public byte[]? Data { get; init; }
        public Action<Stream>? DataWriter { get; init; }

        public long StoredBytes => (long)LengthSectors * SectorSize;
    }

    private const int DaoiTrackEntrySize = 30;   // v1 track entry

    public static void Write(Stream output, IReadOnlyList<TrackInput> tracks, NrgVersion version = NrgVersion.V2)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0) throw new ArgumentException("An NRG needs at least one track.", nameof(tracks));

        // 1) Track data, front of the file. Record each track's byte offsets.
        var offsets = new List<(long Start, long End)>();
        long cursor = 0;
        foreach (var t in tracks)
        {
            long start = cursor;
            if (t.Data is not null)
            {
                if (t.Data.Length != t.StoredBytes)
                    throw new ArgumentException($"Track '{t.Filename}' data length != expected {t.StoredBytes}.");
                output.Write(t.Data);
            }
            else if (t.DataWriter is not null)
            {
                var counter = new CountingStream(output);
                t.DataWriter(counter);
                if (counter.BytesWritten != t.StoredBytes)
                    throw new InvalidOperationException(
                        $"Track '{t.Filename}' writer produced {counter.BytesWritten:N0} bytes, " +
                        $"expected {t.StoredBytes:N0}.");
            }
            else
            {
                WriteZeros(output, t.StoredBytes);
            }
            cursor += t.StoredBytes;
            offsets.Add((start, cursor));
        }

        long chunkOffset = cursor;
        long leadOutLba = tracks[^1].StartLba + tracks[^1].LengthSectors;

        // 2) The cue chunk: one index-1 entry per track, then a lead-out. v2 uses
        // CUEX with a 32-bit LBA; v1 uses CUES with an MSF address.
        using var cue = new MemoryStream();
        for (int i = 0; i < tracks.Count; i++)
        {
            byte ctrl = tracks[i].Mode == NrgTrackMode.Audio ? (byte)0x01 : (byte)0x41;
            WriteCueEntry(cue, version, ctrl, (byte)(i + 1), index: 1, (int)tracks[i].StartLba);
        }
        WriteCueEntry(cue, version, 0x01, 0xAA, index: 1, (int)leadOutLba);
        WriteChunk(output, version == NrgVersion.V2 ? NrgFormat.TagCuex : NrgFormat.TagCues, cue.ToArray());

        // 3) The Disc-At-Once chunk: header + per-track entry. v2 (DAOX) uses
        // 64-bit offsets in 42-byte entries; v1 (DAOI) uses 32-bit in 30-byte.
        using var dao = new MemoryStream();
        WriteU32Be(dao, 0);                           // redundant size
        dao.Write(new byte[14]);                      // UPC/MCN
        dao.WriteByte(0x00);                          // toc type
        dao.WriteByte(0x00);                          // reserved
        dao.WriteByte(1);                             // first track
        dao.WriteByte((byte)tracks.Count);            // last track
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var (start, end) = offsets[i];
            if (version == NrgVersion.V2)
            {
                var e = new byte[DaoxTrackEntrySize];
                BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(12, 2), (ushort)t.SectorSize);
                e[14] = NrgFormat.ModeCode(t.Mode, t.SectorSize);
                BinaryPrimitives.WriteUInt64BigEndian(e.AsSpan(18, 8), (ulong)start); // index0
                BinaryPrimitives.WriteUInt64BigEndian(e.AsSpan(26, 8), (ulong)start); // index1
                BinaryPrimitives.WriteUInt64BigEndian(e.AsSpan(34, 8), (ulong)end);   // end
                dao.Write(e);
            }
            else
            {
                var e = new byte[DaoiTrackEntrySize];
                BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(12, 2), (ushort)t.SectorSize);
                e[14] = NrgFormat.ModeCode(t.Mode, t.SectorSize);
                BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(18, 4), (uint)start);  // index0
                BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(22, 4), (uint)start);  // index1
                BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(26, 4), (uint)end);    // end
                dao.Write(e);
            }
        }
        WriteChunk(output, version == NrgVersion.V2 ? NrgFormat.TagDaox : NrgFormat.TagDaoi, dao.ToArray());

        // 4) END! and the footer that points back to the first chunk.
        WriteChunk(output, NrgFormat.TagEnd, Array.Empty<byte>());

        if (version == NrgVersion.V2)
        {
            Span<byte> footer = stackalloc byte[12];
            NrgFormat.FooterV2.CopyTo(footer);
            BinaryPrimitives.WriteUInt64BigEndian(footer[4..], (ulong)chunkOffset);
            output.Write(footer);
        }
        else
        {
            Span<byte> footer = stackalloc byte[8];
            NrgFormat.FooterV1.CopyTo(footer);
            BinaryPrimitives.WriteUInt32BigEndian(footer[4..], (uint)chunkOffset);
            output.Write(footer);
        }
    }

    private static void WriteCueEntry(Stream s, NrgVersion version, byte ctrl, byte track, byte index, int lba)
    {
        Span<byte> e = stackalloc byte[8];
        e[0] = ctrl;
        e[2] = index;
        if (version == NrgVersion.V2)
        {
            e[1] = track;                             // binary track number
            BinaryPrimitives.WriteInt32BigEndian(e[4..], lba);
        }
        else
        {
            e[1] = ToBcd(track);                      // BCD track number
            // CUES stores an absolute MSF (LBA + 150), in BCD.
            int abs = lba + 150;
            e[5] = ToBcd((byte)(abs / (75 * 60)));
            e[6] = ToBcd((byte)(abs / 75 % 60));
            e[7] = ToBcd((byte)(abs % 75));
        }
        s.Write(e);
    }

    private static byte ToBcd(byte v) => (byte)(((v / 10) << 4) | (v % 10));

    private static void WriteChunk(Stream output, byte[] tag, byte[] payload)
    {
        output.Write(tag);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)payload.Length);
        output.Write(len);
        output.Write(payload);
    }

    private static void WriteU32Be(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteZeros(Stream s, long count)
    {
        Span<byte> chunk = stackalloc byte[4096];
        chunk.Clear();
        while (count > 0)
        {
            int n = (int)Math.Min(count, chunk.Length);
            s.Write(chunk[..n]);
            count -= n;
        }
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); BytesWritten += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); BytesWritten += buffer.Length; }
        public override void WriteByte(byte value) { inner.WriteByte(value); BytesWritten++; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
