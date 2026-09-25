// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.PlayStation;

public sealed class SeqFormatException(string message) : Exception(message);

/// <summary>
/// A parsed PlayStation SEQ sequence — the score half of a VAB+SEQ pair. Header
/// fields plus a count of the MIDI-like events in the body. It is not fully
/// sequenced; the body is walked only far enough to count events and find the end.
/// </summary>
public sealed class SeqFile
{
    public required int Version { get; init; }
    /// <summary>Ticks per quarter note (resolution).</summary>
    public required int Ppqn { get; init; }
    /// <summary>Initial tempo, the raw 24-bit value from the header.</summary>
    public required int Tempo { get; init; }
    public required int EventCount { get; init; }
}

/// <summary>
/// Reads a PlayStation SEQ file.
///
/// Clean-room, from the public SEQ description. Header fields are BIG-ENDIAN.
///   0x00 4  magic — bytes "pQES" (0x70 0x51 0x45 0x53); "SEQp" also accepted
///   0x04 4  version
///   0x0A 2  resolution / ppqn
///   0x0C 3  tempo (24-bit)
///   0x0F 2  rhythm (time signature)
///   0x11    body: MIDI-like delta-time + events, terminated by meta 0xFF 0x2F 0x00
///
/// The body uses variable-length delta times and the standard MIDI event/running-
/// status encoding, with 0xFF meta events (end-of-track = 0xFF 0x2F 0x00).
/// </summary>
public static class Seq
{
    private const int BodyStart = 0x11;

    public static bool IsSeq(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        bool pQES = data[0] == 0x70 && data[1] == 0x51 && data[2] == 0x45 && data[3] == 0x53;
        bool sEQp = data[0] == 0x53 && data[1] == 0x45 && data[2] == 0x51 && data[3] == 0x70;
        return pQES || sEQp;
    }

    public static SeqFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < BodyStart || !IsSeq(data))
            throw new SeqFormatException("Missing the SEQ signature — not a SEQ sequence.");

        var span = data.AsSpan();
        int version = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x04));
        int ppqn = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(0x0A));
        int tempo = (data[0x0C] << 16) | (data[0x0D] << 8) | data[0x0E];

        int eventCount = CountEvents(data, BodyStart);

        return new SeqFile
        {
            Version = version,
            Ppqn = ppqn,
            Tempo = tempo,
            EventCount = eventCount,
        };
    }

    private static int CountEvents(byte[] data, int start)
    {
        int pos = start;
        int end = data.Length;
        int running = 0;
        int events = 0;

        while (pos < end)
        {
            ReadVlq(data, ref pos, end);                 // delta time
            if (pos >= end) break;

            int status = data[pos];
            if (status >= 0x80)
            {
                pos++;
                if (status < 0xF0) running = status;      // system messages don't set running status
            }
            else
            {
                status = running;                          // running status: current byte is data
                if (status == 0) break;                    // no status yet — malformed, stop
            }

            if (status == 0xFF)
            {
                if (pos >= end) break;
                int type = data[pos++];
                int len = ReadVlq(data, ref pos, end);
                events++;
                if (type == 0x2F) break;                   // end of track
                pos += len;
            }
            else if (status == 0xF0 || status == 0xF7)
            {
                int len = ReadVlq(data, ref pos, end);
                pos += len;
                events++;
            }
            else
            {
                int hi = status & 0xF0;
                int dataBytes = (hi == 0xC0 || hi == 0xD0) ? 1 : 2;
                pos += dataBytes;
                events++;
            }
        }

        return events;
    }

    private static int ReadVlq(byte[] data, ref int pos, int end)
    {
        int value = 0;
        while (pos < end)
        {
            byte c = data[pos++];
            value = (value << 7) | (c & 0x7F);
            if ((c & 0x80) == 0) break;
        }
        return value;
    }
}
