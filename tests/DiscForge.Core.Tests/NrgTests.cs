// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cdi;
using DiscForge.Core.Nrg;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the Nero NRG reader/writer and NRG ↔ CDI conversion. NRG has no
/// committed real-Nero fixture here, so — like the CDI reader awaiting a real
/// DiscJuggler descriptor — the guarantee is a round trip: DiscForge's writer and
/// reader agree on the container, and a CDI → NRG → CDI conversion preserves
/// every track's mode, sector size, start LBA and data.
/// </summary>
public class NrgTests
{
    private static NrgWriter.TrackInput Track(NrgTrackMode mode, int sectorSize, long lba, uint sectors, byte fill) =>
        new()
        {
            Mode = mode, SectorSize = sectorSize, StartLba = lba, LengthSectors = sectors,
            DataWriter = os =>
            {
                var block = new byte[sectorSize];
                Array.Fill(block, fill);
                for (uint i = 0; i < sectors; i++) os.Write(block);
            },
        };

    private static byte[] SampleNrg() =>
        WriteNrg(
            Track(NrgTrackMode.Mode1, 2352, 0, 5, 0xA1),
            Track(NrgTrackMode.Audio, 2352, 5, 3, 0xB2),
            Track(NrgTrackMode.Mode1, 2048, 45000, 8, 0xC3));

    private static byte[] WriteNrg(params NrgWriter.TrackInput[] tracks)
    {
        using var ms = new MemoryStream();
        NrgWriter.Write(ms, tracks);
        return ms.ToArray();
    }

    private static byte[] WriteNrgV1(params NrgWriter.TrackInput[] tracks)
    {
        using var ms = new MemoryStream();
        NrgWriter.Write(ms, tracks, NrgVersion.V1);
        return ms.ToArray();
    }

    // ---- NRG round trip -----------------------------------------------------

    [Fact]
    public void A_written_nrg_is_recognised()
    {
        using var ms = new MemoryStream(SampleNrg());
        Assert.True(NrgParser.IsNrg(ms));
    }

    [Fact]
    public void Tracks_read_back_with_their_mode_size_lba_and_length()
    {
        using var ms = new MemoryStream(SampleNrg());
        var img = NrgParser.Parse(ms);

        Assert.True(img.IsV2);
        Assert.Equal(3, img.Tracks.Count);

        Assert.Equal(NrgTrackMode.Mode1, img.Tracks[0].Mode);
        Assert.Equal(2352, img.Tracks[0].SectorSize);
        Assert.Equal(0, img.Tracks[0].StartLba);
        Assert.Equal(5u, img.Tracks[0].LengthSectors);

        Assert.Equal(NrgTrackMode.Audio, img.Tracks[1].Mode);
        Assert.Equal(5, img.Tracks[1].StartLba);

        Assert.Equal(45000, img.Tracks[2].StartLba);   // the high LBA survives
        Assert.Equal(2048, img.Tracks[2].SectorSize);
        Assert.Equal(8u, img.Tracks[2].LengthSectors);
    }

    [Fact]
    public void The_track_data_offsets_point_at_the_right_bytes()
    {
        var nrg = SampleNrg();
        using var ms = new MemoryStream(nrg);
        var img = NrgParser.Parse(ms);

        Assert.Equal(0xA1, nrg[img.Tracks[0].DataOffset]);
        Assert.Equal(0xB2, nrg[img.Tracks[1].DataOffset]);
        Assert.Equal(0xC3, nrg[img.Tracks[2].DataOffset]);
    }

    [Fact]
    public void A_file_without_a_nero_footer_is_refused()
    {
        using var ms = new MemoryStream(new byte[10_000]);
        Assert.False(NrgParser.IsNrg(ms));
        Assert.Throws<NrgFormatException>(() => NrgParser.Parse(ms));
    }

    // ---- NRG v1 round trip --------------------------------------------------

    [Fact]
    public void A_v1_image_round_trips_with_its_tracks_lbas_and_data()
    {
        var nrg = WriteNrgV1(
            Track(NrgTrackMode.Mode1, 2352, 0, 5, 0xA1),
            Track(NrgTrackMode.Audio, 2352, 5, 3, 0xB2),
            Track(NrgTrackMode.Mode1, 2048, 200, 8, 0xC3));

        using var ms = new MemoryStream(nrg);
        Assert.True(NrgParser.IsNrg(ms));
        ms.Position = 0;
        var img = NrgParser.Parse(ms);

        Assert.False(img.IsV2);
        Assert.Equal(3, img.Tracks.Count);
        Assert.Equal(new long[] { 0, 5, 200 }, img.Tracks.Select(t => t.StartLba).ToArray());
        Assert.Equal(NrgTrackMode.Audio, img.Tracks[1].Mode);
        Assert.Equal(2048, img.Tracks[2].SectorSize);
        Assert.Equal(0xA1, nrg[img.Tracks[0].DataOffset]);
        Assert.Equal(0xC3, nrg[img.Tracks[2].DataOffset]);
    }

