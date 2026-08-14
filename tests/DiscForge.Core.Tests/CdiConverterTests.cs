using DiscForge.Core.Cdi;
using DiscForge.Core.Convert;
using DiscForge.Core.Cue;
using Xunit;

namespace DiscForge.Core.Tests;

public class CueTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(75, 0, 1, 0)]
    [InlineData(150, 0, 2, 0)]
    [InlineData(4500, 1, 0, 0)]
    [InlineData(4576, 1, 1, 1)]
    public void Msf_from_sectors(long sectors, int m, int s, int f)
    {
        var msf = Msf.FromSectors(sectors);
        Assert.Equal((m, s, f), (msf.Minutes, msf.Seconds, msf.Frames));
        Assert.Equal(sectors, msf.ToSectors());
    }

    [Fact]
    public void Cue_write_parse_roundtrip()
    {
        var sheet = new CueSheet
        {
            Tracks = new[]
            {
                new CueTrack { Number = 1, Type = CueTrackType.Audio, File = "t01.bin",
                    Pregap = Msf.FromSectors(150),
                    Indices = new[] { new CueIndex(1, Msf.FromSectors(0)) } },
                new CueTrack { Number = 2, Type = CueTrackType.Mode2_2336, File = "t02.bin",
                    Indices = new[] { new CueIndex(1, Msf.FromSectors(0)) } },
            },
        };

        var text = sheet.Write();
        var back = CueSheet.Parse(text);

        Assert.Equal(2, back.Tracks.Count);
        Assert.Equal(CueTrackType.Audio, back.Tracks[0].Type);
        Assert.Equal("t01.bin", back.Tracks[0].File);
        Assert.Equal(150, back.Tracks[0].Pregap!.Value.ToSectors());
        Assert.Equal(CueTrackType.Mode2_2336, back.Tracks[1].Type);
        // Regression: Flush() once read the *current* FILE rather than the one
        // that owned the pending track, so every track claimed the NEXT file.
        Assert.Equal("t02.bin", back.Tracks[1].File);
        Assert.Null(back.Tracks[1].Pregap);
    }

    [Fact]
    public void Parse_single_file_multi_track_cue_shares_one_bin()
    {
        // The common real-world shape: one BIN, several TRACKs inside it.
        const string cue =
            "FILE \"big.bin\" BINARY\n" +
            "  TRACK 01 MODE1/2048\n" +
            "    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n" +
            "    INDEX 01 00:05:00\n";

        var sheet = CueSheet.Parse(cue);

        Assert.Equal(2, sheet.Tracks.Count);
        Assert.All(sheet.Tracks, t => Assert.Equal("big.bin", t.File));
        Assert.Equal(CueTrackType.Mode1_2048, sheet.Tracks[0].Type);
        Assert.Equal(CueTrackType.Audio, sheet.Tracks[1].Type);
    }
}

public class CdiConverterTests
{
    private static byte[] FilledTrack(uint totalSectors, int sectorBytes, byte fill)
    {
        var d = new byte[totalSectors * sectorBytes];
        Array.Fill(d, fill);
        return d;
    }

    [Fact]
    public void Cdi_to_bincue_to_cdi_preserves_content_and_structure_single_session()
    {
        // Single session, 3 tracks: audio(2352), mode1(2048), mode2(2336).
        var sessions = new IReadOnlyList<CdiWriter.TrackInput>[]
        {
            new[]
            {
                new CdiWriter.TrackInput { Mode = CdiTrackMode.Audio, SectorSize = CdiSectorSize.S2352,
                    PregapSectors = 150, LengthSectors = 60, StartLba = 0, Filename = "A.WAV",
                    Data = FilledTrack(210, 2352, 0xA1) },
                new CdiWriter.TrackInput { Mode = CdiTrackMode.Mode1, SectorSize = CdiSectorSize.S2048,
                    PregapSectors = 0, LengthSectors = 100, StartLba = 210, Filename = "D1.ISO",
                    Data = FilledTrack(100, 2048, 0xB2) },
                new CdiWriter.TrackInput { Mode = CdiTrackMode.Mode2, SectorSize = CdiSectorSize.S2336,
                    PregapSectors = 150, LengthSectors = 80, StartLba = 310, Filename = "D2.RAW",
                    Data = FilledTrack(230, 2336, 0xC3) },
            },
        };

        var tmp = Path.Combine(Path.GetTempPath(), "ojug_conv_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cdiPath = Path.Combine(tmp, "src.cdi");
            Directory.CreateDirectory(tmp);
            using (var f = File.Create(cdiPath)) CdiWriter.Write(f, CdiVersion.V3, sessions);

            // CDI -> BIN/CUE
            CdiImage srcImage;
            CdiConverter.BinCueResult conv;
            using (var f = File.OpenRead(cdiPath))
            {
                srcImage = CdiParser.Parse(f);
                conv = CdiConverter.CdiToBinCue(f, srcImage, tmp, "out");
            }
            Assert.Empty(conv.Warnings); // single session -> no warning

            // BIN/CUE -> CDI
            var rebuiltPath = Path.Combine(tmp, "rebuilt.cdi");
            using (var f = File.Create(rebuiltPath))
                CdiConverter.BinCueToCdi(conv.CueText, tmp, CdiVersion.V3, f);

            // Compare structure + per-track cooked content.
            using var srcFs = File.OpenRead(cdiPath);
            using var rebFs = File.OpenRead(rebuiltPath);
            var a = CdiParser.Parse(srcFs);
            var b = CdiParser.Parse(rebFs);

            Assert.Equal(a.TrackCount, b.TrackCount);
            var at = a.AllTracks.ToList();
            var bt = b.AllTracks.ToList();
            for (int i = 0; i < at.Count; i++)
            {
                Assert.Equal(at[i].Mode, bt[i].Mode);
                Assert.Equal(at[i].SectorSize, bt[i].SectorSize);
                Assert.Equal(at[i].LengthSectors, bt[i].LengthSectors);
                Assert.Equal(at[i].PregapSectors, bt[i].PregapSectors);

                using var ea = new MemoryStream();
                using var eb = new MemoryStream();
                CdiExtractor.ExtractUserData(srcFs, at[i], ea);
                CdiExtractor.ExtractUserData(rebFs, bt[i], eb);
                Assert.True(ea.ToArray().AsSpan().SequenceEqual(eb.ToArray()),
                    $"track {i + 1} content mismatch after round-trip");
            }
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Multisession_conversion_warns()
    {
        var sessions = new IReadOnlyList<CdiWriter.TrackInput>[]
        {
            new[] { new CdiWriter.TrackInput { Mode = CdiTrackMode.Audio, SectorSize = CdiSectorSize.S2352,
                PregapSectors = 0, LengthSectors = 10, StartLba = 0, Data = FilledTrack(10, 2352, 1) } },
            new[] { new CdiWriter.TrackInput { Mode = CdiTrackMode.Mode1, SectorSize = CdiSectorSize.S2048,
                PregapSectors = 0, LengthSectors = 10, StartLba = 45000, Data = FilledTrack(10, 2048, 2) } },
        };

        var tmp = Path.Combine(Path.GetTempPath(), "ojug_ms_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            var cdiPath = Path.Combine(tmp, "ms.cdi");
            using (var f = File.Create(cdiPath)) CdiWriter.Write(f, CdiVersion.V35, sessions);
            using var fs = File.OpenRead(cdiPath);
            var img = CdiParser.Parse(fs);
            var conv = CdiConverter.CdiToBinCue(fs, img, tmp, "out");
            Assert.NotEmpty(conv.Warnings);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }
}
