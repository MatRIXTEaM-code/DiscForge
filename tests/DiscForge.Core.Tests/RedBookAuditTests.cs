using DiscForge.Core.Cue;
using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class RedBookAuditTests
{
    private static CueIndex Idx(int n, long sectors) => new(n, Msf.FromSectors(sectors));

    private static CueTrack Track(int number, CueTrackType type, long startSector,
        long? pregapStart = null, string? isrc = null)
    {
        var indices = new List<CueIndex>();
        if (pregapStart != null) indices.Add(Idx(0, pregapStart.Value));
        indices.Add(Idx(1, startSector));
        return new CueTrack
        {
            Number = number,
            Type = type,
            File = "disc.bin",
            Indices = indices,
            Isrc = isrc,
        };
    }

    private static CueSheet Sheet(IEnumerable<CueTrack> tracks, string? catalog = null)
        => new() { Tracks = tracks.ToList(), Catalog = catalog };

    [Fact]
    public void A_clean_audio_disc_passes()
    {
        var cue = Sheet(new[]
        {
            Track(1, CueTrackType.Audio, 0),
            Track(2, CueTrackType.Audio, 20_000),
            Track(3, CueTrackType.Audio, 40_000),
        });
        var r = RedBookAudit.Check(cue, new[] { 20_000, 20_000, 20_000 });
        Assert.True(r.Ok, RedBookAudit.Render(r));
        Assert.Equal(0, r.Errors);
    }

    [Fact]
    public void Non_sequential_track_numbers_are_an_error()
    {
        var cue = Sheet(new[]
        {
            Track(1, CueTrackType.Audio, 0),
            Track(3, CueTrackType.Audio, 20_000),   // skips 2
        });
        var r = RedBookAudit.Check(cue);
        Assert.False(r.Ok);
        Assert.Contains(r.Findings, x => x.Severity == LintSeverity.Error && x.Where == "track 3");
    }

    [Fact]
    public void A_too_short_track_is_flagged()
    {
        var cue = Sheet(new[] { Track(1, CueTrackType.Audio, 0) });
        var r = RedBookAudit.Check(cue, new[] { 100 });   // < 300 sectors
        Assert.False(r.Ok);
        Assert.Contains(r.Findings, x => x.Message.Contains("Red Book minimum"));
    }

    [Fact]
    public void A_track_without_index_01_is_an_error()
    {
        var bad = new CueTrack
        {
            Number = 1,
            Type = CueTrackType.Audio,
            File = "disc.bin",
            Indices = new[] { Idx(0, 0) },   // only pregap, no INDEX 01
        };
        var r = RedBookAudit.Check(Sheet(new[] { bad }));
        Assert.Contains(r.Findings, x => x.Message.Contains("no INDEX 01"));
    }

    [Fact]
    public void Data_track_first_is_accepted_mixed_mode()
    {
        var cue = Sheet(new[]
        {
            Track(1, CueTrackType.Mode2_2352, 0),
            Track(2, CueTrackType.Audio, 30_000),
            Track(3, CueTrackType.Audio, 50_000),
        });
        var r = RedBookAudit.Check(cue);
        Assert.DoesNotContain(r.Findings, x => x.Message.Contains("mixed-mode"));
    }

    [Fact]
    public void Data_track_between_audio_is_a_warning()
    {
        var cue = Sheet(new[]
        {
            Track(1, CueTrackType.Audio, 0),
            Track(2, CueTrackType.Mode1_2352, 30_000),  // data wedged after audio, same session
            Track(3, CueTrackType.Audio, 60_000),
        });
        var r = RedBookAudit.Check(cue);
        Assert.Contains(r.Findings, x => x.Severity == LintSeverity.Warning && x.Message.Contains("same session"));
    }

    [Fact]
    public void Cd_extra_data_in_a_later_session_is_not_flagged()
    {
        var t3 = new CueTrack
        {
            Number = 3, Type = CueTrackType.Mode2_2352, File = "disc.bin",
            Indices = new[] { Idx(1, 60_000) }, Session = 2,
        };
        var cue = Sheet(new[]
        {
            Track(1, CueTrackType.Audio, 0),
            Track(2, CueTrackType.Audio, 30_000),
            t3,
        });
        var r = RedBookAudit.Check(cue);
        Assert.DoesNotContain(r.Findings, x => x.Message.Contains("same session"));
    }

    [Fact]
    public void A_bad_mcn_is_a_warning()
    {
        var cue = Sheet(new[] { Track(1, CueTrackType.Audio, 0) }, catalog: "12345");   // not 13 digits
        var r = RedBookAudit.Check(cue);
        Assert.Contains(r.Findings, x => x.Message.Contains("media catalogue number"));
    }

    [Fact]
    public void A_valid_mcn_passes()
    {
        Assert.True(RedBookAudit.IsValidMcn("0123456789012"));
        Assert.False(RedBookAudit.IsValidMcn("012345678901"));   // 12 digits
        Assert.False(RedBookAudit.IsValidMcn("01234567890AB"));  // non-digit
    }

    [Fact]
    public void Isrc_grammar_is_checked()
    {
        Assert.True(RedBookAudit.IsValidIsrc("USRC17607839"));
        Assert.False(RedBookAudit.IsValidIsrc("USRC1760783"));    // 11 chars
        Assert.False(RedBookAudit.IsValidIsrc("12RC17607839"));   // digits where country letters go
        var cue = Sheet(new[] { Track(1, CueTrackType.Audio, 0, isrc: "BADISRC") });
        var r = RedBookAudit.Check(cue);
        Assert.Contains(r.Findings, x => x.Message.Contains("ISRC"));
    }

    [Fact]
    public void A_short_index0_pause_is_a_warning()
    {
        // INDEX 00 at 19950, INDEX 01 at 20000 → 50-sector pause (< 150).
        var cue = Sheet(new[]
        {
            Track(1, CueTrackType.Audio, 0),
            Track(2, CueTrackType.Audio, 20_000, pregapStart: 19_950),
        });
        var r = RedBookAudit.Check(cue);
        Assert.Contains(r.Findings, x => x.Message.Contains("pause") && x.Severity == LintSeverity.Warning);
    }

    [Fact]
    public void An_empty_disc_is_an_error()
    {
        var r = RedBookAudit.Check(Sheet(Array.Empty<CueTrack>()));
        Assert.False(r.Ok);
        Assert.Contains(r.Findings, x => x.Message.Contains("at least one"));
    }
}
