using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class DiscArchaeologyTests
{
    // A small but complete ISO 9660 (+ Joliet) image with a couple of real files.
    private static byte[] BuildIso()
    {
        var files = new List<IsoBuilder.FileEntry>
        {
            new("READ.ME", Encoding.ASCII.GetBytes(new string('A', 3000))),
            new("DATA.BIN", Enumerable.Range(0, 5000).Select(i => (byte)(i * 37 + 1)).ToArray()),
        };
        return IsoBuilder.Build("ARCHAEO", files, joliet: true).Image;
    }

    [Fact]
    public void A_clean_iso_hides_nothing()
    {
        var iso = BuildIso();

        var report = DiscArchaeology.FindOrphans(iso);

        Assert.False(report.FoundAnything);
        Assert.Empty(report.Orphans);
    }

    [Fact]
    public void Data_appended_past_the_volume_is_found()
    {
        var iso = BuildIso();
        // Append a blob past the end of the declared volume — the classic place to
        // hide data behind a normal-looking disc.
        var hidden = Encoding.ASCII.GetBytes("SECRET-PAYLOAD-" + new string('x', 400));
        var img = new byte[iso.Length + 2048];
        iso.CopyTo(img, 0);
        hidden.CopyTo(img, iso.Length + 100);

        var report = DiscArchaeology.FindOrphans(img);

        Assert.True(report.FoundAnything);
        var found = Assert.Single(report.Orphans);
        Assert.Equal("past-volume-end", found.Zone);
        Assert.Equal("text-like", found.Kind);
        Assert.True(found.Offset >= iso.Length);
        Assert.Contains("SECRET-PAYLOAD", found.AsciiSample);
    }

    [Fact]
    public void A_payload_tucked_in_the_system_area_is_surfaced()
    {
        var iso = BuildIso();
        // Standard ISO 9660 writes nothing to sectors 0–15; content there is notable.
        var payload = Enumerable.Range(0, 600).Select(i => (byte)(i * 91 + 5)).ToArray();
        payload.CopyTo(iso, 4 * 2048);            // sector 4, inside the system area

        var report = DiscArchaeology.FindOrphans(iso);

        Assert.True(report.FoundAnything);
        Assert.Contains(report.Orphans, o => o.Zone == "system-area" && o.Offset == 4 * 2048);
    }

    [Fact]
    public void High_entropy_orphan_data_is_flagged_as_such()
    {
        var iso = BuildIso();
        // A pseudo-random (high-entropy) blob past the volume — looks compressed/encrypted.
        var rng = new Random(1234);
        var blob = new byte[4096];
        rng.NextBytes(blob);
        var img = new byte[iso.Length + 8192];
        iso.CopyTo(img, 0);
        blob.CopyTo(img, iso.Length + 256);

        var report = DiscArchaeology.FindOrphans(img);

        var found = Assert.Single(report.Orphans);
        Assert.Equal("high-entropy", found.Kind);
        Assert.True(found.Entropy > 7.2);
    }

    [Fact]
    public void Tiny_stray_bytes_below_the_threshold_are_ignored()
    {
        var iso = BuildIso();
        var img = new byte[iso.Length + 2048];
        iso.CopyTo(img, 0);
        img[iso.Length + 500] = 0x01;             // a lone byte, well under minOrphanBytes

        var report = DiscArchaeology.FindOrphans(img);   // default threshold 32

        Assert.False(report.FoundAnything);
    }

    [Fact]
    public void A_non_iso_image_is_rejected_clearly()
    {
        var junk = new byte[40 * 2048];
        new Random(7).NextBytes(junk);

        bool threw = false;
        try { DiscArchaeology.FindOrphans(junk); }
        catch (IsoFormatException) { threw = true; }

        Assert.True(threw);   // a non-ISO image must be rejected, not mis-analysed
    }
}
