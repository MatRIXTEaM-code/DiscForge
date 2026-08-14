// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Audio;
using Xunit;

namespace DiscForge.Core.Tests;

public class HdcdTests
{
    // A 32-bit window value that satisfies the self-checking Type-B control code.
    private static uint FindTypeB()
    {
        for (uint cmd = 0; cmd < 256; cmd++)
            for (uint chk = 0; chk < 256; chk++)
            {
                uint w = 0xA0060000u | (cmd << 8) | chk;
                if (Hdcd.IsTypeB(w)) return w;
            }
        throw new InvalidOperationException("no valid Type-B window found");
    }

    // Emit the 32 LSBs of 'target' MSB-first so a w=(w<<1)|lsb window ends at 'target'.
    private static short[] EmbedMono(uint target, int lead)
    {
        var list = new List<short>();
        for (int i = 0; i < lead; i++) list.Add(0);
        for (int bit = 31; bit >= 0; bit--) list.Add((short)((target >> bit) & 1));
        for (int i = 0; i < 200; i++) list.Add(0);
        return list.ToArray();
    }

    [Fact]
    public void Detects_an_embedded_type_b_packet()
    {
        var r = Hdcd.Scan(EmbedMono(FindTypeB(), 100), 1);
        Assert.True(r.PacketsTypeB >= 1);
        Assert.True(r.Detected);
    }

    [Fact]
    public void Follows_the_correct_channel_in_stereo()
    {
        uint target = FindTypeB();
        var mono = EmbedMono(target, 50);
        var stereo = new List<short>();
        foreach (var x in mono) { stereo.Add(0); stereo.Add(x); }   // code only in the right channel
        var r = Hdcd.Scan(stereo.ToArray(), 2);
        Assert.True(r.Detected);
        Assert.True(r.PacketsTypeB >= 1);
    }

    [Fact]
    public void Silence_and_random_audio_are_not_flagged()
    {
        Assert.False(Hdcd.Scan(new short[200_000], 2).Detected);

        var rnd = new Random(7);
        var noise = new short[2_000_000];
        for (int i = 0; i < noise.Length; i++) noise[i] = (short)rnd.Next(-30000, 30000);
        var r = Hdcd.Scan(noise, 2);
        // Type-A fires ~1/2048 by chance; the detector must NOT treat that noise floor as HDCD.
        Assert.Equal(0, r.PacketsTypeB);
        Assert.False(r.Detected);
    }

    [Fact]
    public void A_periodic_type_b_stream_is_detected_with_many_packets()
    {
        uint target = FindTypeB();
        var rnd = new Random(3);
        var big = new List<short>();
        for (int p = 0; p < 50; p++)
        {
            for (int i = 0; i < 5000; i++) big.Add((short)rnd.Next(-30000, 30000));
            big.AddRange(EmbedMono(target, 0));
        }
        var r = Hdcd.Scan(big.ToArray(), 1);
        Assert.True(r.Detected);
        Assert.True(r.PacketsTypeB >= 40);
    }
}
