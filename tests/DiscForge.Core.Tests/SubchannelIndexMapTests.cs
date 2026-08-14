// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// subq-map recovers the true per-track index layout from a captured subchannel. These tests synthesise a
/// Packed96 sidecar with CRC-correct Q frames (via <see cref="SubQ.Position"/>, the same emitter the RAW
/// pipeline uses) for a mixed-mode disc: a data track 1 with no pregap and an audio track 2 with a known
/// 150-sector (2-second) pregap. The mapper must recover INDEX 00, INDEX 01 and the pregap length exactly —
/// which is what makes a Redump-accurate cue possible without guessing a convention.
/// </summary>
public class SubchannelIndexMapTests
{
    // Absolute MSF of a program-area LBA (lead-in offset is 150 sectors = 2 seconds).
    private static Msf Abs(long lba)
    {
        long a = lba + 150;
        return new Msf((int)(a / 4500), (int)(a / 75 % 60), (int)(a % 75));
    }

    // One 96-byte Packed96 sector carrying the given Q frame (Q sits at bytes 12..23).
    private static void WriteSector(Span<byte> dst, QControl control, int track, int index, long lba)
    {
        byte[] q = SubQ.Position(control, track, index, new Msf(0, 0, 0), Abs(lba));
        q.CopyTo(dst.Slice(12, 12));
    }

    private static byte[] BuildMixedModeSub()
    {
        // track 1 (data): LBA 0..99, INDEX 01, no pregap.
        // track 2 (audio): LBA 100..249 INDEX 00 (pregap), LBA 250..349 INDEX 01. Pregap = 150 sectors.
        const int total = 350;
        var sub = new byte[total * 96];
        for (long lba = 0; lba < total; lba++)
        {
            var s = sub.AsSpan((int)lba * 96, 96);
            if (lba < 100) WriteSector(s, QControl.Data, track: 1, index: 1, lba);
            else if (lba < 250) WriteSector(s, QControl.None, track: 2, index: 0, lba);
            else WriteSector(s, QControl.None, track: 2, index: 1, lba);
        }
        return sub;
    }

    [Fact]
    public void Recovers_indexes_and_pregap_for_a_mixed_mode_disc()
    {
        var map = SubchannelIndexMapper.Parse(BuildMixedModeSub(), RawSubcodeForm.Packed96);

        Assert.Equal(2, map.Tracks.Count);
        Assert.Equal(350, map.ValidQFrames);   // every synthesised frame is CRC-valid

        var t1 = map.Tracks[0];
        Assert.Equal(1, t1.Track);
        Assert.True(t1.IsData);
        Assert.Null(t1.Index00Lba);
        Assert.Equal(0, t1.Index01Lba);
        Assert.Equal(0, t1.PregapSectors);

        var t2 = map.Tracks[1];
        Assert.Equal(2, t2.Track);
        Assert.False(t2.IsData);
        Assert.Equal(100, t2.Index00Lba);
        Assert.Equal(250, t2.Index01Lba);
        Assert.Equal(150, t2.PregapSectors);
    }

    [Fact]
    public void Auto_detects_the_packed96_form_when_none_is_given()
    {
        var map = SubchannelIndexMapper.Parse(BuildMixedModeSub());
        Assert.Equal(RawSubcodeForm.Packed96, map.Form);
        Assert.Equal(2, map.Tracks.Count);
        Assert.Equal(150, map.Tracks[1].PregapSectors);
    }

    [Fact]
    public void Ignores_frames_with_a_broken_crc()
    {
        var sub = BuildMixedModeSub();
        // Corrupt the Q of ten mid-track-1 sectors (LBA 50..59): flip a byte inside the CRC-covered region.
        // The track's first body sector (LBA 0) stays valid, so INDEX 01 is still pinned to 0.
        for (int i = 50; i < 60; i++) sub[i * 96 + 13] ^= 0xFF;

        var map = SubchannelIndexMapper.Parse(sub, RawSubcodeForm.Packed96);
        Assert.Equal(340, map.ValidQFrames);          // the ten corrupted frames are rejected
        Assert.Equal(2, map.Tracks.Count);             // track 1 still recovered from its other frames
        Assert.Equal(0, map.Tracks[0].Index01Lba);
    }
}
