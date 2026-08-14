// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.CompilerServices;
using DiscForge.Core.Chd;
using DiscForge.Core.Convert;
using DiscForge.Core.Cue;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The universal conversion hub (<see cref="DiscConverter"/>): every format is
/// read into one canonical <see cref="DiscModel"/> and every writer emits from it,
/// so a conversion is Read -> model -> Write. These tests round-trip a synthetic
/// multi-track disc (a MODE1/2352 data track + an AUDIO track) through each
/// supported target and assert the track data survives byte-for-byte, plus read a
/// real chdman-produced CHD and check malformed/unsupported inputs fault cleanly.
/// </summary>
public class DiscConvertTests
{
    // A small two-track disc: 12 data sectors + 8 audio sectors (both multiples of
    // four frames, so no CHD track padding perturbs the layout).
    private static DiscModel SampleDisc()
    {
        var data = new byte[2352 * 12];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 3 + 7) % 251);
        var audio = new byte[2352 * 8];
        new Random(1234).NextBytes(audio);
        return new DiscModel
        {
            Tracks = new[]
            {
                new DiscModelTrack { Number = 1, Type = CueTrackType.Mode1_2352, SectorSize = 2352, Data = data },
                new DiscModelTrack { Number = 2, Type = CueTrackType.Audio, SectorSize = 2352, Data = audio },
            },
        };
    }

    private static DiscModel SingleDataDisc()
    {
        var data = new byte[2048 * 20];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 5 + 1) % 250);
        return new DiscModel
        {
            Tracks = new[]
            {
                new DiscModelTrack { Number = 1, Type = CueTrackType.Mode1_2048, SectorSize = 2048, Data = data },
            },
        };
    }

    private static void AssertTracksEqual(DiscModel expected, DiscModel actual)
    {
        Assert.Equal(expected.Tracks.Count, actual.Tracks.Count);
        for (int i = 0; i < expected.Tracks.Count; i++)
        {
            Assert.Equal(expected.Tracks[i].Number, actual.Tracks[i].Number);
            Assert.Equal(expected.Tracks[i].Type, actual.Tracks[i].Type);
            Assert.Equal(expected.Tracks[i].SectorSize, actual.Tracks[i].SectorSize);
            Assert.Equal(expected.Tracks[i].Data, actual.Tracks[i].Data);
        }
    }

    private static TempDir NewDir() => new("dforge_hubtest_");

    private static string AssetPath([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "assets", "test-cdfl.chd");

    // ---- core matrix --------------------------------------------------------

    [Fact]
    public void BinCue_round_trips_through_the_hub()
    {
        using var dir = NewDir();
        var src = SampleDisc();
        var cue = Path.Combine(dir.Path, "disc.cue");
        DiscConverter.Write(src, cue);
        var back = DiscConverter.Read(cue);
        AssertTracksEqual(src, back);
    }

    [Fact]
    public void BinCue_to_CHD_to_BinCue_is_byte_identical()
    {
        using var dir = NewDir();
        var src = SampleDisc();
        var chd = Path.Combine(dir.Path, "disc.chd");
        DiscConverter.Write(src, chd);
        var back = DiscConverter.Read(chd);
        AssertTracksEqual(src, back);
    }

    [Fact]
    public void BinCue_to_CDI_to_BinCue_preserves_track_data()
    {
        using var dir = NewDir();
        var src = SampleDisc();
        var cdi = Path.Combine(dir.Path, "disc.cdi");
        DiscConverter.Write(src, cdi);
        var back = DiscConverter.Read(cdi);
        AssertTracksEqual(src, back);
    }

    [Fact]
    public void BinCue_to_NRG_to_BinCue_preserves_track_data()
    {
        using var dir = NewDir();
        var src = SampleDisc();
        var nrg = Path.Combine(dir.Path, "disc.nrg");
        DiscConverter.Write(src, nrg);
        var back = DiscConverter.Read(nrg);
        AssertTracksEqual(src, back);
    }

    [Fact]
    public void ISO_round_trips_through_the_hub()
    {
        using var dir = NewDir();
        var src = SingleDataDisc();
        var iso = Path.Combine(dir.Path, "disc.iso");
        DiscConverter.Write(src, iso);
        var back = DiscConverter.Read(iso);
        AssertTracksEqual(src, back);
    }

    [Fact]
    public void CHD_to_CDI_crosses_formats_through_the_hub()
    {
        using var dir = NewDir();
        var src = SampleDisc();
        var chd = Path.Combine(dir.Path, "disc.chd");
        var cdi = Path.Combine(dir.Path, "disc.cdi");
        DiscConverter.Write(src, chd);
        DiscConverter.Convert(chd, cdi);      // CHD -> model -> CDI, one path
        var back = DiscConverter.Read(cdi);
        AssertTracksEqual(src, back);
    }

    [Fact]
    public void Convert_writes_the_output_file_on_disk()
    {
        using var dir = NewDir();
        var src = SampleDisc();
        var cue = Path.Combine(dir.Path, "src.cue");
        var chd = Path.Combine(dir.Path, "out.chd");
        DiscConverter.Write(src, cue);
        DiscConverter.Convert(cue, chd);
        Assert.True(File.Exists(chd));
        // The written CHD is a real, self-verifying CD CHD.
        var r = ChdExtractor.ExtractCd(File.ReadAllBytes(chd));
        Assert.True(r.Verified);
        Assert.Equal(2, r.Tracks);
    }

    // ---- reading a real chdman image ---------------------------------------

    [Fact]
    public void Reads_a_real_chdman_created_chd()
    {
        var model = DiscConverter.Read(AssetPath());
        Assert.Single(model.Tracks);
        Assert.Equal(CueTrackType.Audio, model.Tracks[0].Type);
        Assert.Equal(2352, model.Tracks[0].SectorSize);
        Assert.Equal(2822400, model.Tracks[0].Data.Length);   // 1200 frames * 2352
        Assert.Equal(1200, model.Tracks[0].SectorCount);
    }

    // ---- structure & metadata ----------------------------------------------

    [Fact]
    public void Model_preserves_track_count_and_types()
    {
        using var dir = NewDir();
        var src = SampleDisc();
        var chd = Path.Combine(dir.Path, "disc.chd");
        DiscConverter.Write(src, chd);
        var back = DiscConverter.Read(chd);
        Assert.Equal(2, back.Tracks.Count);
        Assert.Equal(CueTrackType.Mode1_2352, back.Tracks[0].Type);
        Assert.Equal(CueTrackType.Audio, back.Tracks[1].Type);
    }

    // ---- error handling -----------------------------------------------------

    [Fact]
    public void Unsupported_input_extension_throws_a_domain_exception()
    {
        using var dir = NewDir();
        var bogus = Path.Combine(dir.Path, "file.xyz");
        File.WriteAllBytes(bogus, new byte[16]);
        Assert.Throws<DiscConvertException>(() => DiscConverter.Read(bogus));
    }

    [Fact]
    public void Unsupported_output_extension_throws_a_domain_exception()
    {
        using var dir = NewDir();
        var src = SampleDisc();
        Assert.Throws<DiscConvertException>(
            () => DiscConverter.Write(src, Path.Combine(dir.Path, "out.xyz")));
    }

    [Fact]
    public void Missing_input_file_throws_a_domain_exception()
    {
        Assert.Throws<DiscConvertException>(
            () => DiscConverter.Read(Path.Combine(Path.GetTempPath(), "nope-does-not-exist.cue")));
    }

    [Fact]
    public void A_truncated_iso_that_is_not_whole_sectors_throws()
    {
        using var dir = NewDir();
        var iso = Path.Combine(dir.Path, "bad.iso");
        File.WriteAllBytes(iso, new byte[2048 + 100]);   // not a whole number of 2048-byte sectors
        Assert.Throws<DiscConvertException>(() => DiscConverter.Read(iso));
    }

    [Fact]
    public void Writing_a_raw_audio_disc_as_iso_is_rejected_clearly()
    {
        using var dir = NewDir();
        // A 2352-byte data model cannot become a cooked ISO through the hub.
        var raw = new DiscModel
        {
            Tracks = new[]
            {
                new DiscModelTrack { Number = 1, Type = CueTrackType.Mode1_2352, SectorSize = 2352,
                                     Data = new byte[2352 * 4] },
            },
        };
        Assert.Throws<DiscConvertException>(() => DiscConverter.Write(raw, Path.Combine(dir.Path, "out.iso")));
    }

    [Fact]
    public void Writing_an_empty_model_throws()
    {
        using var dir = NewDir();
        var empty = new DiscModel { Tracks = Array.Empty<DiscModelTrack>() };
        Assert.Throws<DiscConvertException>(() => DiscConverter.Write(empty, Path.Combine(dir.Path, "out.cue")));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir(string prefix) => Path = Directory.CreateTempSubdirectory(prefix).FullName;
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
