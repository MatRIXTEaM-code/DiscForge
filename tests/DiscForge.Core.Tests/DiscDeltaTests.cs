using System.Security.Cryptography;
using System.Text;
using DiscForge.Core.Iso;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

public class DiscDeltaTests
{
    private static byte[] Iso(string vol, params (string Name, byte[] Data)[] files)
    {
        var entries = files.Select(f => new IsoBuilder.FileEntry(f.Name, f.Data)).ToList();
        return IsoBuilder.Build(vol, entries, joliet: true).Image;
    }

    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] Filler(int seed, int len)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        return b;
    }

    // A shared file big enough to matter, plus files that change between versions.
    private static readonly byte[] Shared = Filler(1, 20000);

    private static byte[] BaseIso() => Iso("GAME_V1",
        ("SHARED.BIN", Shared),
        ("LEVEL.DAT", Bytes("level version one")),
        ("OLD.TXT", Bytes("this file is removed in v2")));

    private static byte[] TargetIso() => Iso("GAME_V2",
        ("SHARED.BIN", Shared),                       // identical — should NOT travel in the delta
        ("LEVEL.DAT", Bytes("level version TWO, changed")),
        ("NEW.TXT", Bytes("this file is new in v2")));

    [Fact]
    public void The_named_diff_reports_added_removed_and_changed()
    {
        var delta = DiscDelta.Create(BaseIso(), TargetIso());

        Assert.Contains("/NEW.TXT", delta.Diff.Added);
        Assert.Contains("/OLD.TXT", delta.Diff.Removed);
        Assert.Contains("/LEVEL.DAT", delta.Diff.Changed);
        Assert.True(delta.Diff.Unchanged >= 1);        // SHARED.BIN
    }

    [Fact]
    public void The_shared_file_is_not_carried_in_the_delta()
    {
        var delta = DiscDelta.Create(BaseIso(), TargetIso());

        string sharedSha = System.Convert.ToHexString(SHA256.HashData(Shared)).ToLowerInvariant();
        Assert.DoesNotContain(sharedSha, delta.Store.Keys);   // it lives in the base, referenced only

        // The delta is much smaller than the target image, because 20 KB of shared
        // content stays behind.
        Assert.True(delta.DeltaStoreBytes < Shared.Length);
    }

    [Fact]
    public void Applying_the_delta_rebuilds_the_target_byte_for_byte()
    {
        var baseImg = BaseIso();
        var targetImg = TargetIso();
        var delta = DiscDelta.Create(baseImg, targetImg);

        var rebuilt = DiscDelta.Apply(delta, baseImg);

        Assert.True(rebuilt.AsSpan().SequenceEqual(targetImg));
    }

    [Fact]
    public void The_delta_round_trips_through_json()
    {
        var baseImg = BaseIso();
        var targetImg = TargetIso();
        var delta = DiscDelta.Create(baseImg, targetImg);

        var back = DiscDelta.FromJson(DiscDelta.ToJson(delta));
        var rebuilt = DiscDelta.Apply(back, baseImg);

        Assert.True(rebuilt.AsSpan().SequenceEqual(targetImg));
    }

    [Fact]
    public void Applying_a_delta_to_the_wrong_base_is_refused()
    {
        var delta = DiscDelta.Create(BaseIso(), TargetIso());
        var wrongBase = Iso("SOMETHING_ELSE", ("X.BIN", Filler(9, 5000)));

        bool threw = false;
        try { DiscDelta.Apply(delta, wrongBase); }
        catch (InvalidOperationException) { threw = true; }

        Assert.True(threw);   // the base-image hash guard fires
    }
}
