// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.Mpeg;

/// <summary>The kind of elementary stream a PES stream_id maps to.</summary>
public enum MpegStreamKind
{
    Video,
    Audio,
    /// <summary>private_stream_1 (0xBD) — on a DVD this carries AC3/DTS/LPCM audio
    /// and subpicture streams, keyed by a sub-stream id in the first payload byte.</summary>
    Private1,
    /// <summary>private_stream_2 (0xBF) — DVD navigation (PCI/DSI) packs.</summary>
    Private2,
    Other,
}

/// <summary>One reassembled elementary stream: the concatenation of every PES
/// payload that shared a stream id (and, for private_stream_1, a sub-stream id).</summary>
public sealed class MpegElementaryStream
{
    public required byte StreamId { get; init; }
    /// <summary>The private_stream_1 sub-stream id (0x80=AC3, 0x88=DTS, 0xA0=LPCM,
    /// 0x20-0x3F=subpicture), or -1 when not applicable.</summary>
    public required int SubStreamId { get; init; }
    public required MpegStreamKind Kind { get; init; }
    public required byte[] Data { get; init; }
    public required int PacketCount { get; init; }

    /// <summary>A stable output filename, e.g. "video_e0.m2v", "audio_c0.mp2",
    /// "private_bd_80.ac3".</summary>
    public string SuggestedName()
    {
        string hex = StreamId.ToString("x2");
        return Kind switch
        {
            MpegStreamKind.Video => $"video_{hex}.m2v",
            MpegStreamKind.Audio => $"audio_{hex}.mp2",
            MpegStreamKind.Private1 => SubStreamId >= 0
                ? $"private_bd_{SubStreamId:x2}{Private1Extension()}"
                : "private_bd.bin",
            MpegStreamKind.Private2 => "nav_bf.bin",
            _ => $"stream_{hex}.bin",
        };
    }

    private string Private1Extension() => SubStreamId switch
    {
        >= 0x80 and <= 0x87 => ".ac3",
        >= 0x88 and <= 0x8F => ".dts",
        >= 0xA0 and <= 0xA7 => ".lpcm",
        >= 0x20 and <= 0x3F => ".sup",   // subpicture
        _ => ".bin",
    };
}

/// <summary>Aggregate result of demultiplexing an MPEG program stream.</summary>
public sealed class MpegPsDemuxResult
{
    public required IReadOnlyList<MpegElementaryStream> Streams { get; init; }
    public required int PackCount { get; init; }
    public required int PesPacketCount { get; init; }
    /// <summary>True if the stream looked like MPEG-2 (a program stream whose pack
    /// headers use the 14-byte MPEG-2 form) at least once.</summary>
    public required bool SawMpeg2 { get; init; }
}

/// <summary>
/// Demultiplexes an MPEG-1/MPEG-2 <b>program stream</b> — the container used by
/// VCD/SVCD <c>.mpg</c> and by DVD-Video <c>.VOB</c> files — into its elementary
/// video, audio and (DVD) private streams. It walks the classic pack / system-header
/// / PES structure and concatenates each stream's PES payloads.
///
/// Clean-room and unencrypted-only: this parses the public MPEG program-stream
/// syntax. It does NOT decrypt anything — a CSS-scrambled VOB's PES payloads are
/// still encrypted, so demuxing one yields scrambled elementary streams; DiscForge
/// deliberately does not descramble them. On unprotected VOBs (homemade discs,
/// already-decrypted content) and on VCD/SVCD MPEG it produces clean output.
///
/// Structure walked:
///   pack_start_code            0x000001BA  — MPEG-1: 12 bytes; MPEG-2: 14 + stuffing
///   system_header_start_code   0x000001BB  — 2-byte length, then that many bytes
///   PES packet   0x000001 &lt;stream_id&gt;   — 2-byte length, PES header, payload
///     stream_id 0xE0-0xEF video · 0xC0-0xDF audio · 0xBD private_1 · 0xBF private_2
///     · 0xBE padding (skipped)
/// </summary>
public static class MpegProgramStream
{
    /// <summary>Max bytes to buffer per elementary stream (guards a corrupt length
    /// field from allocating without bound). 2 GiB.</summary>
    private const long MaxPerStream = int.MaxValue;

    public static MpegPsDemuxResult Demux(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var data = ReadAll(input);
        return Demux(data);
    }

