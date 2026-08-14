using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class DiscChronologyTests
{
    private static byte[] BuildIso() =>
        IsoBuilder.Build("TESTVOL", new List<IsoBuilder.FileEntry>
        {
            new("READ.ME", Encoding.ASCII.GetBytes("hello")),
            new("DATA.BIN", new byte[1000]),
        }, joliet: false).Image;

    // Write a 17-byte volume date (YYYYMMDDHHMMSScc + GMT byte) at an absolute offset.
    private static void WriteVolumeDate(byte[] img, int off, int y, int mo, int d)
    {
        string s = $"{y:D4}{mo:D2}{d:D2}00000000";   // date + zeroed time + centiseconds
        for (int i = 0; i < 16; i++) img[off + i] = (byte)s[i];
        img[off + 16] = 0;
    }

    [Fact]
    public void A_volume_date_field_parses()
    {
        var buf = new byte[17];
        Encoding.ASCII.GetBytes("2024011512304500").CopyTo(buf, 0);
        buf[16] = 8;   // +2h

        var d = DiscChronology.ParseVolumeDate(buf);

        Assert.True(d.IsValid);
        Assert.Equal(2024, d.Year);
        Assert.Equal(1, d.Month);
        Assert.Equal(15, d.Day);
        Assert.Equal(12, d.Hour);
        Assert.Equal(30, d.Minute);
        Assert.Equal(45, d.Second);
    }

    [Fact]
    public void An_all_zero_volume_date_is_blank()
    {
        var buf = new byte[17];
        for (int i = 0; i < 16; i++) buf[i] = (byte)'0';
        Assert.True(DiscChronology.ParseVolumeDate(buf).Blank);
    }

    [Fact]
    public void A_directory_record_date_parses()
    {
        var buf = new byte[] { 125, 1, 1, 0, 0, 0, 0 };   // 1900 + 125 = 2025
        var d = DiscChronology.ParseRecordDate(buf);

        Assert.True(d.IsValid);
        Assert.Equal(2025, d.Year);
        Assert.Equal(1, d.Month);
        Assert.Equal(1, d.Day);
    }

    [Fact]
    public void Analyze_reads_the_volume_id_and_file_dates()
    {
        var r = DiscChronology.Analyze(BuildIso());

        Assert.Equal("TESTVOL", r.VolumeId);
        Assert.True(r.FileCount >= 2);
        Assert.NotNull(r.EarliestFile);
        Assert.Equal(2025, r.EarliestFile!.Year);          // IsoBuilder's fixed file date
    }

    [Fact]
    public void A_missing_volume_creation_date_is_flagged()
    {
        // IsoBuilder blanks the volume descriptor dates.
        var r = DiscChronology.Analyze(BuildIso());
        Assert.Contains(r.Anomalies, a => a.Contains("no creation date"));
    }

    [Fact]
    public void A_file_dated_after_the_volume_is_flagged_as_tampering()
    {
        var iso = BuildIso();
        WriteVolumeDate(iso, 16 * 2048 + 813, 2020, 1, 1);   // mastered 2020; the files are 2025

        var r = DiscChronology.Analyze(iso);

        Assert.True(r.FilesAfterVolume >= 2);
        Assert.Contains(r.Anomalies, a => a.Contains("AFTER the volume"));
        Assert.True(r.LooksTampered);
    }

    [Fact]
    public void A_non_iso_image_is_rejected()
    {
        var junk = new byte[40 * 2048];
        new Random(1).NextBytes(junk);
        junk[16 * 2048 + 1] = 0;                            // ensure no accidental CD001

        bool threw = false;
        try { DiscChronology.Analyze(junk); }
        catch (IsoFormatException) { threw = true; }
        Assert.True(threw);
    }
}
