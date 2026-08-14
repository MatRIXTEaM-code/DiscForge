// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Floppy;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// SuperCard Pro (SCP) flux reader — proven by round-trip against a spec-shaped image with a
/// real checksum: header fields, the track offset table, per-revolution metadata, RPM
/// inference, and the 0x0000 flux-overflow decoding. SCP carries its own checksum, so the
/// round-trip is a genuine proof of the parse.
/// </summary>
public class ScpReaderTests
{
    // One track, one revolution at 300 RPM (200 ms rev = 8,000,000 ticks at 25 ns),
    // with a flux stream that exercises the overflow (0x0000) convention.
    private static byte[] BuildScp()
    {
        const int tableStart = 0x10;
        const int tdhOffset = tableStart + ScpReader.MaxTracks * 4;   // 0x2B0
        const int fluxDataOffset = 16;                               // from TDH start

        var flux = new byte[] { 0x00, 0xDA, 0x00, 0x00, 0x00, 0x64, 0x00, 0xC8 };  // 218, overflow, 100, 200
        int total = tdhOffset + 4 + 12 + flux.Length;
        var b = new byte[total];

        // Header.
        b[0] = (byte)'S'; b[1] = (byte)'C'; b[2] = (byte)'P';
        b[3] = 0x22;            // version 2.2
        b[4] = 0x00;            // disk type
        b[5] = 1;               // revolutions
        b[6] = 0; b[7] = 0;     // start/end track
        b[8] = 0x03;            // flags: index + 96 TPI
        b[9] = 0;               // 16-bit cells
        b[10] = 0;              // both heads
        b[11] = 0;              // resolution 0 → 25 ns

        // Track offset table: track 0 → tdhOffset.
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(tableStart, 4), tdhOffset);

        // Track Data Header + one revolution.
        b[tdhOffset] = (byte)'T'; b[tdhOffset + 1] = (byte)'R'; b[tdhOffset + 2] = (byte)'K';
        b[tdhOffset + 3] = 0;    // track number
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(tdhOffset + 4, 4), 8_000_000);   // index duration ticks
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(tdhOffset + 8, 4), 4);           // flux count (words)
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(tdhOffset + 12, 4), fluxDataOffset);
        flux.CopyTo(b, tdhOffset + 16);

        // Checksum: 32-bit sum of bytes from 0x10 to EOF.
        uint sum = 0;
        for (int i = 0x10; i < b.Length; i++) sum = unchecked(sum + b[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x0C, 4), sum);
        return b;
    }

    [Fact]
    public void Reads_the_header_and_validates_the_checksum()
    {
        var img = ScpReader.Parse(BuildScp());

        Assert.True(img.ChecksumValid);
        Assert.Equal(2, img.Header.VersionMajor);
        Assert.Equal(2, img.Header.VersionMinor);
        Assert.Equal(1, img.Header.Revolutions);
        Assert.Equal(25, img.Header.TickNs);
        Assert.True(img.Header.Indexed);
        Assert.True(img.Header.Is96Tpi);
    }

    [Fact]
    public void Reads_the_track_and_revolution_table()
    {
        var img = ScpReader.Parse(BuildScp());

        Assert.Single(img.Tracks);
        var t = img.Tracks[0];
        Assert.Equal(0, t.TrackNumber);
        Assert.Single(t.Revolutions);
        Assert.Equal(8_000_000u, t.Revolutions[0].IndexDurationTicks);
        Assert.Equal(4u, t.Revolutions[0].FluxCount);
    }

    [Fact]
    public void Infers_300_rpm_from_the_index_duration()
    {
        var img = ScpReader.Parse(BuildScp());
        Assert.NotNull(img.Rpm);
        Assert.Equal(300.0, img.Rpm!.Value, 1);
    }

    [Fact]
    public void Decodes_flux_intervals_honouring_the_overflow_word()
    {
        var raw = BuildScp();
        var img = ScpReader.Parse(raw);
        var ns = ScpReader.ReadFluxNs(raw, img, img.Tracks[0], 0);

        // 218 → 5450 ns; overflow(0x10000)+100 → 65636×25 = 1,640,900 ns; 200 → 5000 ns.
        Assert.Equal(new long[] { 5450, 1_640_900, 5000 }, ns);
    }

    [Fact]
    public void A_corrupted_body_fails_the_checksum()
    {
        var raw = BuildScp();
        raw[^1] ^= 0xFF;
        Assert.False(ScpReader.Parse(raw).ChecksumValid);
    }
}
