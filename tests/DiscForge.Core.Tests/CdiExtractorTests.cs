using DiscForge.Core.Cdi;
using Xunit;

namespace DiscForge.Core.Tests;

public class CdiExtractorTests
{
    // ---- Sector cooking windows ----

    [Theory]
    [InlineData(CdiSectorSize.S2048, CdiTrackMode.Mode1, 0, 2048)]
    [InlineData(CdiSectorSize.S2336, CdiTrackMode.Mode2, 8, 2048)]
    [InlineData(CdiSectorSize.S2352, CdiTrackMode.Mode1, 16, 2048)]
    [InlineData(CdiSectorSize.S2352, CdiTrackMode.Mode2, 24, 2048)]
    [InlineData(CdiSectorSize.S2352, CdiTrackMode.Audio, 0, 2352)]
    public void UserDataWindow_matches_spec(
        CdiSectorSize size, CdiTrackMode mode, int expOff, int expLen)
    {
        var (off, len) = CdiExtractor.UserDataWindow(size, mode);
        Assert.Equal(expOff, off);
        Assert.Equal(expLen, len);
    }

    // ---- Synthetic round-trip: write -> extract -> check length & content ----

    [Fact]
    public void Extract_userdata_length_and_content_from_synthetic()
    {
        // A single mode1/2048 data track filled with a known byte.
        const byte fill = 0x5A;
        const uint length = 40, pregap = 10;
        var data = new byte[(pregap + length) * 2048];
        Array.Fill(data, fill);

        var sessions = new IReadOnlyList<CdiWriter.TrackInput>[]
        {
            new[]
            {
                new CdiWriter.TrackInput
                {
                    Mode = CdiTrackMode.Mode1, SectorSize = CdiSectorSize.S2048,
                    PregapSectors = pregap, LengthSectors = length, StartLba = 0,
                    Filename = "DATA.ISO", Data = data,
                },
            },
        };

        using var img = new MemoryStream();
        CdiWriter.Write(img, CdiVersion.V35, sessions);
        img.Position = 0;

        var parsed = CdiParser.Parse(img);
        var track = parsed.AllTracks.Single();

        using var outp = new MemoryStream();
        long written = CdiExtractor.ExtractUserData(img, track, outp);

        Assert.Equal(length * 2048L, written);              // pregap excluded
        Assert.Equal(length * 2048L, outp.Length);
        Assert.All(outp.ToArray(), b => Assert.Equal(fill, b));
    }

    [Fact]
    public void Extract_cooks_2336_user_window_correctly()
    {
        // Build a 2336-sector track where only the user window (offset 8, len
        // 2048) carries a marker byte; the subheader/ECC bytes carry a different
        // byte. Extraction must return ONLY the user window.
        const uint length = 5, pregap = 0;
        var data = new byte[(pregap + length) * 2336];
        Array.Fill(data, (byte)0xEE);                       // framing bytes
        for (uint s = 0; s < length; s++)
            for (int i = 0; i < 2048; i++)
                data[s * 2336 + 8 + i] = 0xC3;              // user window

        var sessions = new IReadOnlyList<CdiWriter.TrackInput>[]
        {
            new[]
            {
                new CdiWriter.TrackInput
                {
                    Mode = CdiTrackMode.Mode2, SectorSize = CdiSectorSize.S2336,
                    PregapSectors = pregap, LengthSectors = length, StartLba = 0,
                    Filename = "M2.RAW", Data = data,
                },
            },
        };

        using var img = new MemoryStream();
        CdiWriter.Write(img, CdiVersion.V3, sessions);
        img.Position = 0;
        var track = CdiParser.Parse(img).AllTracks.Single();

        using var outp = new MemoryStream();
        CdiExtractor.ExtractUserData(img, track, outp);

        Assert.Equal(length * 2048L, outp.Length);
        Assert.All(outp.ToArray(), b => Assert.Equal(0xC3, b)); // no framing leaked
    }

    // ---- The strong one: reconstruct a REAL ISO from the genuine cdi4dc image ----

    [Fact]
    public void Reconstructs_source_iso_from_real_cdi4dc_image()
    {
        var dir = FindFixtures();
        var cdiPath = Path.Combine(dir, "cdi4dc_audiodata_v35.cdi");
        var isoPath = Path.Combine(dir, "source.iso");
        if (!File.Exists(cdiPath) || !File.Exists(isoPath))
            return; // fixtures optional in some checkouts

        var iso = File.ReadAllBytes(isoPath);

        // Confirmed parameters for this fixture's data track (see
        // docs/reference/validate_cdi.py): Mode2/Form1, 2336-byte sectors, user
        // content begins at file offset 1413504, 188 sectors, no pregap in the
        // stored content region. We construct the track descriptor directly
        // because full "wild" cdi4dc descriptor parsing is a separate compat
        // path; the extractor's cooking is what's under test here.
        var track = new CdiTrack
        {
            Number = 1, SessionIndex = 1,
            Mode = CdiTrackMode.Mode2, SectorSize = CdiSectorSize.S2336,
            PregapSectors = 0, LengthSectors = (uint)(iso.Length / 2048),
            StartLba = 45000, TotalSectors = (uint)(iso.Length / 2048),
            FileOffset = 1413504,
        };

        using var cdi = File.OpenRead(cdiPath);
        using var outp = new MemoryStream();
        CdiExtractor.ExtractUserData(cdi, track, outp);

        Assert.Equal(iso.Length, outp.Length);
        Assert.True(iso.AsSpan().SequenceEqual(outp.ToArray()),
            "extracted user data must byte-match source.iso");
    }

    private static string FindFixtures()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return AppContext.BaseDirectory;
    }
}
