// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.Media;

public sealed class TodFormatException(string message) : Exception(message);

/// <summary>
/// Reads the PlayStation TOD animation format — the streamed keyframe animation
/// "TOD Info" / "TOD RIP" inspect and extract. TOD is a plain, unencrypted
/// container: a small file header, then a sequence of frames, each a list of
/// packets that address an object and carry a coordinate/attribute update.
///
/// Clean-room, from the public Sony TOD description:
///   Header (8 bytes): u16 version, u16 resolution (v-syncs per frame), u32 frames.
///   Frame: u16 length-in-words (incl. this header), u16 packet count, u32 frame#.
///   Packet: u16 object id, u8 type/flag (type high nibble, flag low nibble),
///           u8 data length in words, then that many 4-byte words.
///
/// Honest scope — as with the NRG reader before a real sample: TOD support is
/// validated by round trip (this reader and its companion writer agree on the
/// container) and follows the public structure description; it has not yet been
/// checked against a TOD file produced by the Sony tools. A field a real file
/// reads differently is a bug to fix against the sample, not a redesign. The
/// per-packet payload (matrix/RST/light data) is carried as raw bytes, not
/// interpreted. Nothing here is protection-related.
/// </summary>
public static class Tod
{
    public sealed record TodPacket
    {
        public required int ObjectId { get; init; }
        public required int Type { get; init; }
        public required int Flag { get; init; }
        public required byte[] Data { get; init; }
    }

    public sealed record TodFrame
    {
        public required int FrameNumber { get; init; }
        public required IReadOnlyList<TodPacket> Packets { get; init; }
    }

    public sealed record TodFile
    {
        public int Version { get; init; } = 1;
        public int Resolution { get; init; } = 1;
        public required IReadOnlyList<TodFrame> Frames { get; init; }
    }

    private const int HeaderSize = 8;

    public static TodFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize)
            throw new TodFormatException("File is too short to hold a TOD header.");

        int version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0));
        int resolution = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        long frameCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (frameCount > 1_000_000)
            throw new TodFormatException($"Implausible frame count {frameCount} — file is likely not a TOD.");

        var frames = new List<TodFrame>((int)Math.Min(frameCount, 1024));
        int pos = HeaderSize;
        for (long f = 0; f < frameCount; f++)
        {
            if (pos + 8 > data.Length) break;   // truncated tail: stop cleanly
            int frameStart = pos;
            int lengthWords = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            int packetCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
            int frameNumber = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            int p = pos + 8;

            var packets = new List<TodPacket>(packetCount);
            for (int k = 0; k < packetCount && p + 4 <= data.Length; k++)
            {
                int objectId = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));
                byte typeFlag = data[p + 2];
                int dataWords = data[p + 3];
                int dataBytes = dataWords * 4;
                p += 4;
                var payload = new byte[Math.Max(0, Math.Min(dataBytes, data.Length - p))];
                Array.Copy(data, p, payload, 0, payload.Length);
                p += dataBytes;

                packets.Add(new TodPacket
                {
                    ObjectId = objectId,
                    Type = (typeFlag >> 4) & 0x0F,
                    Flag = typeFlag & 0x0F,
                    Data = payload,
                });
            }

            frames.Add(new TodFrame { FrameNumber = frameNumber, Packets = packets });

            // Advance by the frame's declared length so a partly-parsed frame can't
            // desync the stream; fall back to the packet cursor if the length is 0.
            int next = lengthWords > 0 ? frameStart + lengthWords * 4 : p;
            pos = next > pos ? next : p;
        }

        return new TodFile { Version = version, Resolution = resolution, Frames = frames };
    }

    /// <summary>Serialise a TOD (used to validate the reader by round trip).</summary>
    public static byte[] Write(TodFile tod)
    {
        ArgumentNullException.ThrowIfNull(tod);
        using var ms = new MemoryStream();
        Span<byte> h = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(h[..2], (ushort)tod.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(2, 2), (ushort)tod.Resolution);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(4, 4), (uint)tod.Frames.Count);
        ms.Write(h);

        var fh = new byte[8];
        var ph = new byte[4];
        foreach (var frame in tod.Frames)
        {
            int lengthWords = 2;   // the 8-byte frame header
            foreach (var pk in frame.Packets) lengthWords += 1 + pk.Data.Length / 4;

            BinaryPrimitives.WriteUInt16LittleEndian(fh.AsSpan(0, 2), (ushort)lengthWords);
            BinaryPrimitives.WriteUInt16LittleEndian(fh.AsSpan(2, 2), (ushort)frame.Packets.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(fh.AsSpan(4, 4), (uint)frame.FrameNumber);
            ms.Write(fh);

            foreach (var pk in frame.Packets)
            {
                if (pk.Data.Length % 4 != 0)
                    throw new TodFormatException("Packet data must be a whole number of 4-byte words.");
                BinaryPrimitives.WriteUInt16LittleEndian(ph.AsSpan(0, 2), (ushort)pk.ObjectId);
                ph[2] = (byte)(((pk.Type & 0x0F) << 4) | (pk.Flag & 0x0F));
                ph[3] = (byte)(pk.Data.Length / 4);
                ms.Write(ph);
                ms.Write(pk.Data);
            }
        }
        return ms.ToArray();
    }
}
