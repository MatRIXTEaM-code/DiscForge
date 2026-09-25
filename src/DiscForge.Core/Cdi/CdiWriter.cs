// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Cdi;

/// <summary>
/// Writes CDI images in the DiscForge CANONICAL layout — the format the parser
/// reads and the eventual "Create image" workflow will produce. Fully specified
/// in docs/CDI_FORMAT.md; kept byte-for-byte in step with gen_cdi.py so the two
/// independent implementations cross-validate against one written spec.
///
/// This is a descriptor/container writer. It writes caller-supplied track data
/// verbatim; sector cooking (ECC/EDC, subchannel) belongs to a higher layer and
/// to the burn engine, not here.
/// </summary>
public static class CdiWriter
{
    private static readonly byte[] Mark = [0, 0, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>One track's definition plus its raw stored bytes.</summary>
    public sealed record TrackInput
    {
        public required CdiTrackMode Mode { get; init; }
        public required CdiSectorSize SectorSize { get; init; }
        public required uint PregapSectors { get; init; }
        public required uint LengthSectors { get; init; }
        public required uint StartLba { get; init; }
        public string Filename { get; init; } = "TRACK.DAT";

        /// <summary>Raw stored bytes for this track (pregap+track sectors,
        /// SectorSize each). If null, the writer emits zero-filled data of the
        /// correct length — useful for descriptor-only tests.</summary>
        public byte[]? Data { get; init; }

        /// <summary>
        /// Streaming alternative to <see cref="Data"/>: a callback that writes
        /// exactly <see cref="StoredBytes"/> bytes to the output. Lets a
        /// DVD-sized track be written without ever holding it in memory. If both
        /// are null the writer emits zeros; setting both is an error.
        /// </summary>
        public Action<Stream>? DataWriter { get; init; }

        public uint TotalSectors => PregapSectors + LengthSectors;
        public long StoredBytes => (long)TotalSectors * (int)SectorSize;
    }

    public static void Write(Stream output, CdiVersion version, IReadOnlyList<IReadOnlyList<TrackInput>> sessions)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sessions);
        if (version == CdiVersion.Unknown)
            throw new ArgumentException("Version must be V2, V3, or V35.", nameof(version));

        // 1) Track data region (concatenated, descriptor order).
        long trackDataLength = 0;
        foreach (var s in sessions)
            foreach (var t in s)
            {
                if (t.Data is not null && t.DataWriter is not null)
                    throw new ArgumentException(
                        $"Track '{t.Filename}' sets both Data and DataWriter; use one.");
                if (t.Data is not null && t.Data.Length != t.StoredBytes)
                    throw new ArgumentException(
                        $"Track '{t.Filename}' data length {t.Data.Length} != expected {t.StoredBytes}.");
                trackDataLength += t.StoredBytes;
            }

        foreach (var s in sessions)
            foreach (var t in s)
            {
                if (t.Data is not null)
                {
                    output.Write(t.Data);
                }
                else if (t.DataWriter is not null)
                {
                    // Count what the callback emits: a short or long write would
                    // silently corrupt every offset in the descriptor.
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
            }

        // 2) Descriptor.
        var desc = BuildDescriptor(sessions);
        output.Write(desc);

        // 3) Trailer.
        Span<byte> trailer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[..4], (uint)version);
        uint locator = version == CdiVersion.V35
            ? (uint)(desc.Length + 8)          // descriptor length from EOF
            : (uint)trackDataLength;           // absolute descriptor offset
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], locator);
        output.Write(trailer);
    }

    private static byte[] BuildDescriptor(IReadOnlyList<IReadOnlyList<TrackInput>> sessions)
    {
        using var ms = new MemoryStream();
        WriteU16(ms, (ushort)sessions.Count);
        foreach (var session in sessions)
        {
            WriteU16(ms, (ushort)session.Count);
            foreach (var t in session)
            {
                WriteU32(ms, 0);               // lead-in
                ms.Write(Mark);
                ms.Write(Mark);
                WriteU32(ms, 0);               // reserved0

                var fn = Encoding.ASCII.GetBytes(t.Filename);
                if (fn.Length > 255) throw new ArgumentException("Filename too long.");
                ms.WriteByte((byte)fn.Length);
                ms.Write(fn);

                WriteU32(ms, t.PregapSectors);
                WriteU32(ms, t.LengthSectors);
                WriteU32(ms, (uint)t.Mode);
                WriteU32(ms, t.StartLba);
                WriteU32(ms, t.TotalSectors);
                WriteU32(ms, SectorSizeCode(t.SectorSize));
                WriteU32(ms, 0);               // reserved1
            }
            WriteU32(ms, 0);                   // session tail
        }
        return ms.ToArray();
    }

    private static uint SectorSizeCode(CdiSectorSize s) => s switch
    {
        CdiSectorSize.S2048 => 0,
        CdiSectorSize.S2336 => 1,
        CdiSectorSize.S2352 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
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

    /// <summary>
    /// Write-only passthrough that tallies bytes, so a streamed track can be
    /// checked against its declared length. Does not own the inner stream.
    /// </summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            inner.WriteByte(value);
            BytesWritten++;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
