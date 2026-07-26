// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Gdi;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the Dreamcast .gdi (GD-ROM) index parser and validator.
///
/// The parser turns the plain text table of contents into a track model; the
/// validator checks that model against the files beside it. A real GD-ROM has a
/// distinctive shape — two low-density tracks, then the game in a high-density
/// data track at LBA 45000 — and the interesting checks are the ones that catch
/// a broken or mis-described dump before a patch or browse fails on it.
/// </summary>
public class GdiParserTests
{
    // A typical three-track GD-ROM index: data + audio in the low-density area,
    // then the game data track high on the disc.
    private const string TypicalGdi =
        "3\n" +
        "1 0 4 2352 track01.bin 0\n" +
        "2 600 0 2352 track02.raw 0\n" +
        "3 45000 4 2352 track03.bin 0\n";

    // ---- parsing ------------------------------------------------------------

    [Fact]
    public void A_typical_index_parses_to_three_tracks()
    {
        var disc = GdiParser.Parse(TypicalGdi);

        Assert.Equal(3, disc.Tracks.Count);
        Assert.Equal(new long[] { 0, 600, 45000 }, disc.Tracks.Select(t => t.StartLba).ToArray());
        Assert.Equal(GdiTrackType.Data, disc.Tracks[0].Type);
        Assert.Equal(GdiTrackType.Audio, disc.Tracks[1].Type);
        Assert.Equal(GdiTrackType.Data, disc.Tracks[2].Type);
        Assert.All(disc.Tracks, t => Assert.Equal(2352, t.SectorSize));
    }

    [Fact]
    public void The_boot_data_track_is_the_high_density_data_track()
    {
        var disc = GdiParser.Parse(TypicalGdi);

        Assert.NotNull(disc.BootDataTrack);
        Assert.Equal(3, disc.BootDataTrack!.Number);
        Assert.Equal(45000, disc.BootDataTrack.StartLba);
        Assert.True(disc.BootDataTrack.IsHighDensity);
    }

    [Fact]
    public void Low_density_tracks_are_not_treated_as_high_density()
    {
        var disc = GdiParser.Parse(TypicalGdi);
        Assert.False(disc.Tracks[0].IsHighDensity);
        Assert.False(disc.Tracks[1].IsHighDensity);
    }

    [Fact]
    public void A_quoted_filename_with_spaces_is_read_whole()
    {
        var disc = GdiParser.Parse("1\n1 0 4 2352 \"my game track.bin\" 0\n");
        Assert.Equal("my game track.bin", disc.Tracks[0].FileName);
    }

    [Fact]
    public void Extra_whitespace_between_fields_is_tolerated()
    {
        var disc = GdiParser.Parse("1\n  1    0   4    2352   track.bin   0  \n");
        Assert.Single(disc.Tracks);
        Assert.Equal("track.bin", disc.Tracks[0].FileName);
    }

    [Fact]
    public void An_offset_is_carried_through()
    {
        var disc = GdiParser.Parse("1\n1 0 4 2352 track.bin 1234\n");
        Assert.Equal(1234, disc.Tracks[0].Offset);
    }

    // ---- parse failures -----------------------------------------------------

    [Fact]
    public void An_empty_index_is_refused()
    {
        Assert.Throws<GdiFormatException>(() => GdiParser.Parse(""));
    }

    [Fact]
    public void A_non_numeric_track_count_is_refused()
    {
        var ex = Assert.Throws<GdiFormatException>(() => GdiParser.Parse("lots\n1 0 4 2352 t.bin 0\n"));
        Assert.Contains("track count", ex.Message);
    }

    [Fact]
    public void A_count_that_disagrees_with_the_lines_is_refused()
    {
        var ex = Assert.Throws<GdiFormatException>(
            () => GdiParser.Parse("3\n1 0 4 2352 t.bin 0\n"));
        Assert.Contains("declares 3", ex.Message);
    }

    [Fact]
    public void A_track_line_with_too_few_fields_is_refused()
    {
        Assert.Throws<GdiFormatException>(() => GdiParser.Parse("1\n1 0 4 2352 t.bin\n"));
    }

    [Fact]
    public void An_unknown_track_type_is_refused()
    {
        var ex = Assert.Throws<GdiFormatException>(
            () => GdiParser.Parse("1\n1 0 7 2352 t.bin 0\n"));
        Assert.Contains("neither audio", ex.Message);
    }

    // ---- validation against files ------------------------------------------