    public static MpegPsDemuxResult Demux(ReadOnlySpan<byte> s)
    {
        var outputs = new Dictionary<int, StreamAccumulator>();
        var order = new List<int>();
        int packs = 0, pes = 0;
        bool sawMpeg2 = false;

        int i = 0;
        int n = s.Length;
        while (i + 4 <= n)
        {
            // Find the next start code 00 00 01.
            if (!(s[i] == 0x00 && s[i + 1] == 0x00 && s[i + 2] == 0x01))
            {
                i++;
                continue;
            }

            byte code = s[i + 3];

            if (code == 0xBA)                    // pack header
            {
                packs++;
                i += 4;
                if (i >= n) break;
                if ((s[i] & 0xC0) == 0x40)       // MPEG-2 pack: 10 bytes + stuffing
                {
                    sawMpeg2 = true;
                    if (i + 10 > n) break;
                    int stuffing = s[i + 9] & 0x07;
                    i += 10 + stuffing;
                }
                else                              // MPEG-1 pack: 8 bytes
                {
                    i += 8;
                }
                continue;
            }

            if (code == 0xB9)                    // MPEG_program_end_code
            {
                i += 4;
                continue;
            }

            if (code == 0xBB || (code >= 0xBC && code <= 0xFF)) // system header or PES
            {
                if (i + 6 > n) break;
                int len = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(i + 4, 2));
                int payloadStart = i + 6;
                int payloadEnd = payloadStart + len;
                if (payloadEnd > n) payloadEnd = n;               // tolerate truncation

                if (code == 0xBB || code == 0xBE)                 // system header / padding: skip
                {
                    i = payloadEnd;
                    continue;
                }

                pes++;
                var pesBytes = s[payloadStart..payloadEnd];
                var (dataStart, kind) = ParsePesHeader(pesBytes, code);
                if (dataStart < 0) { i = payloadEnd; continue; }

                var payload = pesBytes[dataStart..];
                int key = code;
                int sub = -1;
                if (code == 0xBD && payload.Length >= 1)          // private_1: split by sub-stream
                {
                    sub = payload[0];
                    key = (code << 8) | sub;
                    // LPCM has a 7-byte sub-header, AC3/DTS a 4-byte one; keep the
                    // audio frames only (drop the sub-stream id + its header) so the
                    // output is a clean elementary stream.
                    int skip = sub is >= 0xA0 and <= 0xA7 ? 7 : 4;
                    payload = payload.Length >= skip ? payload[skip..] : ReadOnlySpan<byte>.Empty;
                }

                if (!outputs.TryGetValue(key, out var acc))
                {
                    acc = new StreamAccumulator(code, sub, kind);
                    outputs[key] = acc;
                    order.Add(key);
                }
                acc.Append(payload);
                i = payloadEnd;
                continue;
            }

            i += 4;   // some other start code (e.g. video sequence inside — shouldn't appear at PS level)
        }

        var streams = new List<MpegElementaryStream>(order.Count);
        foreach (int key in order)
            streams.Add(outputs[key].ToStream());

        return new MpegPsDemuxResult
        {
            Streams = streams,
            PackCount = packs,
            PesPacketCount = pes,
            SawMpeg2 = sawMpeg2,
        };
    }

    /// <summary>Return (payload offset within the PES packet, stream kind), or
    /// (-1, _) if the header is malformed. Handles both MPEG-1 and MPEG-2 PES.</summary>
    private static (int, MpegStreamKind) ParsePesHeader(ReadOnlySpan<byte> pes, byte streamId)
    {
        var kind = KindOf(streamId);
        // private_stream_2 (0xBF) has NO PES header — payload follows immediately.
        if (streamId == 0xBF) return (0, kind);

        int p = 0;
        if (pes.Length >= 1 && (pes[0] & 0xC0) == 0x80)
        {
            // MPEG-2 PES: flags(2) + header_data_length(1) + that many bytes.
            if (pes.Length < 3) return (-1, kind);
            int headerLen = pes[2];
            p = 3 + headerLen;
        }
        else
        {
            // MPEG-1 PES: optional 0xFF stuffing (<=16), optional STD buffer (2),
            // then PTS/DTS (0/5/10 bytes signalled by the leading bits).
            int stuff = 0;
            while (p < pes.Length && pes[p] == 0xFF && stuff < 16) { p++; stuff++; }
            if (p < pes.Length && (pes[p] & 0xC0) == 0x40) p += 2;          // STD buffer scale/size
            if (p >= pes.Length) return (-1, kind);
            int marker = pes[p] & 0xF0;
            if (marker == 0x20) p += 5;              // PTS only
            else if (marker == 0x30) p += 10;        // PTS + DTS
            else if (pes[p] == 0x0F) p += 1;         // no PTS/DTS
            else p += 1;                             // tolerate: assume 1-byte flag
        }
        if (p > pes.Length) return (-1, kind);
        return (p, kind);
    }

    private static MpegStreamKind KindOf(byte id) => id switch
    {
        >= 0xE0 and <= 0xEF => MpegStreamKind.Video,
        >= 0xC0 and <= 0xDF => MpegStreamKind.Audio,
        0xBD => MpegStreamKind.Private1,
        0xBF => MpegStreamKind.Private2,
        _ => MpegStreamKind.Other,
    };

    private sealed class StreamAccumulator(byte streamId, int sub, MpegStreamKind kind)
    {
        private readonly MemoryStream _buf = new();
        private int _packets;

        public void Append(ReadOnlySpan<byte> payload)
        {
            if (_buf.Length + payload.Length > MaxPerStream) return;
            _buf.Write(payload);
            _packets++;
        }

        public MpegElementaryStream ToStream() => new()
        {
            StreamId = streamId,
            SubStreamId = sub,
            Kind = kind,
            Data = _buf.ToArray(),
            PacketCount = _packets,
        };
    }

    private static byte[] ReadAll(Stream s)
    {
        if (s is MemoryStream ms) return ms.ToArray();
        using var buffer = new MemoryStream();
        s.CopyTo(buffer);
        return buffer.ToArray();
    }
}
