// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cue;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the cuesheet validator.
///
/// What matters here is the direction of the errors. A false alarm is a
/// nuisance; a false clean bill of health costs a disc, because the user burns
/// on the strength of it and finds out afterwards. So the tests lean on
/// confirming that genuine faults are caught rather than that clean sheets pass
/// — though both are checked.
/// </summary>
public class CueValidatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose()
    {
        // Tolerant teardown: a file still held open would otherwise fail a test
        // whose assertions all passed, and an intermittent red build teaches
        // people to ignore the suite.
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Write a BIN of the given sector count so the arithmetic has
    /// something real to check against.</summary>
    private string MakeBin(string name, long sectors, int sectorSize = 2352)
    {
        string path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        fs.SetLength(sectors * sectorSize);
        return path;
    }

    private CueValidation Validate(string cueText) =>
        CueValidator.Validate(CueSheet.Parse(cueText), _dir);

    private static bool HasError(CueValidation v, string fragment) =>
        v.Issues.Any(i => i.Level == CueIssueLevel.Error &&
                          i.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool HasWarning(CueValidation v, string fragment) =>
        v.Issues.Any(i => i.Level == CueIssueLevel.Warning &&
                          i.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    // --- the happy path ------------------------------------------------------

    [Fact]
    public void A_sheet_matching_its_file_passes()
    {
        MakeBin("audio.bin", 1000);

        var v = Validate("""
            FILE "audio.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
            """);

        Assert.False(v.HasErrors);
    }

    [Fact]
    public void Several_tracks_in_one_file_pass()
    {
        MakeBin("album.bin", 3000);

        var v = Validate("""
            FILE "album.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 01 00:13:25
              TRACK 03 AUDIO
                INDEX 01 00:26:50
            """);

        Assert.False(v.HasErrors);
    }

    // --- the file itself -----------------------------------------------------

    [Fact]
    public void A_missing_data_file_is_an_error()
    {
        // The commonest broken cuesheet by far: the sheet was copied and the BIN
        // was not, or one of them was renamed.
        var v = Validate("""
            FILE "nowhere.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
            """);

        Assert.True(HasError(v, "not beside the sheet"));
        Assert.Equal(-1, v.FileSizes["nowhere.bin"]);
    }

    [Fact]
    public void A_file_that_is_not_whole_sectors_is_flagged()
    {
        // A truncated BIN, or a track type whose sector size doesn't match.
        string path = Path.Combine(_dir, "odd.bin");
        using (var fs = File.Create(path)) fs.SetLength(2352 * 100 + 17);

        var v = Validate("""
            FILE "odd.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
            """);

        Assert.True(HasWarning(v, "whole number"));
    }

    [Fact]
    public void An_index_past_the_end_of_the_file_is_an_error()
    {
        // The sheet describes more disc than exists — burning it would run off
        // the end of the data.
        MakeBin("short.bin", 100);

        var v = Validate("""
            FILE "short.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 01 00:10:00
            """);

        Assert.True(HasError(v, "holds only"));
    }

    [Fact]
    public void A_final_track_with_nothing_after_it_is_an_error()
    {
        MakeBin("exact.bin", 750);      // 750 sectors = 00:10:00

        var v = Validate("""
            FILE "exact.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 01 00:10:00
            """);

        Assert.True(HasError(v, "no content"));
    }

    [Fact]
    public void A_suspiciously_short_final_track_is_warned_about()
    {
        MakeBin("nearly.bin", 800);     // last track gets 50 sectors

        var v = Validate("""
            FILE "nearly.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 01 00:10:00
            """);

        Assert.True(HasWarning(v, "very short"));
    }

    // --- indexes -------------------------------------------------------------

    [Fact]
    public void A_track_without_index_01_is_an_error()
    {
        MakeBin("a.bin", 1000);

        var v = Validate("""
            FILE "a.bin" BINARY
              TRACK 01 AUDIO
                INDEX 00 00:00:00
            """);

        Assert.True(HasError(v, "INDEX 01"));
    }

    [Fact]
    public void A_pregap_after_the_track_it_precedes_is_an_error()
    {
        MakeBin("a.bin", 1000);

        var v = Validate("""
            FILE "a.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 00 00:10:00
                INDEX 01 00:05:00
            """);

        Assert.True(HasError(v, "cannot start after"));
    }

    [Fact]
    public void Indexes_that_do_not_ascend_are_an_error()
    {
        MakeBin("a.bin", 2000);

        var v = Validate("""
            FILE "a.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:05:00
                INDEX 02 00:03:00
            """);

        Assert.True(HasError(v, "ascend"));
    }

    // --- numbering -----------------------------------------------------------

    [Fact]
    public void Duplicate_track_numbers_are_an_error()
    {
        MakeBin("a.bin", 2000);

        var v = Validate("""
            FILE "a.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 01 AUDIO
                INDEX 01 00:10:00
            """);

        Assert.True(HasError(v, "share a number"));
    }

    [Fact]
    public void Numbering_that_does_not_start_at_one_is_warned_about()
    {
        MakeBin("a.bin", 1000);

        var v = Validate("""
            FILE "a.bin" BINARY
              TRACK 05 AUDIO
                INDEX 01 00:00:00
            """);

        Assert.True(HasWarning(v, "rather than 1"));
        Assert.False(v.HasErrors);      // legal, just unusual
    }

    [Fact]
    public void A_gap_in_track_numbering_is_warned_about()
    {
        MakeBin("a.bin", 2000);

        var v = Validate("""
            FILE "a.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 03 AUDIO
                INDEX 01 00:10:00
            """);

        Assert.True(HasWarning(v, "jump"));
    }

    // --- track types ---------------------------------------------------------

    [Fact]
    public void Mixed_sector_sizes_in_one_file_are_an_error()
    {
        // Every offset after the change would be computed from the wrong sector
        // size, so nothing beyond the first track would land where the sheet
        // says it does.
        MakeBin("mixed.bin", 2000);

        var v = Validate("""
            FILE "mixed.bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
              TRACK 02 MODE1/2048
                INDEX 01 00:10:00
            """);

        Assert.True(HasError(v, "cannot hold both"));
    }

    [Fact]
    public void Audio_flags_on_a_data_track_are_warned_about()
    {
        MakeBin("data.bin", 1000, sectorSize: 2352);

        var v = Validate("""
            FILE "data.bin" BINARY
              TRACK 01 MODE1/2352
                FLAGS PRE
                INDEX 01 00:00:00
            """);

        Assert.True(HasWarning(v, "audio flags"));
    }

    [Fact]
    public void Cooked_mode_1_is_noted_as_needing_regeneration()
    {
        MakeBin("cooked.bin", 1000, sectorSize: 2048);

        var v = Validate("""
            FILE "cooked.bin" BINARY
              TRACK 01 MODE1/2048
                INDEX 01 00:00:00
            """);

        Assert.Contains(v.Issues, i => i.Level == CueIssueLevel.Info &&
                                       i.Message.Contains("regenerated"));
    }

    // --- metadata ------------------------------------------------------------

    [Fact]
    public void A_malformed_isrc_is_warned_about()
    {
        MakeBin("a.bin", 1000);

        var v = Validate("""
            FILE "a.bin" BINARY
              TRACK 01 AUDIO
                ISRC TOOSHORT
                INDEX 01 00:00:00
            """);

        Assert.True(HasWarning(v, "ISRC"));
    }

    [Fact]
    public void A_malformed_catalog_is_warned_about()
    {
        MakeBin("a.bin", 1000);

        var v = Validate("""
            CATALOG 12345
            FILE "a.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
            """);

        Assert.True(HasWarning(v, "13"));
    }

    // --- edges ---------------------------------------------------------------

    [Fact]
    public void An_empty_sheet_is_an_error_rather_than_a_crash()
    {
        var v = CueValidator.Validate(
            new CueSheet { Tracks = Array.Empty<CueTrack>() }, _dir);

        Assert.True(HasError(v, "no tracks"));
    }

    [Fact]
    public void A_clean_sheet_reports_clean()
    {
        MakeBin("clean.bin", 5000);

        var v = Validate("""
            FILE "clean.bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            """);

        Assert.False(v.HasErrors);
        // MODE1/2352 on a data track produces no warnings at all — the pregap
        // note only applies to audio track 1.
        Assert.True(v.Clean || v.Issues.All(i => i.Level == CueIssueLevel.Info));
    }

    [Fact]
    public void The_file_size_is_reported_so_the_user_can_check_it()
    {
        MakeBin("sized.bin", 1000);

        var v = Validate("""
            FILE "sized.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
            """);

        Assert.Equal(1000L * 2352, v.FileSizes["sized.bin"]);
    }
}