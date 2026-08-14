using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class HiddenSessionArchaeologyTests
{
    // Convenience: place a set of tagged tracks and analyze them.
    private static SessionArchaeologyReport Analyze(params SessionTrackInput[] tracks) =>
        HiddenSessionArchaeology.Analyze(HiddenSessionArchaeology.Place(tracks));

    [Fact]
    public void A_plain_audio_disc_has_no_hidden_session()
    {
        var r = Analyze(
            new SessionTrackInput(1, 1, false, null, 10000),
            new SessionTrackInput(2, 1, false, null, 12000),
            new SessionTrackInput(3, 1, false, null, 9000));

        Assert.False(r.Multisession);
        Assert.False(r.HasHiddenData);
        Assert.Single(r.Sessions);
        Assert.Equal(SessionKind.Audio, r.Sessions[0].Kind);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void A_single_session_data_disc_is_not_hidden()
    {
        var r = Analyze(new SessionTrackInput(1, 1, true, 1, 300000));
        Assert.Equal(SessionKind.Data, r.Sessions[0].Kind);
        Assert.False(r.HasHiddenData);
    }

    [Fact]
    public void A_cd_extra_disc_flags_the_second_session_data_as_hidden()
    {
        // Session 1: two audio tracks. Session 2: a Mode 2 data track hiding behind them.
        var r = Analyze(
            new SessionTrackInput(1, 1, false, null, 15000),
            new SessionTrackInput(2, 1, false, null, 18000),
            new SessionTrackInput(3, 2, true, 2, 40000));

        Assert.True(r.Multisession);
        Assert.True(r.HasHiddenData);

        var s2 = r.Sessions.Single(s => s.Number == 2);
        Assert.True(s2.Hidden);
        Assert.Equal(SessionKind.Data, s2.Kind);
        Assert.Equal(2, s2.DataMode);
        Assert.Contains(r.Findings, f => f.Contains("CD Extra"));
        Assert.Contains(r.Findings, f => f.Contains("Session 2"));
    }

    [Fact]
    public void The_inter_session_gap_is_the_standard_lead_out_lead_in()
    {
        var placed = HiddenSessionArchaeology.Place(new[]
        {
            new SessionTrackInput(1, 1, false, null, 15000),
            new SessionTrackInput(2, 2, true, 1, 40000),
        });
        var r = HiddenSessionArchaeology.Analyze(placed);

        Assert.Single(r.InterSessionGaps);
        Assert.Equal(HiddenSessionArchaeology.StandardMultisessionGap, r.InterSessionGaps[0]);

        // Session 2's data starts after session 1's end plus the gap.
        var s1 = r.Sessions[0];
        var s2 = r.Sessions[1];
        Assert.Equal(s1.EndLba + 1 + HiddenSessionArchaeology.StandardMultisessionGap, s2.StartLba);
    }

    [Fact]
    public void A_mixed_mode_first_session_is_not_hidden_but_a_later_data_session_is()
    {
        // Session 1: a data track then audio (mixed mode). Session 2: more data (hidden).
        var r = Analyze(
            new SessionTrackInput(1, 1, true, 1, 50000),
            new SessionTrackInput(2, 1, false, null, 12000),
            new SessionTrackInput(3, 2, true, 2, 30000));

        var s1 = r.Sessions[0];
        Assert.Equal(SessionKind.Mixed, s1.Kind);
        Assert.False(s1.Hidden);                       // data in the FIRST session is not hidden

        var s2 = r.Sessions[1];
        Assert.True(s2.Hidden);
        Assert.Contains(r.Findings, f => f.Contains("2 separate data sessions"));
    }

    [Fact]
    public void Three_sessions_with_two_hidden_data_sessions()
    {
        var r = Analyze(
            new SessionTrackInput(1, 1, false, null, 20000),   // audio
            new SessionTrackInput(2, 2, true, 1, 30000),       // hidden data
            new SessionTrackInput(3, 3, true, 2, 25000));      // another hidden data

        Assert.Equal(3, r.Sessions.Count);
        Assert.Equal(2, r.Sessions.Count(s => s.Hidden));
        Assert.Equal(2, r.InterSessionGaps.Count);
    }

    [Fact]
    public void Empty_input_is_handled()
    {
        var r = HiddenSessionArchaeology.Analyze(System.Array.Empty<SessionTrack>());
        Assert.Empty(r.Sessions);
        Assert.False(r.HasHiddenData);
        Assert.Contains("nothing to map", r.Summary());
    }

    [Fact]
    public void Render_marks_the_hidden_session()
    {
        var r = Analyze(
            new SessionTrackInput(1, 1, false, null, 15000),
            new SessionTrackInput(2, 2, true, 2, 40000));
        string text = HiddenSessionArchaeology.Render(r);
        Assert.Contains("HIDDEN", text);
        Assert.Contains("Session 2", text);
    }
}
