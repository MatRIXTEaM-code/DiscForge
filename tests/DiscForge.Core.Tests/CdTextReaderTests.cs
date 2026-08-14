// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

public class CdTextReaderTests
{
    private static CdTextBuilder.DiscText SampleAlbum() => new()
    {
        AlbumTitle = "Greatest Hits",
        AlbumPerformer = "The Examples",
        Tracks = new[]
        {
            new CdTextBuilder.TrackText("Opening Theme", "The Examples"),
            new CdTextBuilder.TrackText("Second Song", null),
            new CdTextBuilder.TrackText("The Long Finale That Spans Packs", "Guest Vocalist"),
        },
    };

    [Fact]
    public void Round_trips_album_and_track_text_through_the_builder()
    {
        var packs = CdTextBuilder.BuildPacks(SampleAlbum(), firstTrack: 1, lastTrack: 3);
        var info = CdTextReader.Read(packs);

        Assert.Equal("Greatest Hits", info.AlbumTitle);
        Assert.Equal("The Examples", info.AlbumPerformer);
        Assert.Equal(3, info.Tracks.Count);
        Assert.Equal("Opening Theme", info.Tracks[0].Title);
        Assert.Equal("The Examples", info.Tracks[0].Performer);
        Assert.Equal("Second Song", info.Tracks[1].Title);
        Assert.Null(info.Tracks[1].Performer);      // this track had no performer
        Assert.Equal("The Long Finale That Spans Packs", info.Tracks[2].Title);
        Assert.Equal("Guest Vocalist", info.Tracks[2].Performer);
    }

    [Fact]
    public void Reads_the_size_information_pack()
    {
        var packs = CdTextBuilder.BuildPacks(SampleAlbum(), firstTrack: 1, lastTrack: 3);
        var info = CdTextReader.Read(packs);
        Assert.Equal(1, info.FirstTrack);
        Assert.Equal(3, info.LastTrack);
        Assert.Equal(0x09, info.LanguageCode);      // English
        Assert.Equal(0x00, info.CharacterCode);     // ISO 8859-1
    }

    [Fact]
    public void Repeated_pack_cycles_do_not_duplicate_text()
    {
        var packs = CdTextBuilder.BuildPacks(SampleAlbum(), 1, 3);
        // The lead-in loops the packs; feed three copies and the text must not triple.
        var looped = packs.Concat(packs).Concat(packs).ToList();
        var info = CdTextReader.Read(looped);
        Assert.Equal("Greatest Hits", info.AlbumTitle);
        Assert.Equal("Opening Theme", info.Tracks[0].Title);
    }

    [Fact]
    public void A_bad_crc_pack_is_counted_and_dropped()
    {
        var packs = CdTextBuilder.BuildPacks(SampleAlbum(), 1, 3);
        packs[0][5] ^= 0xFF;   // corrupt payload → CRC now fails
        var info = CdTextReader.Read(packs, requireValidCrc: true);
        Assert.True(info.PacksBadCrc >= 1);
        // The first title pack is gone, so the album title is damaged — but parsing still succeeds.
        Assert.NotNull(info);
    }

    [Fact]
    public void Round_trips_through_the_rw_symbol_layer()
    {
        var packs = CdTextBuilder.BuildPacks(SampleAlbum(), 1, 3);
        // Emit the packs into lead-in R–W symbols, then decode them back.
        int sectors = (packs.Length + CdTextBuilder.PacksPerSector - 1) / CdTextBuilder.PacksPerSector;
        var symbols = new byte[sectors * 96];
        for (int s = 0; s < sectors; s++)
            CdTextBuilder.FillSectorRw(packs, s, symbols.AsSpan(s * 96, 96));

        var recovered = CdTextReader.DecodeRwSymbols(symbols);
        var info = CdTextReader.Read(recovered);
        Assert.Equal("Greatest Hits", info.AlbumTitle);
        Assert.Equal("The Long Finale That Spans Packs", info.Tracks[2].Title);
    }

    [Fact]
    public void A_flat_pack_stream_parses_and_skips_a_four_byte_header()
    {
        var packs = CdTextBuilder.BuildPacks(SampleAlbum(), 1, 3);
        var flat = new List<byte> { 0, 0, 0, 0 };   // 4-byte .cdt header
        foreach (var p in packs) flat.AddRange(p);
        var info = CdTextReader.ReadPackStream(flat.ToArray());
        Assert.Equal("Greatest Hits", info.AlbumTitle);
        Assert.Equal(3, info.Tracks.Count);
    }

    [Fact]
    public void No_packs_yields_no_text()
    {
        var info = CdTextReader.Read(Array.Empty<byte[]>());
        Assert.False(info.HasText);
        Assert.Contains("No CD-TEXT", info.Summary());
    }
}
