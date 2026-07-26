using System;
using System.Collections.Generic;
using System.Text;
using DiscForge.Core.VideoCd;
using Xunit;

namespace DiscForge.Core.Tests;

public class VideoCdControlTests
{
    [Fact]
    public void Info_sector_is_2048_bytes_and_starts_with_the_id()
    {
        var s = VideoCdControl.BuildInfo(new VideoCdInfoPlan { AlbumId = "MY ALBUM" });
        Assert.Equal(2048, s.Length);
        Assert.Equal("VIDEO_CD", Encoding.ASCII.GetString(s, 0, 8));
        Assert.Equal(0x02, s[0x08]);
        Assert.Equal(8, s[0x34]);                 // offset multiplier
    }

    [Fact]
    public void Info_round_trips_identity_fields()
    {
        var plan = new VideoCdInfoPlan { Version = 2, AlbumId = "PRESERVED", VolumeCount = 3, VolumeNumber = 2 };
        var parsed = VideoCdControl.ReadInfo(VideoCdControl.BuildInfo(plan));
        Assert.Equal("VIDEO_CD", parsed.Id);
        Assert.Equal(2, parsed.Version);
        Assert.Equal("PRESERVED", parsed.AlbumId);
        Assert.Equal(3, parsed.VolumeCount);
        Assert.Equal(2, parsed.VolumeNumber);
    }

    [Fact]
    public void Supervcd_uses_a_different_id_string()
    {
        var s = VideoCdControl.BuildInfo(new VideoCdInfoPlan { SuperVcd = true });
        Assert.Equal("SUPERVCD", Encoding.ASCII.GetString(s, 0, 8));
    }

    [Fact]
    public void Entries_round_trip_track_and_msf_in_bcd()
    {
        var entries = new List<VideoCdEntry>
        {
            new() { TrackNumber = 1, Minute = 0, Second = 2, Frame = 0 },
            new() { TrackNumber = 2, Minute = 3, Second = 40, Frame = 25 },
        };
        var sector = VideoCdControl.BuildEntries(entries);
        Assert.Equal(2048, sector.Length);
        Assert.Equal("ENTRYVCD", Encoding.ASCII.GetString(sector, 0, 8));

        var read = VideoCdControl.ReadEntries(sector);
        Assert.Equal(2, read.Count);
        Assert.Equal(2, read[1].TrackNumber);
        Assert.Equal(3, read[1].Minute);
        Assert.Equal(40, read[1].Second);
        Assert.Equal(25, read[1].Frame);
    }

    [Fact]
    public void Entries_are_stored_as_bcd_bytes()
    {
        var entries = new List<VideoCdEntry> { new() { TrackNumber = 12, Minute = 34, Second = 56, Frame = 74 } };
        var s = VideoCdControl.BuildEntries(entries);
        Assert.Equal(0x12, s[0x0C]);
        Assert.Equal(0x34, s[0x0D]);
        Assert.Equal(0x56, s[0x0E]);
        Assert.Equal(0x74, s[0x0F]);
    }

    [Fact]
    public void Empty_entry_list_is_rejected()
    {
        bool threw = false;
        try { VideoCdControl.BuildEntries(new List<VideoCdEntry>()); }
        catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }
}
