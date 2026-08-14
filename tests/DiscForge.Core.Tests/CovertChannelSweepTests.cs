using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class CovertChannelSweepTests
{
    private static byte[] BuildIso() =>
        IsoBuilder.Build("VOL", new List<IsoBuilder.FileEntry>
        {
            new("READ.ME", Encoding.ASCII.GetBytes("hello")),        // 5 bytes → lots of slack
            new("DATA.BIN", new byte[2048]),                          // exactly one sector → no slack
        }, joliet: false).Image;

    [Fact]
    public void A_clean_iso_hides_nothing()
    {
        var r = CovertChannelSweep.Scan(BuildIso());
        Assert.False(r.AnyHidden);
        Assert.Contains("No hidden data", r.Summary());
    }

    [Fact]
    public void Data_stashed_in_file_slack_is_found()
    {
        var iso = BuildIso();
        // Find READ.ME's slack and plant a message there.
        IsoDirectory dir;
        using (var ms = new MemoryStream(iso, writable: false)) dir = IsoReader.Read(ms);
        var readme = dir.Files.First(f => f.Path.Contains("READ.ME"));
        long end = (long)readme.Extent * 2048 + readme.Size;
        var msg = Encoding.ASCII.GetBytes("SECRET PAYLOAD HIDDEN IN SLACK");
        msg.CopyTo(iso, (int)end);

        var r = CovertChannelSweep.Scan(iso);
        Assert.True(r.AnyHidden);
        Assert.Contains(r.Findings, f => f.Zone == "file-slack" && f.Detail.Contains("READ.ME"));
        Assert.True(r.HiddenBytes >= msg.Length);
    }

    [Fact]
    public void A_payload_in_the_system_area_is_flagged()
    {
        var iso = BuildIso();
        var payload = Encoding.ASCII.GetBytes("bootloader-or-secret");
        payload.CopyTo(iso, 0);                                       // sector 0 = system area

        var r = CovertChannelSweep.Scan(iso);
        Assert.Contains(r.Findings, f => f.Zone == "system-area");
    }

    [Fact]
    public void High_entropy_slack_is_described_differently()
    {
        var iso = BuildIso();
        IsoDirectory dir;
        using (var ms = new MemoryStream(iso, writable: false)) dir = IsoReader.Read(ms);
        var readme = dir.Files.First(f => f.Path.Contains("READ.ME"));
        long end = (long)readme.Extent * 2048 + readme.Size;
        var rng = new System.Random(1);
        for (long i = end; i < ((end + 2047) / 2048) * 2048; i++) iso[i] = (byte)rng.Next(1, 256);

        var r = CovertChannelSweep.Scan(iso);
        var slack = r.Findings.First(f => f.Zone == "file-slack");
        Assert.True(slack.Entropy > 5.0);      // random bytes → high entropy
    }
}
