// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cue;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// bin/cue merge and split — the binmerge job. The guarantee that matters is a
/// perfect round trip: split(merge(x)) returns the original per-track bytes, and
/// the cue arithmetic (absolute vs file-relative INDEX times) is exact both ways.
/// </summary>
public class BinCueMergeTests
{
    private const int Sz = BinCueMerge.RawSectorSize;

    private static byte[] Pattern(int sectors, byte seed)
    {
        var b = new byte[sectors * Sz];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(seed + i);
        return b;
    }

    private sealed class Temp : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "df-bincue-" + Guid.NewGuid().ToString("N")[..8]);
        public Temp() => Directory.CreateDirectory(Dir);
        public string P(string name) => Path.Combine(Dir, name);
        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public void Merge_ThenSplit_RoundTripsBytesAndCue()
    {
        using var t = new Temp();

        var t1 = Pattern(100, 0x10);   // data track
        var t2 = Pattern(80, 0x40);    // audio, with a 2-sector embedded pregap
        var t3 = Pattern(60, 0x90);    // audio

        File.WriteAllBytes(t.P("t1.bin"), t1);
        File.WriteAllBytes(t.P("t2.bin"), t2);
        File.WriteAllBytes(t.P("t3.bin"), t3);

        File.WriteAllText(t.P("multi.cue"),
            "FILE \"t1.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "FILE \"t2.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 00 00:00:00\n    INDEX 01 00:00:02\n" +
            "FILE \"t3.bin\" BINARY\n  TRACK 03 AUDIO\n    INDEX 01 00:00:00\n");

        // ---- merge ----
        var mr = BinCueMerge.Merge(t.P("multi.cue"), t.P("game.bin"), t.P("game.cue"));
        Assert.Equal(3, mr.Tracks);

        var merged = File.ReadAllBytes(t.P("game.bin"));
        Assert.Equal((100 + 80 + 60) * Sz, merged.Length);
        Assert.True(merged.AsSpan(0, t1.Length).SequenceEqual(t1));
        Assert.True(merged.AsSpan(100 * Sz, t2.Length).SequenceEqual(t2));
        Assert.True(merged.AsSpan(180 * Sz, t3.Length).SequenceEqual(t3));

        var mcue = CueSheet.Parse(File.ReadAllText(t.P("game.cue")));
        Assert.All(mcue.Tracks, tr => Assert.Equal("game.bin", tr.File));   // single file now
        Assert.Equal(0, mcue.Tracks[0].Indices[0].Time.ToSectors());        // track 1 @ 0
        Assert.Equal(100, mcue.Tracks[1].Indices[0].Time.ToSectors());      // track 2 INDEX 00 @ 100
        Assert.Equal(102, mcue.Tracks[1].Indices[1].Time.ToSectors());      // track 2 INDEX 01 @ 102
        Assert.Equal(180, mcue.Tracks[2].Indices[0].Time.ToSectors());      // track 3 @ 180

        // ---- split back ----
        var sr = BinCueMerge.Split(t.P("game.cue"), Path.Combine(t.Dir, "out"), "Game", t.P("out.cue"));
        Assert.Equal(3, sr.Tracks);

        string outDir = Path.Combine(t.Dir, "out");
        Assert.True(File.ReadAllBytes(Path.Combine(outDir, "Game (Track 1).bin")).AsSpan().SequenceEqual(t1));
        Assert.True(File.ReadAllBytes(Path.Combine(outDir, "Game (Track 2).bin")).AsSpan().SequenceEqual(t2));
        Assert.True(File.ReadAllBytes(Path.Combine(outDir, "Game (Track 3).bin")).AsSpan().SequenceEqual(t3));

        // Split cue restores file-relative indices.
        var scue = CueSheet.Parse(File.ReadAllText(t.P("out.cue")));
        Assert.Equal(0, scue.Tracks[0].Indices[0].Time.ToSectors());
        Assert.Equal(0, scue.Tracks[1].Indices[0].Time.ToSectors());   // INDEX 00 relative
        Assert.Equal(2, scue.Tracks[1].Indices[1].Time.ToSectors());   // INDEX 01 relative
        Assert.Equal(0, scue.Tracks[2].Indices[0].Time.ToSectors());
    }

    [Fact]
    public void Merge_RefusesASingleFileCue()
    {
        using var t = new Temp();
        File.WriteAllBytes(t.P("only.bin"), Pattern(10, 1));
        File.WriteAllText(t.P("one.cue"),
            "FILE \"only.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");
        Assert.Throws<InvalidDataException>(() =>
            BinCueMerge.Merge(t.P("one.cue"), t.P("o.bin"), t.P("o.cue")));
    }

    [Fact]
    public void RealRedumpCue_ParsesAndMergeArithmeticIsCorrect()
    {
        // The exact shape Redump distributes: quoted names with spaces and
        // parentheses, a MODE2/2352 data track, and an audio track with a
        // 150-sector (2s) pregap declared as INDEX 00 / INDEX 01.
        const string cue =
            "FILE \"Resident Evil 2 - Dual Shock Ver. (USA) (Disc 1) (Track 1).bin\" BINARY\n" +
            "  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "FILE \"Resident Evil 2 - Dual Shock Ver. (USA) (Disc 1) (Track 2).bin\" BINARY\n" +
            "  TRACK 02 AUDIO\n    INDEX 00 00:00:00\n    INDEX 01 00:02:00\n";

        var sheet = CueSheet.Parse(cue);
        Assert.Equal(2, sheet.Tracks.Count);
        Assert.Equal(CueTrackType.Mode2_2352, sheet.Tracks[0].Type);
        Assert.EndsWith("(Track 1).bin", sheet.Tracks[0].File);
        Assert.Equal(CueTrackType.Audio, sheet.Tracks[1].Type);
        Assert.Equal(2, sheet.Tracks[1].Indices.Count);
        Assert.Equal(0, sheet.Tracks[1].Indices[0].Time.ToSectors());     // INDEX 00
        Assert.Equal(150, sheet.Tracks[1].Indices[1].Time.ToSectors());   // INDEX 01 = 2s pregap

        // Two distinct files, so the writer keeps a FILE line per track and it
        // round-trips.
        var round = CueSheet.Parse(sheet.Write());
        Assert.EndsWith("(Track 2).bin", round.Tracks[1].File);

        // Merge arithmetic: track 2's file begins after track 1's sectors, so both
        // its indices shift forward by that offset.
        var starts = new Dictionary<string, long>
        {
            [sheet.Tracks[0].File] = 0,
            [sheet.Tracks[1].File] = 1000,
        };
        var merged = BinCueMerge.RewriteForMerge(sheet, starts, "re2.bin");
        Assert.Equal(1000, merged.Tracks[1].Indices[0].Time.ToSectors());
        Assert.Equal(1150, merged.Tracks[1].Indices[1].Time.ToSectors());
        Assert.All(merged.Tracks, tr => Assert.Equal("re2.bin", tr.File));
    }

    [Fact]
    public void RewriteForMerge_ShiftsIndicesByFileOffset()
    {
        var cue = CueSheet.Parse(
            "FILE \"a.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n" +
            "FILE \"b.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n");
        var starts = new Dictionary<string, long> { ["a.bin"] = 0, ["b.bin"] = 500 };
        var merged = BinCueMerge.RewriteForMerge(cue, starts, "m.bin");
        Assert.Equal(0, merged.Tracks[0].Indices[0].Time.ToSectors());
        Assert.Equal(500, merged.Tracks[1].Indices[0].Time.ToSectors());
    }
}
