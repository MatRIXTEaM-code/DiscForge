// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Forensics;
using DiscForge.Core.Preservation;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The Disc MRI's two halves proven separately: the spiral geometry (does a
/// sector land where physics puts it, and does the inverse mapping find it
/// again?) and the evidence pipeline (classification, sidecar overlay, and
/// the worst-wins polar rendering — including the claim that a radial scratch
/// is VISIBLE as a radial streak).
/// </summary>
public class DiscMriTests
{
    // ---- spiral geometry ---------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1_000)]
    [InlineData(150_000)]
    [InlineData(359_845)]
    public void Locate_SectorAt_RoundTrips(long sector)
    {
        const long total = 360_000;
        var (r, theta) = DiscMri.Locate(sector);
        double angle = theta % (2 * Math.PI);
        long found = DiscMri.SectorAt(r, angle, total);
        Assert.InRange(found, sector - 1, sector + 1);
    }

    [Fact]
    public void Locate_RespectsRedBookGeometry()
    {
        // Program area starts at 25 mm; a full 80-minute disc stays on the platter.
        Assert.Equal(0.025, DiscMri.Locate(0).Radius, 4);
        double r74 = DiscMri.Locate(333_000).Radius;    // 74-min disc
        double r80 = DiscMri.Locate(360_000).Radius;    // 80-min disc
        Assert.True(r74 > 0.055 && r74 < 0.060, $"74-min lead-out at {r74 * 1000:F1} mm");
        Assert.True(r80 > r74 && r80 < 0.0605, $"80-min lead-out at {r80 * 1000:F1} mm");

        // Radius grows monotonically along the dump.
        double prev = 0;
        for (long s = 0; s <= 300_000; s += 10_000)
        {
            double r = DiscMri.Locate(s).Radius;
            Assert.True(r > prev);
            prev = r;
        }
    }

    // ---- classification ----------------------------------------------------

    private static byte[] Mode1Sector(long lba)
    {
        var user = new byte[2048];
        new Random((int)lba).NextBytes(user);
        var raw = new byte[2352];
        RawSectorBuilder.BuildMode1(user, Msf.FromSectors(lba + 150), raw);
        return raw;
    }

    [Fact]
    public void Classify_SpansAndSidecar_ProduceTheRightEvidence()
    {
        // 4 good data, 1 void-in-data, 3 audio (1 silent), with a sidecar
        // marking one unreadable and one boundary sector.
        var ms = new MemoryStream();
        for (int i = 0; i < 4; i++) ms.Write(Mode1Sector(i));
        ms.Write(new byte[2352]);                        // void inside the data span
        var noise = new byte[2352]; new Random(9).NextBytes(noise);
        ms.Write(noise);                                 // audio content
        ms.Write(new byte[2352]);                        // audio silence
        ms.Write(noise);                                 // audio content
        ms.Position = 0;

        var spans = new List<(long, long, bool)> { (0, 4, false), (5, 7, true) };
        var map = new BadSectorMap
        {
            Image = "x.bin", TotalSectors = 8,
            UnreadableLba = new long[] { 2, 6 },         // 6 is also boundary
            BoundaryLba = new long[] { 6 },
        };
        var ev = DiscMri.Classify(ms, spans, map);

        Assert.Equal(DiscMri.Evidence.DataGood, ev[0]);
        Assert.Equal(DiscMri.Evidence.Unreadable, ev[2]);      // sidecar overrules content
        Assert.Equal(DiscMri.Evidence.SynclessVoid, ev[4]);
        Assert.Equal(DiscMri.Evidence.Audio, ev[5]);
        Assert.Equal(DiscMri.Evidence.Boundary, ev[6]);        // boundary overrules unreadable
        Assert.Equal(DiscMri.Evidence.Audio, ev[7]);
    }

    [Fact]
    public void Classify_WithoutSpans_IsHonestlyAmbiguous()
    {
        var ms = new MemoryStream();
        ms.Write(Mode1Sector(0));
        ms.Write(new byte[2352]);                        // zero: could be silence OR void
        ms.Position = 0;
        var ev = DiscMri.Classify(ms);

        Assert.Equal(DiscMri.Evidence.DataGood, ev[0]);
        Assert.Equal(DiscMri.Evidence.AudioSilence, ev[1]);    // never claimed as damage
    }

    // ---- rendering ---------------------------------------------------------

    [Fact]
    public void RenderPng_IsAValidPngOfTheRequestedSize()
    {
        var ev = new DiscMri.Evidence[50_000];
        Array.Fill(ev, DiscMri.Evidence.DataGood);
        var png = DiscMri.RenderPng(ev, 128);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        // IHDR width/height are big-endian at offsets 16 and 20.
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        Assert.Equal(128, w);
        Assert.Equal(128, h);
    }

    /// <summary>
    /// The reason the MRI exists: physical damage has a visible physical
    /// shape. Damage every sector whose spiral pass crosses one narrow angular
    /// window (what a radial scratch does), render, and check pixels: red
    /// inside the streak's angle at several radii, green away from it.
    /// </summary>
    [Fact]
    public void RadialScratch_AppearsAsARadialStreak()
    {
        const long total = 300_000;
        var ev = new DiscMri.Evidence[total];
        for (long s = 0; s < total; s++)
        {
            double a = DiscMri.Locate(s).Theta % (2 * Math.PI);
            ev[s] = a < 0.35 ? DiscMri.Evidence.Unreadable : DiscMri.Evidence.DataGood;
        }

        int size = 400;
        var rgba = DiscMri.RenderRgba(ev, ref size);

        // Sample along the scratch angle and away from it, at three radii
        // spread across the program area (25–55 mm mapped onto the canvas).
        (byte R, byte G, byte B) At(double radiusMeters, double angle)
        {
            double scale = 0.060 / (size / 2.0 - 8);
            int x = (int)(size / 2.0 + Math.Cos(angle) * radiusMeters / scale);
            int y = (int)(size / 2.0 + Math.Sin(angle) * radiusMeters / scale);
            long o = ((long)y * size + x) * 4;
            return (rgba[o], rgba[o + 1], rgba[o + 2]);
        }

        foreach (double r in new[] { 0.030, 0.040, 0.050 })
        {
            var inStreak = At(r, 0.17);
            var offStreak = At(r, Math.PI);              // opposite side of the disc
            Assert.True(inStreak.R > inStreak.G, $"streak at {r * 1000:F0} mm should be red, got {inStreak}");
            Assert.True(offStreak.G > offStreak.R, $"clean area at {r * 1000:F0} mm should be green, got {offStreak}");
        }
    }
}
