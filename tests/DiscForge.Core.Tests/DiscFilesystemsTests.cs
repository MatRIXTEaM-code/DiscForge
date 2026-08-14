using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class DiscFilesystemsTests
{
    private const int SS = 2048;

    private static byte[] BuildIso()
    {
        var files = new List<IsoBuilder.FileEntry> { new("READ.ME", Encoding.ASCII.GetBytes("hi")) };
        return IsoBuilder.Build("HYBRIDVOL", files, joliet: true).Image;
    }

    private static void PutAscii(byte[] img, long off, string s)
    {
        for (int i = 0; i < s.Length; i++) img[off + i] = (byte)s[i];
    }

    [Fact]
    public void An_iso_with_joliet_reports_both()
    {
        var report = DiscFilesystems.Identify(BuildIso());

        Assert.Contains(report.Filesystems, f => f.Kind == "ISO 9660");
        Assert.Contains(report.Filesystems, f => f.Kind == "Joliet");
        Assert.Contains(report.Filesystems, f => f.Kind == "ISO 9660" && f.Label == "HYBRIDVOL");
    }

    [Fact]
    public void A_cd_xa_marker_in_the_pvd_is_detected()
    {
        var img = BuildIso();
        PutAscii(img, 16L * SS + 1024, "CD-XA001");   // stamp CD-XA into the PVD

        var report = DiscFilesystems.Identify(img);

        Assert.Contains(report.Filesystems, f => f.Kind == "CD-XA");
    }

    [Fact]
    public void A_udf_recognition_sequence_is_detected()
    {
        var img = new byte[64 * SS];
        // Minimal VRS: BEA01, NSR03, TEA01 in consecutive sectors from 16.
        img[16 * SS] = 0; PutAscii(img, 16L * SS + 1, "BEA01");
        img[17 * SS] = 0; PutAscii(img, 17L * SS + 1, "NSR03");
        img[18 * SS] = 0; PutAscii(img, 18L * SS + 1, "TEA01");

        var report = DiscFilesystems.Identify(img);

        Assert.Contains(report.Filesystems, f => f.Kind == "UDF" && f.Detail.Contains("NSR03"));
    }

    [Fact]
    public void An_apple_hfs_volume_is_detected_with_its_name()
    {
        var img = new byte[40 * SS];
        img[1024] = 0x42; img[1025] = 0x44;              // 'BD' MDB signature
        img[1024 + 36] = 7;                              // Pascal length
        PutAscii(img, 1024 + 37, "MacDisc");             // drVN volume name

        var report = DiscFilesystems.Identify(img);

        Assert.Contains(report.Filesystems, f => f.Kind == "Apple HFS" && f.Label == "MacDisc");
    }

    [Fact]
    public void A_mac_pc_hybrid_is_flagged_as_hybrid()
    {
        var img = BuildIso();
        // Splice an HFS MDB into the same image — a real Mac+PC hybrid layout.
        img[1024] = 0x42; img[1025] = 0x44;
        img[1024 + 36] = 3;
        PutAscii(img, 1024 + 37, "MAC");

        var report = DiscFilesystems.Identify(img);

        Assert.True(report.IsHybrid);
        Assert.Contains(report.Filesystems, f => f.Kind == "ISO 9660");
        Assert.Contains(report.Filesystems, f => f.Kind == "Apple HFS");
    }

    [Fact]
    public void A_raw_image_with_no_filesystem_reports_none()
    {
        var img = new byte[40 * SS];
        new Random(3).NextBytes(img);
        // Make sure no accidental CD001 lands at sector 16.
        img[16 * SS + 1] = 0;

        var report = DiscFilesystems.Identify(img);

        Assert.False(report.Any);
    }
}
