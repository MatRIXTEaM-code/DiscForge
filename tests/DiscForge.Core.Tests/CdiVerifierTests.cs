using DiscForge.Core.Cdi;
using DiscForge.Core.Create;
using DiscForge.Core.Iso;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

public class Crc32Tests
{
    // Reference values from zlib.crc32 (the IEEE 802.3 algorithm Crc32 implements).
    [Theory]
    [InlineData("123456789", 0xCBF43926u)] // standard CRC-32 check value
    public void Matches_standard_check_value(string ascii, uint expected)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(ascii);
        Assert.Equal(expected, Crc32.Compute(bytes));
    }

    [Fact]
    public void Empty_is_zero() => Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void Matches_zlib_reference_buffers()
    {
        var zeros = new byte[2048];
        Assert.Equal(0xF1E8BA9Eu, Crc32.Compute(zeros));

        var fives = new byte[2048];
        Array.Fill(fives, (byte)0x5A);
        Assert.Equal(0x9CC512C3u, Crc32.Compute(fives));
    }

    [Fact]
    public void Streaming_equals_oneshot()
    {
        var data = new byte[5000];
        new Random(1).NextBytes(data);
        var c = new Crc32();
        c.Update(data.AsSpan(0, 1000));
        c.Update(data.AsSpan(1000, 4000));
        Assert.Equal(Crc32.Compute(data), c.Value);
    }
}

public class CdiVerifierTests
{
    [Fact]
    public void User_data_checksum_streams_and_does_not_buffer_the_track()
    {
        // Regression: the verifier used to extract user data into a MemoryStream
        // to CRC it, which throws "Stream was too long" past 2 GB — i.e. it could
        // not check any DVD-sized image. It must stream instead.
        // (Kept small here for test speed; the point is the code path, not the size.)
        var files = new[] { new IsoBuilder.FileEntry("DATA.BIN", Pattern(400_000, 7)) };
        var ms = new MemoryStream();
        CdiCreator.CreateDataImage("VOL", files, CdiVersion.V35, ms);
        ms.Position = 0;

        var image = CdiParser.Parse(ms);
        var report = CdiVerifier.Verify(ms, image, computeUserChecksums: true);

        Assert.True(report.Passed);
        var c = Assert.Single(report.Checksums);
        Assert.NotNull(c.UserCrc32);          // must be computed, not skipped
        Assert.NotNull(c.UserBytes);
        Assert.True(c.UserBytes > 0);
        Assert.DoesNotContain(report.Issues, i => i.Message.Contains("checksum skipped"));
    }

    private static byte[] Pattern(int len, byte seed)
    {
        var d = new byte[len];
        for (int i = 0; i < len; i++) d[i] = (byte)((i * 13 + seed) & 0xFF);
        return d;
    }

    private static string FixtureDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "synthetic");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("synthetic fixtures not found");
    }

    [Fact]
    public void Valid_image_passes_with_stable_checksum()
    {
        var path = Path.Combine(FixtureDir(), "single_data_v35.cdi");
        using var fs = File.OpenRead(path);
        var image = CdiParser.Parse(fs);

        var report = CdiVerifier.Verify(fs, image, computeUserChecksums: true);

        Assert.True(report.Passed);
        Assert.False(report.HasErrors);
        var t1 = report.Checksums.Single();
        Assert.Equal(512000, t1.StoredBytes);
        Assert.Equal(0x5BE22732u, t1.StoredCrc32);   // cross-checked vs zlib
        Assert.NotNull(t1.UserCrc32);
    }

    [Fact]
    public void All_synthetic_fixtures_pass()
    {
        foreach (var path in Directory.GetFiles(FixtureDir(), "*.cdi"))
        {
            using var fs = File.OpenRead(path);
            var image = CdiParser.Parse(fs);
            var report = CdiVerifier.Verify(fs, image);
            Assert.True(report.Passed, $"{Path.GetFileName(path)} should verify clean");
        }
    }

    [Fact]
    public void Detects_non_contiguous_track_offset()
    {
        // Hand-craft an image model whose second track claims a wrong FileOffset.
        var t1 = new CdiTrack
        {
            Number = 1, SessionIndex = 0, Mode = CdiTrackMode.Mode1,
            SectorSize = CdiSectorSize.S2048, PregapSectors = 0, LengthSectors = 10,
            StartLba = 0, TotalSectors = 10, FileOffset = 0,
        };
        var t2Bad = t1 with { Number = 2, StartLba = 10, FileOffset = 99999 };

        var image = new CdiImage
        {
            Version = CdiVersion.V35, FileLength = 10_000_000,
            DescriptorOffset = 9_000_000,
            Sessions = new[] { new CdiSession { Index = 0, Tracks = new[] { t1, t2Bad } } },
        };

        using var dummy = new MemoryStream(new byte[10_000_000]);
        var report = CdiVerifier.Verify(dummy, image);

        Assert.False(report.Passed);
        Assert.Contains(report.Issues, i =>
            i.Severity == VerifySeverity.Error && i.Message.Contains("file offset"));
    }

    [Fact]
    public void Detects_track_data_past_descriptor()
    {
        var t = new CdiTrack
        {
            Number = 1, SessionIndex = 0, Mode = CdiTrackMode.Mode1,
            SectorSize = CdiSectorSize.S2048, PregapSectors = 0, LengthSectors = 1000,
            StartLba = 0, TotalSectors = 1000, FileOffset = 0,
        };
        var image = new CdiImage
        {
            Version = CdiVersion.V3, FileLength = 100_000, DescriptorOffset = 500, // too small
            Sessions = new[] { new CdiSession { Index = 0, Tracks = new[] { t } } },
        };

        using var dummy = new MemoryStream(new byte[100_000]);
        var report = CdiVerifier.Verify(dummy, image);
        Assert.False(report.Passed);
        Assert.Contains(report.Issues, i => i.Message.Contains("past descriptor"));
    }
}
