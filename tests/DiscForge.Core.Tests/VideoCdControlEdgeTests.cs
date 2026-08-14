using System;
using System.Collections.Generic;
using System.Text;
using DiscForge.Core.VideoCd;
using Xunit;

namespace DiscForge.Core.Tests;

public class VideoCdControlEdgeTests
{
    [Fact]
    public void Album_id_is_truncated_to_16_chars()
    {
        var s = VideoCdControl.BuildInfo(new VideoCdInfoPlan { AlbumId = new string('X', 40) });
        var parsed = VideoCdControl.ReadInfo(s);
        Assert.Equal(16, parsed.AlbumId.Length);
    }

    [Fact]
    public void Unsupported_version_is_rejected()
    {
        bool threw = false;
        try { VideoCdControl.BuildInfo(new VideoCdInfoPlan { Version = 3 }); } catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void Too_many_entries_are_rejected()
    {
        var many = new List<VideoCdEntry>();
        for (int i = 0; i < 501; i++) many.Add(new VideoCdEntry { TrackNumber = 1, Minute = 0, Second = 0, Frame = 0 });
        bool threw = false;
        try { VideoCdControl.BuildEntries(many); } catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void Out_of_range_track_number_is_rejected()
    {
        bool threw = false;
        try { VideoCdControl.BuildEntries(new List<VideoCdEntry> { new() { TrackNumber = 100, Minute = 0, Second = 0, Frame = 0 } }); }
        catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void Svcd_entries_use_the_svcd_id()
    {
        var s = VideoCdControl.BuildEntries(
            new List<VideoCdEntry> { new() { TrackNumber = 1, Minute = 0, Second = 2, Frame = 0 } }, superVcd: true);
        Assert.Equal("ENTRYSVD", Encoding.ASCII.GetString(s, 0, 8));
    }

    [Fact]
    public void Reading_a_too_short_info_buffer_throws()
    {
        bool threw = false;
        try { VideoCdControl.ReadInfo(new byte[100]); } catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }
}