    private static string WriteDisc(string dir, string gdi, params (string Name, int Bytes)[] files)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "disc.gdi"), gdi);
        foreach (var (name, bytes) in files)
            File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
        return dir;
    }

    [Fact]
    public void A_complete_and_consistent_image_validates_clean_of_errors()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gdi_ok_" + Guid.NewGuid().ToString("N"));
        WriteDisc(dir, TypicalGdi,
            ("track01.bin", 2352 * 10),
            ("track02.raw", 2352 * 5),
            ("track03.bin", 2352 * 100));
        try
        {
            var disc = GdiParser.Parse(TypicalGdi);
            var report = GdiValidator.Validate(disc, dir);
            Assert.False(report.HasErrors);
            Assert.Contains(report.Issues, i => i.Message.Contains("high-density data track"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_missing_track_file_is_an_error()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gdi_miss_" + Guid.NewGuid().ToString("N"));
        WriteDisc(dir, TypicalGdi,
            ("track01.bin", 2352 * 10),
            ("track02.raw", 2352 * 5));   // track03.bin absent
        try
        {
            var report = GdiValidator.Validate(GdiParser.Parse(TypicalGdi), dir);
            Assert.True(report.HasErrors);
            Assert.Contains(report.Issues, i => i.Level == GdiIssueLevel.Error && i.Message.Contains("track03.bin"));
            Assert.Equal(-1, report.FileSizes["track03.bin"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_track_file_that_is_not_a_whole_number_of_sectors_warns()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gdi_trunc_" + Guid.NewGuid().ToString("N"));
        WriteDisc(dir, TypicalGdi,
            ("track01.bin", 2352 * 10),
            ("track02.raw", 2352 * 5),
            ("track03.bin", 2352 * 100 + 17));   // 17 bytes over a sector boundary
        try
        {
            var report = GdiValidator.Validate(GdiParser.Parse(TypicalGdi), dir);
            Assert.Contains(report.Issues, i => i.Level == GdiIssueLevel.Warning && i.Message.Contains("whole number"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Descending_track_lbas_are_an_error()
    {
        var disc = GdiParser.Parse("2\n1 45000 4 2352 a.bin 0\n2 600 0 2352 b.raw 0\n");
        string dir = Path.Combine(Path.GetTempPath(), "gdi_lba_" + Guid.NewGuid().ToString("N"));
        WriteDisc(dir, "x", ("a.bin", 2352), ("b.raw", 2352));
        try
        {
            var report = GdiValidator.Validate(disc, dir);
            Assert.Contains(report.Issues, i => i.Level == GdiIssueLevel.Error && i.Message.Contains("must ascend"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_image_with_no_high_density_data_track_warns()
    {
        // Only low-density tracks — an odd or partial dump.
        var disc = GdiParser.Parse("2\n1 0 4 2352 a.bin 0\n2 600 0 2352 b.raw 0\n");
        string dir = Path.Combine(Path.GetTempPath(), "gdi_lowonly_" + Guid.NewGuid().ToString("N"));
        WriteDisc(dir, "x", ("a.bin", 2352), ("b.raw", 2352));
        try
        {
            var report = GdiValidator.Validate(disc, dir);
            Assert.Contains(report.Issues, i => i.Level == GdiIssueLevel.Warning && i.Message.Contains("high-density"));
            Assert.Null(disc.BootDataTrack);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // A wrong file dropped on the GDI view must fail cleanly, never echoing its
    // whole content into the message (that once bloated a diagnostic log to 760 KB).

    [Fact]
    public void A_binary_file_is_refused_without_dumping_its_bytes()
    {
        var binary = new string('A', 100) + "\0" + new string('B', 10000);   // NUL early, as real binary has
        var ex = Assert.Throws<GdiFormatException>(() => GdiParser.Parse(binary));
        Assert.Contains("binary", ex.Message);
        Assert.True(ex.Message.Length < 200, $"Error message was {ex.Message.Length} chars — it should be bounded.");
    }

    [Fact]
    public void A_garbage_first_line_is_truncated_in_the_error()
    {
        var junk = new string('x', 100000) + "\nrest\n";     // no NUL, but a huge first line
        var ex = Assert.Throws<GdiFormatException>(() => GdiParser.Parse(junk));
        Assert.Contains("track count", ex.Message);
        Assert.True(ex.Message.Length < 200, $"Error message was {ex.Message.Length} chars — it should be bounded.");
    }
}
