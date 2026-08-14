// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.PlayStation;

/// <summary>One reassembled STR video frame: its MDEC bitstream and dimensions.</summary>
public sealed class StrFrame
{
    public required int FrameNumber { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>The concatenated MDEC bitstream for the frame, trimmed to the
    /// declared byte length. Pixel decode (MDEC) is deferred — see docs/PSX_MEDIA.md.</summary>
    public required byte[] Bitstream { get; init; }
    /// <summary>True if every declared chunk of the frame was present.</summary>
    public required bool Complete { get; init; }
}

/// <summary>Result of demultiplexing a .str: reassembled video frames and the
/// raw XA-audio sectors passed through untouched.</summary>
public sealed class StrDemuxResult
{
    public required IReadOnlyList<StrFrame> Frames { get; init; }
    /// <summary>Raw bytes of each XA-audio sector, in file order.</summary>
    public required IReadOnlyList<byte[]> AudioSectors { get; init; }
    public required int VideoSectorCount { get; init; }
    public required int TotalSectorCount { get; init; }
}

/// <summary>
/// Demultiplexes a PlayStation .str (CD Mode 2 Form 1 sectors) into video frames
/// and XA audio.
///
/// Clean-room, from the public STR description. Each 2352-byte sector carries an
/// 8-byte XA subheader (file, channel, submode, coding) before its 2048-byte user
/// data; the submode bits split video (0x02) from audio (0x04). A .str may also be
/// stored as bare 2048-byte user-data sectors (all video).
///
/// A video sector's user data begins with the 32-byte STR frame header
/// (LITTLE-ENDIAN — the magic 0x0160 is on disk as bytes 0x60 0x01):
///   0x00 u16 magic 0x0160
///   0x02 u16 chunk index (within the frame)
///   0x04 u16 chunks in this frame
///   0x06 u32 frame number
///   0x0A u32 frame data bytes (total, across all chunks)
///   0x0E u16 width
///   0x10 u16 height
///   0x20     MDEC bitstream chunk (up to 2016 bytes)
/// A frame's bitstream is the concatenation of its chunks in index order, trimmed
/// to the declared byte length.
/// </summary>
public static class StrDemuxer
{
    public const int StrMagic = 0x0160;
    public const int FrameHeaderSize = 0x20;

    private const int SubmodeVideo = 0x02;
    private const int SubmodeAudio = 0x04;

    public enum Layout
    {
        /// <summary>Full 2352-byte raw sectors (sync + header + subheader + 2048 user).</summary>
        Raw2352,
        /// <summary>Bare 2048-byte Mode 2 Form 1 user-data sectors.</summary>
        UserData2048,
    }

    private sealed class Pending
    {
        public int FrameNumber;
        public int Width;
        public int Height;
        public int DeclaredBytes;
        public int ChunkCount;
        public byte[]?[] Chunks = System.Array.Empty<byte[]?>();
        public int Present;
    }

    public static StrDemuxResult Demux(Stream stream, Layout layout)
    {
        ArgumentNullException.ThrowIfNull(stream);

        int sectorSize = layout == Layout.Raw2352 ? 2352 : 2048;
        int subheaderOffset = layout == Layout.Raw2352 ? 16 : -1;
        int userOffset = layout == Layout.Raw2352 ? 24 : 0;
        const int userLen = 2048;

        var frames = new List<StrFrame>();
        var audio = new List<byte[]>();
        var pending = new Dictionary<int, Pending>();
        var order = new List<int>();                // frame numbers in first-seen order
        int videoSectors = 0, totalSectors = 0;

        var sector = new byte[sectorSize];
        while (ReadFull(stream, sector))
        {
            totalSectors++;

            if (layout == Layout.Raw2352)
            {
                int submode = sector[subheaderOffset + 2];
                if ((submode & SubmodeAudio) != 0 && (submode & SubmodeVideo) == 0)
                {
                    audio.Add((byte[])sector.Clone());
                    continue;
                }
            }

            var user = sector.AsSpan(userOffset, userLen);
            int magic = BinaryPrimitives.ReadUInt16LittleEndian(user);
            if (magic != StrMagic)
                continue;                              // not a video-bitstream sector

            videoSectors++;
            int chunkIndex = BinaryPrimitives.ReadUInt16LittleEndian(user.Slice(0x02));
            int chunkCount = BinaryPrimitives.ReadUInt16LittleEndian(user.Slice(0x04));
            int frameNumber = (int)BinaryPrimitives.ReadUInt32LittleEndian(user.Slice(0x06));
            int declaredBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(user.Slice(0x0A));
            int width = BinaryPrimitives.ReadUInt16LittleEndian(user.Slice(0x0E));
            int height = BinaryPrimitives.ReadUInt16LittleEndian(user.Slice(0x10));

            if (chunkCount <= 0 || chunkIndex < 0 || chunkIndex >= chunkCount)
                continue;                              // corrupt header, skip the sector

            if (!pending.TryGetValue(frameNumber, out var p))
            {
                p = new Pending
                {
                    FrameNumber = frameNumber,
                    Width = width,
                    Height = height,
                    DeclaredBytes = declaredBytes,
                    ChunkCount = chunkCount,
                    Chunks = new byte[]?[chunkCount],
                };
                pending[frameNumber] = p;
                order.Add(frameNumber);
            }

            var payload = user.Slice(FrameHeaderSize, userLen - FrameHeaderSize).ToArray();
            if (p.Chunks[chunkIndex] is null)
            {
                p.Chunks[chunkIndex] = payload;
                p.Present++;
            }

            if (p.Present == p.ChunkCount)
            {
                frames.Add(Finalize(p));
                pending.Remove(frameNumber);
                order.Remove(frameNumber);
            }
        }

        // Emit any frames that never completed, in first-seen order (best effort).
        foreach (int fn in order)
            frames.Add(Finalize(pending[fn]));

        return new StrDemuxResult
        {
            Frames = frames,
            AudioSectors = audio,
            VideoSectorCount = videoSectors,
            TotalSectorCount = totalSectors,
        };
    }

    private static StrFrame Finalize(Pending p)
    {
        using var ms = new MemoryStream();
        for (int i = 0; i < p.ChunkCount; i++)
            if (p.Chunks[i] is byte[] c)
                ms.Write(c, 0, c.Length);

        byte[] full = ms.ToArray();
        int len = p.DeclaredBytes > 0 && p.DeclaredBytes <= full.Length ? p.DeclaredBytes : full.Length;
        var bitstream = new byte[len];
        System.Array.Copy(full, bitstream, len);

        return new StrFrame
        {
            FrameNumber = p.FrameNumber,
            Width = p.Width,
            Height = p.Height,
            Bitstream = bitstream,
            Complete = p.Present == p.ChunkCount,
        };
    }

    private static bool ReadFull(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) return false;                  // clean EOF (partial trailing sector dropped)
            total += n;
        }
        return true;
    }
}
