using DiscForge.Core.Cdi;
using DiscForge.Core.Convert;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class IsoConverterTests
{
    private static byte[] Pattern(int len, byte seed)
    {
        var d = new byte[len];
        for (int i = 0; i < len; i++) d[i] = (byte)((i * 13 + seed) & 0xFF);
        return d;
    }

    /// <summary>A real ISO built by our own builder.</summary>
    private static byte[] RealIso() =>
        IsoBuilder.Build("TESTVOL", new[]
        {
            new IsoBuilder.FileEntry("README.TXT", Pattern(2000, 1)),
            new IsoBuilder.FileEntry("DATA.BIN", Pattern(9000, 2)),
        }).Image;

    private static string TempFile(byte[] content, string ext)
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ext);
        File.WriteAllBytes(p, content);
        return p;
    }

    [Fact]
    public void Wraps_an_iso_into_a_cdi_our_parser_reads_back()
    {
        var iso = RealIso();
        var path = TempFile(iso, ".iso");
        try
        {
            using var cdi = new MemoryStream();
            var result = IsoConverter.IsoToCdi(path, CdiVersion.V35, cdi);

            Assert.Equal(iso.Length / 2048, result.Sectors);
            Assert.Empty(result.Warnings);           // it IS an ISO 9660

            cdi.Position = 0;
            var image = CdiParser.Parse(cdi);
            var track = Assert.Single(image.AllTracks);
            Assert.Equal(CdiTrackMode.Mode1, track.Mode);
            Assert.Equal(CdiSectorSize.S2048, track.SectorSize);
            Assert.Equal((uint)(iso.Length / 2048), track.LengthSectors);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Iso_to_cdi_to_iso_round_trips_byte_for_byte()
    {
        // The proof: wrapping and unwrapping must return exactly the input.
        var iso = RealIso();
        var path = TempFile(iso, ".iso");
        try
        {
            using var cdi = new MemoryStream();
            IsoConverter.IsoToCdi(path, CdiVersion.V35, cdi);
            cdi.Position = 0;

            var image = CdiParser.Parse(cdi);
            using var back = new MemoryStream();
            IsoConverter.CdiToIso(cdi, image, back);

            Assert.Equal(iso, back.ToArray());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_wrapped_iso_is_still_browsable()
    {
        // End to end: an ISO wrapped as CDI must still list its files.
        var iso = RealIso();
        var path = TempFile(iso, ".iso");
        try
        {
            using var cdi = new MemoryStream();
            IsoConverter.IsoToCdi(path, CdiVersion.V35, cdi);
            cdi.Position = 0;

            var image = CdiParser.Parse(cdi);
            using var view = new CdiUserDataStream(cdi, image.AllTracks.Single());
            var dir = IsoReader.Read(view);

            Assert.Equal(2, dir.Files.Count());
            Assert.Contains(dir.Files, f => f.Name == "README.TXT");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_file_that_is_not_a_whole_number_of_sectors_is_refused()
    {
        // A raw 2352-byte BIN is the classic mistake here.
        var path = TempFile(new byte[2352 * 3], ".iso");
        try
        {
            using var cdi = new MemoryStream();
            var ex = Assert.Throws<InvalidDataException>(
                () => IsoConverter.IsoToCdi(path, CdiVersion.V35, cdi));
            Assert.Contains("2048", ex.Message);
            Assert.Contains("BIN/CUE", ex.Message);   // point at the right tool
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_sector_aligned_file_without_an_iso_signature_warns_but_converts()
    {
        // UDF-only or HFS images are legitimate data tracks — warn, don't refuse.
        var path = TempFile(new byte[2048 * 20], ".iso");
        try
        {
            using var cdi = new MemoryStream();
            var result = IsoConverter.IsoToCdi(path, CdiVersion.V35, cdi);

            Assert.Equal(20, result.Sectors);
            Assert.Contains(result.Warnings, w => w.Contains("ISO 9660 signature"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        var path = TempFile(Array.Empty<byte>(), ".iso");
        try
        {
            using var cdi = new MemoryStream();
            Assert.Throws<InvalidDataException>(() => IsoConverter.IsoToCdi(path, CdiVersion.V35, cdi));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_audio_only_image_cannot_become_an_iso()
    {
        var inputs = new[]
        {
            new CdiWriter.TrackInput
            {
                Mode = CdiTrackMode.Audio, SectorSize = CdiSectorSize.S2352,
                PregapSectors = 0, LengthSectors = 10, StartLba = 0,
                Filename = "T1.BIN", Data = new byte[2352 * 10],
            },
        };
        var ms = new MemoryStream();
        CdiWriter.Write(ms, CdiVersion.V35, new[] { (IReadOnlyList<CdiWriter.TrackInput>)inputs });
        ms.Position = 0;
        var image = CdiParser.Parse(ms);

        using var iso = new MemoryStream();
        var ex = Assert.Throws<InvalidDataException>(() => IsoConverter.CdiToIso(ms, image, iso));
        Assert.Contains("audio disc cannot become an ISO", ex.Message);
    }

    [Fact]
    public void Mixed_mode_to_iso_keeps_the_data_track_and_warns_about_the_audio()
    {
        var inputs = new[]
        {
            new CdiWriter.TrackInput
            {
                Mode = CdiTrackMode.Mode1, SectorSize = CdiSectorSize.S2048,
                PregapSectors = 0, LengthSectors = 5, StartLba = 0,
                Filename = "T1.BIN", Data = Pattern(2048 * 5, 3),
            },
            new CdiWriter.TrackInput
            {
                Mode = CdiTrackMode.Audio, SectorSize = CdiSectorSize.S2352,
                PregapSectors = 0, LengthSectors = 10, StartLba = 5,
                Filename = "T2.BIN", Data = new byte[2352 * 10],
            },
        };
        var ms = new MemoryStream();
        CdiWriter.Write(ms, CdiVersion.V35, new[] { (IReadOnlyList<CdiWriter.TrackInput>)inputs });
        ms.Position = 0;
        var image = CdiParser.Parse(ms);

        using var iso = new MemoryStream();
        var result = IsoConverter.CdiToIso(ms, image, iso);

        Assert.Equal(5, result.Sectors);
        Assert.Contains(result.Warnings, w => w.Contains("audio tracks are dropped"));
    }
}
