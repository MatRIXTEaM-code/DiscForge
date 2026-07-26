// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdi;
using DiscForge.Core.Convert;
using DiscForge.Core.Cue;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Session-aware BIN/CUE → CDI: a Redump-style Dreamcast MIL-CD rip is a two-session
/// disc, its second session marked with "REM SESSION 02". These check that the cue
/// parser captures those markers and that the converter emits a real two-session CDI
/// (correct track grouping, and session-2 LBAs stepped across the inter-session gap),
/// instead of collapsing everything into one session as the old converter did.
/// </summary>
public class DreamcastSessionCdiTests
{
    private const int Sector = 2352;

    [Fact]
    public void CueParser_CapturesRemSessionMarkers()
    {
        var cue =
            "REM SESSION 01\n" +
            "FILE \"t1.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n" +
            "FILE \"t2.bin\" BINARY\n  TRACK 02 MODE1/2352\n    INDEX 01 00:00:00\n" +
            "REM SESSION 02\n" +
            "FILE \"t3.bin\" BINARY\n  TRACK 03 MODE1/2352\n    INDEX 01 00:00:00\n";

        var sheet = CueSheet.Parse(cue);
        Assert.Equal(new[] { 1, 1, 2 }, sheet.Tracks.Select(t => t.Session).ToArray());
    }

    [Fact]
    public void NoSessionMarkers_DefaultToSessionOne()
    {
        var cue = "FILE \"a.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n";
        var sheet = CueSheet.Parse(cue);
        Assert.All(sheet.Tracks, t => Assert.Equal(1, t.Session));
    }

    [Fact]
    public void BinCueToCdi_TwoSessions_GroupsTracksAndStepsLbaAcrossTheGap()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_dc_").FullName;
        try
        {
            WriteSectors(dir, "t1.bin", 10);   // session 1, audio
            WriteSectors(dir, "t2.bin", 20);   // session 1, data
            WriteSectors(dir, "t3.bin", 30);   // session 2, high-density game

            var cue =
                "REM SESSION 01\n" +
                "FILE \"t1.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n" +
                "FILE \"t2.bin\" BINARY\n  TRACK 02 MODE1/2352\n    INDEX 01 00:00:00\n" +
                "REM SESSION 02\n" +
                "FILE \"t3.bin\" BINARY\n  TRACK 03 MODE1/2352\n    INDEX 01 00:00:00\n";

            using var ms = new MemoryStream();
            CdiConverter.BinCueToCdi(cue, dir, CdiVersion.V2, ms);
            ms.Position = 0;
            var image = CdiParser.Parse(ms);

            Assert.Equal(2, image.Sessions.Count);
            Assert.Equal(new[] { 1, 2 }, image.Sessions[0].Tracks.Select(t => t.Number).ToArray());
            Assert.Equal(new[] { 3 }, image.Sessions[1].Tracks.Select(t => t.Number).ToArray());

            var t1 = image.Sessions[0].Tracks[0];
            var t2 = image.Sessions[0].Tracks[1];
            var t3 = image.Sessions[1].Tracks[0];
            Assert.Equal(0u, t1.StartLba);
            Assert.Equal(10u, t2.StartLba);
            // Session 1 spans 30 sectors; session 2 begins after the inter-session gap.
            Assert.Equal(30u + CdiConverter.MultisessionGap, t3.StartLba);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BinCueToCdi_SingleSession_UnchangedBehaviour()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_dc_").FullName;
        try
        {
            WriteSectors(dir, "a.bin", 12);
            WriteSectors(dir, "b.bin", 8);
            var cue =
                "FILE \"a.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
                "FILE \"b.bin\" BINARY\n  TRACK 02 MODE1/2352\n    INDEX 01 00:00:00\n";

            using var ms = new MemoryStream();
            CdiConverter.BinCueToCdi(cue, dir, CdiVersion.V2, ms);
            ms.Position = 0;
            var image = CdiParser.Parse(ms);

            Assert.Single(image.Sessions);
            Assert.Equal(2, image.Sessions[0].Tracks.Count);
            Assert.Equal(0u, image.Sessions[0].Tracks[0].StartLba);
            Assert.Equal(12u, image.Sessions[0].Tracks[1].StartLba);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void WriteSectors(string dir, string name, int sectors)
    {
        var data = new byte[sectors * Sector];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + 1);
        File.WriteAllBytes(Path.Combine(dir, name), data);
    }
}
