// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Dumping;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>DumpSet gathers a dump as a tool left it (redumper's cue + per-track bins + log).</summary>
public class DumpSetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dumpset-" + Guid.NewGuid().ToString("N"));

    public DumpSetTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Touch(string name, string content = "x")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Cue_brings_its_tracks_log_and_sidecars()
    {
        var cue = Touch("game.cue",
            "FILE \"game (Track 1).bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "FILE \"game (Track 2).bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 00 00:00:00\n    INDEX 01 00:02:00\n");
        Touch("game (Track 1).bin"); Touch("game (Track 2).bin");
        Touch("game.log"); Touch("game.sbi");
        Touch("unrelated.txt");

        var set = DumpSet.Resolve(cue);
        var names = set.Files.Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "game.cue", "game (Track 1).bin", "game (Track 2).bin", "game.log", "game.sbi" }, names);
        Assert.Empty(set.Missing);
        Assert.Equal("game.log", Path.GetFileName(set.Log));
    }

    [Fact]
    public void Missing_track_is_reported_not_thrown()
    {
        var cue = Touch("d.cue", "FILE \"d (Track 1).bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
        var set = DumpSet.Resolve(cue);
        Assert.Single(set.Files);
        Assert.Single(set.Missing);
    }

    [Fact]
    public void Lone_track_file_finds_the_dump_log_without_its_track_suffix()
    {
        var bin = Touch("game (Track 1).bin");
        Touch("game.log"); Touch("other.log");
        var set = DumpSet.Resolve(bin);
        Assert.Equal("game.log", Path.GetFileName(set.Log));
        Assert.Equal(2, set.Files.Count);
    }
}
