// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Media;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// A PlayStation TOD animation stream hand-assembled to its documented layout —
/// not produced by <see cref="Tod.Write"/>. The existing TOD test is a write→parse
/// round trip (self-consistent); this pins the <b>parser</b> to the real byte
/// offsets (file header, frame header in 4-byte words, packet type/flag nibbles and
/// data-word count) so a shared writer+parser offset bug would surface here.
/// </summary>
public class TodSpecFixtureTests
{
    private static void U16(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
    private static void U32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);

    [Fact]
    public void A_hand_assembled_tod_parses_from_the_documented_offsets()
    {
        // File header (8): version=1, resolution=1, frameCount=1.
        // Frame header (8): lengthWords=4 (16 bytes / 4), packetCount=1, frameNumber=7.
        // Packet (4): objectId=5, typeFlag=(type 2 << 4)|(flag 3)=0x23, dataWords=1 → 4 payload bytes.
        var buf = new byte[24];
        U16(buf, 0, 1);        // version
        U16(buf, 2, 1);        // resolution
        U32(buf, 4, 1);        // frame count

        U16(buf, 8, 4);        // frame length in 4-byte words (header + packet + data)
        U16(buf, 10, 1);       // packet count
        U32(buf, 12, 7);       // frame number

        U16(buf, 16, 5);       // object id
        buf[18] = 0x23;        // type 2, flag 3
        buf[19] = 1;           // data words -> 4 bytes
        buf[20] = 0xDE; buf[21] = 0xAD; buf[22] = 0xBE; buf[23] = 0xEF;   // payload

        var tod = Tod.Parse(buf);

        Assert.Equal(1, tod.Version);
        Assert.Equal(1, tod.Resolution);
        var frame = Assert.Single(tod.Frames);
        Assert.Equal(7, frame.FrameNumber);

        var packet = Assert.Single(frame.Packets);
        Assert.Equal(5, packet.ObjectId);
        Assert.Equal(2, packet.Type);
        Assert.Equal(3, packet.Flag);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, packet.Data);
    }
}
