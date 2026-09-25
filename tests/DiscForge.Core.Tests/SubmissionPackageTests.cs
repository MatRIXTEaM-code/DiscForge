// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cue;
using DiscForge.Core.Dat;
using DiscForge.Core.Redump;
using Xunit;

namespace DiscForge.Core.Tests;

public class SubmissionPackageTests
{
    private static SubmissionInfo SingleTrack() => new()
    {
        FileName = "Game.iso",
        InputFormat = "ISO",
        Tracks = new[]
        {
            new TrackSubmission
            {
                Number = 1, Type = CueTrackType.Mode1_2048, Size = 53248, Sectors = 26,
                Crc32 = "aabbccdd", Md5 = "0123456789abcdef0123456789abcdef",
                Sha1 = "0123456789abcdef0123456789abcdef01234567",
            },
        },
        TotalSize = 53248,
        CombinedCrc32 = "aabbccdd",
        CombinedMd5 = "0123456789abcdef0123456789abcdef",
        CombinedSha1 = "0123456789abcdef0123456789abcdef01234567",
        Cuesheet = "FILE \"Game.iso\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n",
    };

    [Fact]
    public void Bundle_dat_verifies_the_whole_image_hashes()
    {
        var art = SubmissionPackage.Build(SingleTrack(), "My Game");
        var dat = DatFile.ParseText(art.Dat);

        var m = dat.Verify(53248, "aabbccdd", "0123456789abcdef0123456789abcdef01234567", null);
        Assert.True(m.Verified);
        Assert.Equal("My Game", m.Rom!.Game);
    }

    [Fact]
    public void Bundle_carries_the_info_text_and_cuesheet()
    {
        var art = SubmissionPackage.Build(SingleTrack(), "My Game");
        Assert.False(string.IsNullOrWhiteSpace(art.InfoText));
        Assert.Contains("aabbccdd".ToUpperInvariant(), art.InfoText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(art.Cuesheet);
        Assert.Contains("TRACK 01", art.Cuesheet!);
    }

    [Fact]
    public void A_multitrack_disc_catalogues_each_track_in_the_dat()
    {
        var info = SingleTrack() with
        {
            Tracks = new[]
            {
                new TrackSubmission { Number = 1, Type = CueTrackType.Mode1_2352, Size = 100, Sectors = 1, Crc32 = "11111111", Md5 = "a", Sha1 = "b" },
                new TrackSubmission { Number = 2, Type = CueTrackType.Audio, Size = 200, Sectors = 1, Crc32 = "22222222", Md5 = "c", Sha1 = "d" },
            },
        };
        var art = SubmissionPackage.Build(info, "Multi");
        Assert.Equal(2, art.TrackRoms.Count);
        var dat = DatFile.ParseText(art.Dat);
        // The whole image plus both tracks appear as roms.
        Assert.True(dat.Verify(100, "11111111", null, null).Verified);
        Assert.True(dat.Verify(200, "22222222", null, null).Verified);
    }
}
