using System.Collections.Generic;
using System.IO;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

public class LibraryWatchTests
{
    private static WatchSnapshot Snap(params WatchEntry[] entries)
        => new() { Entries = new List<WatchEntry>(entries) };

    private static WatchEntry E(string path, string sha, long mtime = 100, long len = 10)
        => new() { Path = path, Sha256 = sha, MTimeTicks = mtime, Length = len };

    [Fact]
    public void No_changes_reports_everything_unchanged()
    {
        var a = Snap(E("a.bin", "aaa"), E("b.bin", "bbb"));
        var r = LibraryWatch.Compare(a, Snap(E("a.bin", "aaa"), E("b.bin", "bbb")));
        Assert.Equal(2, r.Unchanged);
        Assert.False(r.AnyChange);
        Assert.False(r.RotDetected);
    }

    [Fact]
    public void Content_changed_without_a_timestamp_move_is_flagged_as_rot()
    {
        var before = Snap(E("game.bin", "goodhash", mtime: 500));
        var after = Snap(E("game.bin", "CORRUPT", mtime: 500));   // same mtime, different content
        var r = LibraryWatch.Compare(before, after);
        Assert.Equal(1, r.SuspectedRot);
        Assert.True(r.RotDetected);
        Assert.Contains(r.Changes, c => c.Kind == DriftKind.SuspectedRot && c.Path == "game.bin");
    }

    [Fact]
    public void Content_and_timestamp_both_changed_is_an_edit_not_rot()
    {
        var before = Snap(E("save.bin", "v1", mtime: 500));
        var after = Snap(E("save.bin", "v2", mtime: 900));         // timestamp moved -> intentional
        var r = LibraryWatch.Compare(before, after);
        Assert.Equal(1, r.Modified);
        Assert.Equal(0, r.SuspectedRot);
        Assert.False(r.RotDetected);
    }

    [Fact]
    public void Added_and_removed_files_are_detected()
    {
        var before = Snap(E("a.bin", "aaa"));
        var after = Snap(E("b.bin", "bbb"));
        var r = LibraryWatch.Compare(before, after);
        Assert.Equal(1, r.Added);
        Assert.Equal(1, r.Removed);
        Assert.Contains(r.Changes, c => c.Kind == DriftKind.Added && c.Path == "b.bin");
        Assert.Contains(r.Changes, c => c.Kind == DriftKind.Removed && c.Path == "a.bin");
    }

    [Fact]
    public void Scan_then_corrupt_a_byte_without_touching_mtime_is_caught_end_to_end()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dfwatch_" + System.Threading.Interlocked.Increment(ref _counter));
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "disc.bin");
            File.WriteAllBytes(f, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var mtime = File.GetLastWriteTimeUtc(f);

            var baseline = LibraryWatch.ScanDirectory(dir, null);

            // Flip a byte, then reset the timestamp to mimic silent block-level corruption.
            var bytes = File.ReadAllBytes(f);
            bytes[3] ^= 0xFF;
            File.WriteAllBytes(f, bytes);
            File.SetLastWriteTimeUtc(f, mtime);

            var now = LibraryWatch.ScanDirectory(dir, null);
            var r = LibraryWatch.Compare(baseline, now);
            Assert.True(r.RotDetected);
            Assert.Equal(1, r.SuspectedRot);
        }
        finally { Directory.Delete(dir, true); }
    }
    private static int _counter;

    [Fact]
    public void Snapshot_survives_a_json_round_trip()
    {
        var s = Snap(E("a.bin", "aaa", mtime: 42, len: 7));
        var back = LibraryWatch.FromJson(LibraryWatch.ToJson(s));
        Assert.Single(back.Entries);
        Assert.Equal("aaa", back.Entries[0].Sha256);
        Assert.Equal(42, back.Entries[0].MTimeTicks);
    }
}
