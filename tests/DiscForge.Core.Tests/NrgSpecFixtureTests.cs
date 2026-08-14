// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Nrg;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// A Nero NRG v2 (NER5) image hand-assembled to the public format layout —
/// deliberately NOT produced by <see cref="NrgWriter"/>. The existing NRG tests
/// prove DiscForge's writer and reader agree; this proves the <b>reader</b> reads
/// the documented byte offsets (footer → chunk chain → DAOX entries → CUEX LBAs)
/// independently, so a wrong offset shared by writer and reader would surface here
/// instead of round-tripping silently.
/// </summary>
public class NrgSpecFixtureTests
{
    private const int DaoxHeader = 22, DaoxEntry = 42;

    private static void U16Be(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v);
    private static void U32Be(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
    private static void U64Be(byte[] b, int at, ulong v) => BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(at), v);

    // Build a two-track NER5 image by hand: a data region up front, then a CUEX and
    // a DAOX chunk, an END! marker, and the "NER5" + 64-bit-offset footer.
    private static byte[] BuildNer5()
    {
        // Track 1: Mode1/2048, LBA 0, 4 sectors. Track 2: Audio/2352, LBA 4, 3 sectors.
        const int t1Size = 2048, t1Sectors = 4;
        const int t2Size = 2352, t2Sectors = 3;
        long t1Start = 0, t1End = t1Start + (long)t1Size * t1Sectors;   // 0 .. 8192
        long t2Start = t1End, t2End = t2Start + (long)t2Size * t2Sectors; // 8192 .. 15248

        var data = new byte[t2End];
        data[t1Start] = 0xA1;   // marker at track 1's data
        data[t2Start] = 0xB2;   // marker at track 2's data

        // CUEX payload: 8-byte entries [ctrl][track][index][0][i32 BE lba], index 1.
        var cuex = new byte[16];
        cuex[1] = 1; cuex[2] = 1; U32Be(cuex, 4, 0);   // track 1, lba 0
        cuex[9] = 2; cuex[10] = 1; U32Be(cuex, 12, 4); // track 2, lba 4

        // DAOX payload: 22-byte header (first/last track at +20/+21) then 42-byte entries.
        var daox = new byte[DaoxHeader + 2 * DaoxEntry];
        daox[20] = 1;   // first track
        daox[21] = 2;   // last track
        WriteEntry(daox, DaoxHeader + 0 * DaoxEntry, (ushort)t1Size, 0x00, (ulong)t1Start, (ulong)t1End);
        WriteEntry(daox, DaoxHeader + 1 * DaoxEntry, (ushort)t2Size, 0x07, (ulong)t2Start, (ulong)t2End);

        using var ms = new MemoryStream();
        ms.Write(data);
        long chunkOffset = ms.Position;
        WriteChunk(ms, "CUEX", cuex);
        WriteChunk(ms, "DAOX", daox);
        WriteChunk(ms, "END!", Array.Empty<byte>());
        // Footer: "NER5" + 64-bit big-endian offset to the first chunk.
        var footer = new byte[12];
        System.Text.Encoding.ASCII.GetBytes("NER5").CopyTo(footer, 0);
        U64Be(footer, 4, (ulong)chunkOffset);
        ms.Write(footer);
        return ms.ToArray();
    }

    private static void WriteEntry(byte[] daox, int at, ushort sectorSize, byte mode, ulong index1, ulong end)
    {
        // [0..11] ISRC, [12..13] sector size, [14] mode, [15..25] pregap, [26..33] index1, [34..41] end.
        U16Be(daox, at + 12, sectorSize);
        daox[at + 14] = mode;
        U64Be(daox, at + 26, index1);
        U64Be(daox, at + 34, end);
    }

    private static void WriteChunk(Stream s, string tag, byte[] payload)
    {
        s.Write(System.Text.Encoding.ASCII.GetBytes(tag));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)payload.Length);
        s.Write(size);
        s.Write(payload);
    }

    [Fact]
    public void A_hand_assembled_ner5_reads_its_tracks_from_the_documented_offsets()
    {
        var nrg = BuildNer5();
        using var ms = new MemoryStream(nrg);

        Assert.True(NrgParser.IsNrg(ms));
        var img = NrgParser.Parse(ms);

        Assert.True(img.IsV2);
        Assert.Equal(2, img.Tracks.Count);

        var t1 = img.Tracks[0];
        Assert.Equal(NrgTrackMode.Mode1, t1.Mode);
        Assert.Equal(2048, t1.SectorSize);
        Assert.Equal(0, t1.StartLba);
        Assert.Equal(4u, t1.LengthSectors);
        Assert.Equal(0xA1, nrg[t1.DataOffset]);

        var t2 = img.Tracks[1];
        Assert.Equal(NrgTrackMode.Audio, t2.Mode);
        Assert.Equal(2352, t2.SectorSize);
        Assert.Equal(4, t2.StartLba);          // from the CUEX table
        Assert.Equal(3u, t2.LengthSectors);
        Assert.Equal(0xB2, nrg[t2.DataOffset]);
    }
}
