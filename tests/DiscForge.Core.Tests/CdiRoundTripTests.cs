using DiscForge.Core.Cdi;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Cross-validation of the canonical layout:
///  1. CdiWriter (C#) -> CdiParser (C#): internal round-trip across the
///     version/topology matrix.
///  2. Python gen_cdi.py fixtures -> CdiParser (C#): confirms the two
///     independent implementations agree on the same written spec.
///
/// Fixtures for (2) live in tests/fixtures/synthetic/ and are produced by
/// `python3 docs/reference/gen_cdi.py --suite tests/fixtures/synthetic`.
/// </summary>
public class CdiRoundTripTests
{
    private static CdiWriter.TrackInput Track(
        CdiTrackMode mode, CdiSectorSize size, uint pregap, uint length, uint lba,
        string name) => new()
    {
        Mode = mode, SectorSize = size, PregapSectors = pregap,
        LengthSectors = length, StartLba = lba, Filename = name,
    };

    [Fact]
    public void Writer_parser_roundtrip_multisession_mixed()
    {
        var sessions = new IReadOnlyList<CdiWriter.TrackInput>[]
        {
            new[] { Track(CdiTrackMode.Audio, CdiSectorSize.S2352, 150, 300, 0, "AUDIO01.WAV") },
            new[]
            {
                Track(CdiTrackMode.Mode1, CdiSectorSize.S2048, 150, 200, 45000, "DATA_M1.ISO"),
                Track(CdiTrackMode.Mode2, CdiSectorSize.S2336, 150, 120, 46000, "DATA_M2.RAW"),
            },
        };

        using var ms = new MemoryStream();
        CdiWriter.Write(ms, CdiVersion.V35, sessions);
        ms.Position = 0;

        var image = CdiParser.Parse(ms);

        Assert.Equal(CdiVersion.V35, image.Version);
        Assert.Equal(2, image.Sessions.Count);
        Assert.Equal(3, image.TrackCount);

        var all = image.AllTracks.ToList();
        Assert.Equal(CdiTrackMode.Audio, all[0].Mode);
        Assert.Equal("AUDIO01.WAV", all[0].SourceFilename);
        Assert.Equal(0, all[0].FileOffset);

        // Second track's file offset must equal the first track's stored bytes.
        long expectedOffset1 = (150L + 300) * 2352;
        Assert.Equal(expectedOffset1, all[1].FileOffset);
        Assert.Equal(CdiSectorSize.S2048, all[1].SectorSize);
        Assert.Equal(45000u, all[1].StartLba);

        long expectedOffset2 = expectedOffset1 + (150L + 200) * 2048;
        Assert.Equal(expectedOffset2, all[2].FileOffset);
        Assert.Equal(CdiTrackMode.Mode2, all[2].Mode);
        Assert.Equal(1, all[2].SessionIndex);
    }

    [Theory]
    [InlineData(CdiVersion.V2)]
    [InlineData(CdiVersion.V3)]
    [InlineData(CdiVersion.V35)]
    public void Writer_parser_roundtrip_all_versions(CdiVersion version)
    {
        var sessions = new IReadOnlyList<CdiWriter.TrackInput>[]
        {
            new[] { Track(CdiTrackMode.Mode1, CdiSectorSize.S2048, 150, 100, 0, "DATA_M1.ISO") },
        };

        using var ms = new MemoryStream();
        CdiWriter.Write(ms, version, sessions);
        ms.Position = 0;

        var image = CdiParser.Parse(ms);
        Assert.Equal(version, image.Version);
        Assert.Equal(1, image.TrackCount);
        var t = image.AllTracks.Single();
        Assert.Equal(100u, t.LengthSectors);
        Assert.Equal(CdiSectorSize.S2048, t.SectorSize);
    }

    // ---- Cross-implementation: parse Python-generated fixtures ----

    private static string FixtureDir()
    {
        // Walk up from the test binary to the repo's tests/fixtures/synthetic.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "synthetic");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException(
            "tests/fixtures/synthetic not found. Run gen_cdi.py --suite first.");
    }

    [Theory]
    [InlineData("single_data_v2.cdi", CdiVersion.V2, 1, 1)]
    [InlineData("single_data_v3.cdi", CdiVersion.V3, 1, 1)]
    [InlineData("single_data_v35.cdi", CdiVersion.V35, 1, 1)]
    [InlineData("audio_data_v35.cdi", CdiVersion.V35, 2, 2)]
    [InlineData("multitrack_mixed_v3.cdi", CdiVersion.V3, 1, 3)]
    [InlineData("three_session_v35.cdi", CdiVersion.V35, 3, 3)]
    public void Parses_python_generated_fixtures(
        string file, CdiVersion version, int sessions, int tracks)
    {
        var path = Path.Combine(FixtureDir(), file);
        Assert.True(File.Exists(path), $"Missing fixture: {path}");

        using var fs = File.OpenRead(path);
        var image = CdiParser.Parse(fs);

        Assert.Equal(version, image.Version);
        Assert.Equal(sessions, image.Sessions.Count);
        Assert.Equal(tracks, image.TrackCount);

        // File offsets must be monotonically increasing and start at 0.
        long prev = -1;
        foreach (var t in image.AllTracks)
        {
            Assert.True(t.FileOffset > prev, "file offsets must increase");
            prev = t.FileOffset;
        }
        Assert.Equal(0, image.AllTracks.First().FileOffset);
    }
}