    [Fact]
    public void A_v1_footer_is_reported_as_v1()
    {
        var nrg = WriteNrgV1(Track(NrgTrackMode.Mode1, 2352, 0, 2, 0x55));
        using var ms = new MemoryStream(nrg);
        Assert.False(NrgParser.Parse(ms).IsV2);
    }

    // ---- CDI <-> NRG --------------------------------------------------------

    private static byte[] SampleCdi()
    {
        using var ms = new MemoryStream();
        var s1 = new List<CdiWriter.TrackInput>
        {
            new() { Mode = CdiTrackMode.Mode1, SectorSize = CdiSectorSize.S2352, PregapSectors = 0,
                    LengthSectors = 4, StartLba = 0, Data = Filled(4 * 2352, 0x11) },
            new() { Mode = CdiTrackMode.Audio, SectorSize = CdiSectorSize.S2352, PregapSectors = 0,
                    LengthSectors = 3, StartLba = 4, Data = Filled(3 * 2352, 0x22) },
        };
        var s2 = new List<CdiWriter.TrackInput>
        {
            new() { Mode = CdiTrackMode.Mode1, SectorSize = CdiSectorSize.S2048, PregapSectors = 0,
                    LengthSectors = 10, StartLba = 45000, Data = Filled(10 * 2048, 0x33) },
        };
        CdiWriter.Write(ms, CdiVersion.V35, new[] { (IReadOnlyList<CdiWriter.TrackInput>)s1, s2 });
        return ms.ToArray();
    }

    private static byte[] Filled(int n, byte v) { var b = new byte[n]; Array.Fill(b, v); return b; }

    [Fact]
    public void Cdi_to_nrg_carries_every_track_and_its_lba()
    {
        var cdiBytes = SampleCdi();
        using var cdi = new MemoryStream(cdiBytes);
        var image = CdiParser.Parse(cdi);

        using var nrg = new MemoryStream();
        cdi.Position = 0;
        NrgConverter.CdiToNrg(cdi, image, nrg);
        nrg.Position = 0;
        var nrgImage = NrgParser.Parse(nrg);

        Assert.Equal(3, nrgImage.Tracks.Count);
        Assert.Equal(new long[] { 0, 4, 45000 }, nrgImage.Tracks.Select(t => t.StartLba).ToArray());
        Assert.Equal(NrgTrackMode.Audio, nrgImage.Tracks[1].Mode);
    }

    [Fact]
    public void Cdi_to_nrg_to_cdi_preserves_tracks_and_data()
    {
        var cdiBytes = SampleCdi();
        using var cdi = new MemoryStream(cdiBytes);
        var image = CdiParser.Parse(cdi);

        using var nrg = new MemoryStream();
        cdi.Position = 0;
        NrgConverter.CdiToNrg(cdi, image, nrg);

        nrg.Position = 0;
        var nrgImage = NrgParser.Parse(nrg);
        using var cdi2 = new MemoryStream();
        nrg.Position = 0;
        NrgConverter.NrgToCdi(nrg, nrgImage, CdiVersion.V35, cdi2);

        cdi2.Position = 0;
        var back = CdiParser.Parse(cdi2);

        Assert.Equal(image.TrackCount, back.TrackCount);
        Assert.Equal(
            image.AllTracks.Select(t => (t.Mode, (int)t.SectorSize, t.StartLba, t.LengthSectors)),
            back.AllTracks.Select(t => (t.Mode, (int)t.SectorSize, t.StartLba, t.LengthSectors)));

        // Track content survives: extract track 3's first sector from each.
        var t3orig = image.AllTracks.Last();
        var t3back = back.AllTracks.Last();
        cdi.Position = 0; cdi2.Position = 0;
        using var a = new MemoryStream(); using var b = new MemoryStream();
        CdiExtractor.ExtractRaw(cdi, t3orig, a);
        CdiExtractor.ExtractRaw(cdi2, t3back, b);
        Assert.Equal(a.ToArray(), b.ToArray());
    }
}
