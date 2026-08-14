// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

public class XaStreamMapTests
{
    private static byte[] Raw(int file, int ch, XaSubmode sm, byte coding = 0x01)
    {
        var s = new byte[2352];
        s[0] = 0x00; for (int i = 1; i <= 10; i++) s[i] = 0xFF; s[11] = 0x00;   // sync
        s[15] = 0x02;                                                          // Mode 2
        s[16] = (byte)file; s[17] = (byte)ch; s[18] = (byte)sm; s[19] = coding;
        s[20] = (byte)file; s[21] = (byte)ch; s[22] = (byte)sm; s[23] = coding;
        return s;
    }

    private static byte[] Concat(IEnumerable<byte[]> sectors)
    {
        var list = sectors.ToList();
        var img = new byte[list.Sum(x => x.Length)];
        int o = 0;
        foreach (var s in list) { s.CopyTo(img, o); o += s.Length; }
        return img;
    }

    [Fact]
    public void A_non_xa_image_is_recognised_as_such()
    {
        var img = new byte[2352 * 4];   // all zero — no sync, no Mode 2
        var r = XaStreamMap.Analyze(img);
        Assert.False(r.IsXa);
        Assert.Contains("No CD-ROM XA", r.Summary());
    }

    [Fact]
    public void A_single_audio_stream_is_mapped_with_its_coding()
    {
        var sectors = Enumerable.Range(0, 8)
            .Select(_ => Raw(0, 0, XaSubmode.Audio | XaSubmode.Form2 | XaSubmode.RealTime, coding: 0x01));
        var r = XaStreamMap.Analyze(Concat(sectors));

        Assert.True(r.IsXa);
        Assert.Equal(8, r.Mode2Sectors);
        Assert.Equal(8, r.Form2Sectors);
        Assert.Equal(8, r.AudioSectors);
        var ch = Assert.Single(r.Channels);
        Assert.Equal("audio", ch.Kind);
        Assert.NotNull(ch.Audio);
        Assert.Equal(37800, ch.Audio!.Value.SampleRate);
        Assert.True(ch.Audio.Value.Stereo);
    }

    [Fact]
    public void Interleaved_audio_and_video_form_two_streams_with_many_switches()
    {
        var sectors = new List<byte[]>();
        for (int i = 0; i < 10; i++)
        {
            sectors.Add(Raw(0, 1, XaSubmode.Video | XaSubmode.RealTime));   // video on channel 1
            sectors.Add(Raw(0, 0, XaSubmode.Audio | XaSubmode.Form2 | XaSubmode.RealTime)); // audio on channel 0
        }
        var r = XaStreamMap.Analyze(Concat(sectors));

        Assert.Equal(2, r.Channels.Count);
        Assert.Equal(10, r.VideoSectors);
        Assert.Equal(10, r.AudioSectors);
        Assert.True(r.InterleaveSwitches >= 18);   // switches almost every sector
    }

    [Fact]
    public void Records_and_eof_are_counted()
    {
        var sectors = new List<byte[]>
        {
            Raw(0, 0, XaSubmode.Data),
            Raw(0, 0, XaSubmode.Data | XaSubmode.EndOfRecord),
            Raw(0, 0, XaSubmode.Data | XaSubmode.EndOfRecord | XaSubmode.EndOfFile),
        };
        var r = XaStreamMap.Analyze(Concat(sectors));
        var ch = Assert.Single(r.Channels);
        Assert.Equal(2, ch.Records);
        Assert.True(ch.EndsFile);
        Assert.Equal("data", ch.Kind);
    }

    [Fact]
    public void Form1_and_form2_sectors_are_split()
    {
        var sectors = new List<byte[]>
        {
            Raw(0, 0, XaSubmode.Data),                       // Form 1
            Raw(0, 0, XaSubmode.Data),                       // Form 1
            Raw(0, 0, XaSubmode.Audio | XaSubmode.Form2),    // Form 2
        };
        var r = XaStreamMap.Analyze(Concat(sectors));
        Assert.Equal(2, r.Form1Sectors);
        Assert.Equal(1, r.Form2Sectors);
    }

    [Fact]
    public void Mode1_sectors_are_ignored_in_a_mixed_image()
    {
        var mode1 = new byte[2352];
        mode1[0] = 0x00; for (int i = 1; i <= 10; i++) mode1[i] = 0xFF; mode1[11] = 0x00;
        mode1[15] = 0x01;   // Mode 1 — no XA subheader
        var img = Concat(new[] { mode1, Raw(0, 0, XaSubmode.Audio | XaSubmode.Form2) });

        var r = XaStreamMap.Analyze(img);
        Assert.Equal(1, r.Mode2Sectors);   // the Mode 1 sector is skipped
        Assert.Single(r.Channels);
    }

    [Fact]
    public void The_mode2_2336_layout_needs_no_sync()
    {
        // 2336-byte Mode 2 sectors: subheader at offset 0, no sync/header.
        byte[] Sec(int file, int ch, XaSubmode sm)
        {
            var s = new byte[2336];
            s[0] = (byte)file; s[1] = (byte)ch; s[2] = (byte)sm; s[3] = 0x01;
            s[4] = (byte)file; s[5] = (byte)ch; s[6] = (byte)sm; s[7] = 0x01;
            return s;
        }
        var img = Concat(new[]
        {
            Sec(1, 0, XaSubmode.Audio | XaSubmode.Form2),
            Sec(1, 0, XaSubmode.Audio | XaSubmode.Form2),
        });
        var r = XaStreamMap.Analyze(img, XaExtract.SectorLayout.Mode2_2336);
        Assert.Equal(2, r.Mode2Sectors);
        Assert.Equal(2, r.AudioSectors);
        Assert.Equal(1, r.Channels[0].File);
    }
}
