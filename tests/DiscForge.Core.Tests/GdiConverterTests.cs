// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdi;
using DiscForge.Core.Gdi;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for GDI ↔ CDI conversion. Both formats hold the same track data, so the
/// bar is faithfulness: a GDI → CDI → GDI round trip must reproduce the index and
/// every track file byte-for-byte, and — the part that is easy to lose — the
/// GD-ROM's two-session layout, with the large LBA gap between the low-density
/// tracks and the high-density game, must survive.
/// </summary>
public class GdiConverterTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gdiconv_" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static void WriteTrack(string path, int sectors, int sectorSize, byte fill)
    {
        var data = new byte[sectors * sectorSize];
        Array.Fill(data, fill);
        File.WriteAllBytes(path, data);
    }

    /// <summary>A standard three-track GD-ROM: data + audio low-density, game high.</summary>
    private static void WriteSampleGdi(TempDir t)
    {
        WriteTrack(t.File("track01.bin"), 4, 2352, 0x11);
        WriteTrack(t.File("track02.raw"), 3, 2352, 0x22);
        WriteTrack(t.File("track03.bin"), 10, 2352, 0x33);
        File.WriteAllText(t.File("game.gdi"),
            "3\n" +
            "1 0 4 2352 track01.bin 0\n" +
            "2 600 0 2352 track02.raw 0\n" +
            "3 45000 4 2352 track03.bin 0\n");
    }

    // ---- GDI -> CDI ---------------------------------------------------------

    [Fact]
    public void Gdi_to_cdi_produces_two_sessions_split_at_the_high_density_boundary()
    {
        using var t = new TempDir();
        WriteSampleGdi(t);

        using var cdiStream = new MemoryStream();
        GdiConverter.GdiToCdi(t.File("game.gdi"), CdiVersion.V35, cdiStream);
        cdiStream.Position = 0;
        var img = CdiParser.Parse(cdiStream);

        Assert.Equal(2, img.Sessions.Count);
        Assert.Equal(2, img.Sessions[0].Tracks.Count);   // low-density data + audio
        Assert.Single(img.Sessions[1].Tracks);           // high-density game
        Assert.Equal(45000u, img.Sessions[1].Tracks[0].StartLba);
    }

    [Fact]
    public void Gdi_to_cdi_maps_track_types_and_sector_sizes()
    {
        using var t = new TempDir();
        WriteSampleGdi(t);

        using var cdiStream = new MemoryStream();
        GdiConverter.GdiToCdi(t.File("game.gdi"), CdiVersion.V35, cdiStream);
        cdiStream.Position = 0;
        var tracks = CdiParser.Parse(cdiStream).AllTracks.ToList();

        Assert.Equal(CdiTrackMode.Mode1, tracks[0].Mode);   // data
        Assert.Equal(CdiTrackMode.Audio, tracks[1].Mode);   // audio
        Assert.Equal(CdiTrackMode.Mode1, tracks[2].Mode);   // data
        Assert.All(tracks, x => Assert.Equal(CdiSectorSize.S2352, x.SectorSize));
    }

    [Fact]
    public void A_missing_track_file_is_refused()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File("game.gdi"), "1\n1 45000 4 2352 nope.bin 0\n");

        using var cdiStream = new MemoryStream();
        Assert.Throws<GdiFormatException>(() => GdiConverter.GdiToCdi(t.File("game.gdi"), CdiVersion.V35, cdiStream));
    }

    // ---- the round trip -----------------------------------------------------

    [Fact]
    public void Gdi_to_cdi_to_gdi_reproduces_the_index_layout()
    {
        using var t = new TempDir();
        WriteSampleGdi(t);

        using var cdiStream = new MemoryStream();
        GdiConverter.GdiToCdi(t.File("game.gdi"), CdiVersion.V35, cdiStream);
        cdiStream.Position = 0;
        var img = CdiParser.Parse(cdiStream);

        using var outDir = new TempDir();
        var result = GdiConverter.CdiToGdi(cdiStream, img, outDir.Path, "game");

        // Same track count, LBAs, types and sector sizes (filenames differ).
        var original = GdiParser.ParseFile(t.File("game.gdi"));
        var regenerated = GdiParser.Parse(result.GdiText);

        Assert.Equal(original.Tracks.Count, regenerated.Tracks.Count);
        Assert.Equal(
            original.Tracks.Select(x => (x.Number, x.StartLba, x.Type, x.SectorSize)),
            regenerated.Tracks.Select(x => (x.Number, x.StartLba, x.Type, x.SectorSize)));
    }

    [Fact]
    public void Gdi_to_cdi_to_gdi_reproduces_every_track_file_byte_for_byte()
    {
        using var t = new TempDir();
        WriteSampleGdi(t);

        using var cdiStream = new MemoryStream();
        GdiConverter.GdiToCdi(t.File("game.gdi"), CdiVersion.V35, cdiStream);
        cdiStream.Position = 0;
        var img = CdiParser.Parse(cdiStream);

        using var outDir = new TempDir();
        var result = GdiConverter.CdiToGdi(cdiStream, img, outDir.Path, "game");

        Assert.Equal(File.ReadAllBytes(t.File("track01.bin")),
                     File.ReadAllBytes(outDir.File(result.TrackFiles[0])));
        Assert.Equal(File.ReadAllBytes(t.File("track02.raw")),
                     File.ReadAllBytes(outDir.File(result.TrackFiles[1])));
        Assert.Equal(File.ReadAllBytes(t.File("track03.bin")),
                     File.ReadAllBytes(outDir.File(result.TrackFiles[2])));
    }

    [Fact]
    public void Data_tracks_get_a_bin_file_and_audio_tracks_a_raw_file()
    {
        using var t = new TempDir();
        WriteSampleGdi(t);

        using var cdiStream = new MemoryStream();
        GdiConverter.GdiToCdi(t.File("game.gdi"), CdiVersion.V35, cdiStream);
        cdiStream.Position = 0;
        var img = CdiParser.Parse(cdiStream);

        using var outDir = new TempDir();
        var result = GdiConverter.CdiToGdi(cdiStream, img, outDir.Path, "game");

        Assert.EndsWith(".bin", result.TrackFiles[0]);   // data
        Assert.EndsWith(".raw", result.TrackFiles[1]);   // audio
        Assert.EndsWith(".bin", result.TrackFiles[2]);   // data
    }

    [Fact]
    public void A_single_high_density_track_converts_to_one_session()
    {
        using var t = new TempDir();
        WriteTrack(t.File("track03.bin"), 8, 2352, 0x44);
        File.WriteAllText(t.File("game.gdi"), "1\n1 45000 4 2352 track03.bin 0\n");

        using var cdiStream = new MemoryStream();
        GdiConverter.GdiToCdi(t.File("game.gdi"), CdiVersion.V35, cdiStream);
        cdiStream.Position = 0;
        var img = CdiParser.Parse(cdiStream);

        Assert.Single(img.Sessions);
        Assert.Equal(45000u, img.AllTracks.First().StartLba);
    }
}
