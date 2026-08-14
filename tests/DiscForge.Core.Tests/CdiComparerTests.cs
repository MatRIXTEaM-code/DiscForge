using DiscForge.Core.Cdi;
using DiscForge.Core.Create;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class CdiComparerTests
{
    private static byte[] Pattern(int len, byte seed)
    {
        var d = new byte[len];
        for (int i = 0; i < len; i++) d[i] = (byte)((i * 13 + seed) & 0xFF);
        return d;
    }

    private static MemoryStream MakeCdi(string vol, params (string name, byte[] data)[] files)
    {
        var ms = new MemoryStream();
        CdiCreator.CreateDataImage(vol,
            files.Select(f => new IsoBuilder.FileEntry(f.name, f.data)).ToList(),
            CdiVersion.V35, ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Identical_deterministic_builds_are_equivalent()
    {
        using var a = MakeCdi("VOL", ("A.BIN", Pattern(4000, 1)), ("B.BIN", Pattern(6000, 2)));
        using var b = MakeCdi("VOL", ("A.BIN", Pattern(4000, 1)), ("B.BIN", Pattern(6000, 2)));

        var ia = CdiParser.Parse(a);
        var ib = CdiParser.Parse(b);
        var report = CdiComparer.Compare(a, ia, b, ib);

        Assert.True(report.Equal);
        Assert.Empty(report.StructuralDifferences);
        Assert.Empty(report.TrackDifferences);
        Assert.Empty(report.ContentMismatchTracks);
    }

    [Fact]
    public void Different_content_same_size_flags_crc_mismatch()
    {
        // Same file sizes/structure, different bytes -> identical layout, CRC differs.
        using var a = MakeCdi("VOL", ("DATA.BIN", Pattern(8000, 1)));
        using var b = MakeCdi("VOL", ("DATA.BIN", Pattern(8000, 99)));

        var ia = CdiParser.Parse(a);
        var ib = CdiParser.Parse(b);
        var report = CdiComparer.Compare(a, ia, b, ib);

        Assert.False(report.Equal);
        Assert.Empty(report.StructuralDifferences);
        Assert.NotEmpty(report.ContentMismatchTracks);
    }

    [Fact]
    public void Structure_only_mode_ignores_content()
    {
        using var a = MakeCdi("VOL", ("DATA.BIN", Pattern(8000, 1)));
        using var b = MakeCdi("VOL", ("DATA.BIN", Pattern(8000, 99)));

        var ia = CdiParser.Parse(a);
        var ib = CdiParser.Parse(b);
        var report = CdiComparer.Compare(a, ia, b, ib, compareContent: false);

        // Same structure; content ignored -> equivalent.
        Assert.True(report.Equal);
    }

    [Fact]
    public void Different_version_is_a_structural_difference()
    {
        var files = new[] { new IsoBuilder.FileEntry("F.BIN", Pattern(2048, 5)) };
        using var a = new MemoryStream();
        using var b = new MemoryStream();
        CdiCreator.CreateDataImage("VOL", files, CdiVersion.V3, a);
        CdiCreator.CreateDataImage("VOL", files, CdiVersion.V35, b);
        a.Position = 0; b.Position = 0;

        var ia = CdiParser.Parse(a);
        var ib = CdiParser.Parse(b);
        var report = CdiComparer.Compare(a, ia, b, ib);

        Assert.False(report.Equal);
        Assert.Contains(report.StructuralDifferences, s => s.StartsWith("version"));
    }

    [Fact]
    public void Different_track_metadata_is_reported_per_track()
    {
        // Build two images with different track modes/sizes via CdiWriter directly.
        CdiTrackInputImage(out var a, CdiTrackMode.Mode1, CdiSectorSize.S2048, 50);
        CdiTrackInputImage(out var b, CdiTrackMode.Mode2, CdiSectorSize.S2336, 50);

        var ia = CdiParser.Parse(a);
        var ib = CdiParser.Parse(b);
        var report = CdiComparer.Compare(a, ia, b, ib);

        Assert.False(report.Equal);
        Assert.Contains(report.TrackDifferences, d => d.Field == "mode");
        Assert.Contains(report.TrackDifferences, d => d.Field == "sectorSize");
        a.Dispose(); b.Dispose();
    }

    private static void CdiTrackInputImage(out MemoryStream ms, CdiTrackMode mode, CdiSectorSize size, uint length)
    {
        ms = new MemoryStream();
        var data = new byte[(int)size * length];
        Array.Fill(data, (byte)0x5A);
        var sessions = new IReadOnlyList<CdiWriter.TrackInput>[]
        {
            new[] { new CdiWriter.TrackInput {
                Mode = mode, SectorSize = size, PregapSectors = 0, LengthSectors = length,
                StartLba = 0, Filename = "T.BIN", Data = data } },
        };
        CdiWriter.Write(ms, CdiVersion.V35, sessions);
        ms.Position = 0;
    }
}
