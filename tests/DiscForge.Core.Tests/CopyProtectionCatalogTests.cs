using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

public class CopyProtectionCatalogTests
{
    private static CopyProtectionCatalog.ScannedBinary Bin(string name, string containing)
        => new(name, Encoding.ASCII.GetBytes("....." + containing + "....."));

    [Fact]
    public void SafeDisc_is_confirmed_from_its_files()
    {
        var r = CopyProtectionCatalog.Identify(new[] { "/00000001.TMP;1", "/GAME.ICD;1", "/GAME.EXE;1" });
        var d = Assert.Single(r.Detections);
        Assert.Equal("SafeDisc", d.Scheme);
        Assert.Equal(ProtectionConfidence.Confirmed, d.Confidence);
        Assert.Contains(d.Evidence, e => e.Detail == "00000001.TMP");
        Assert.Contains(d.Evidence, e => e.Detail == ".icd");
    }

    [Fact]
    public void SafeDisc_version_is_extracted_from_the_exe_signature()
    {
        // "BoG_ *90.0&!!  Yy>" then, at +20, three little-endian int32: 2, 90, 40.
        var sig = Encoding.ASCII.GetBytes("BoG_ *90.0&!!  Yy>");
        var data = new byte[64];
        System.Array.Copy(sig, 0, data, 10, sig.Length);
        void I32(int at, int v) { data[at] = (byte)v; data[at + 1] = (byte)(v >> 8); data[at + 2] = (byte)(v >> 16); data[at + 3] = (byte)(v >> 24); }
        I32(30, 2); I32(34, 90); I32(38, 40);   // 10 + 20 = 30

        var r = CopyProtectionCatalog.Identify(
            new[] { "/GAME.EXE;1" },
            new[] { new CopyProtectionCatalog.ScannedBinary("GAME.EXE", data) });

        var d = Assert.Single(r.Detections);
        Assert.Equal("SafeDisc", d.Scheme);
        Assert.Equal("2.90.0040", d.Version);
    }

    [Fact]
    public void SecuROM_is_confirmed_from_its_dlls()
    {
        var r = CopyProtectionCatalog.Identify(new[] { "/CMS16.DLL;1", "/CMS_95.DLL;1", "/CMS_NT.DLL;1" });
        var d = Assert.Single(r.Detections);
        Assert.Equal("SecuROM", d.Scheme);
        Assert.Equal(ProtectionConfidence.Confirmed, d.Confidence);
    }

    [Fact]
    public void LaserLock_is_confirmed_from_its_directory()
    {
        var r = CopyProtectionCatalog.Identify(new[] { "/LASERLOK/NOMOUSE.SP;1", "/GAME.EXE;1" });
        Assert.Contains(r.Detections, d => d.Scheme == "LaserLock" && d.Confidence == ProtectionConfidence.Confirmed);
    }

    [Fact]
    public void CD_Cops_version_trails_its_marker()
    {
        var bytes = Encoding.ASCII.GetBytes("xx CD-Cops,  ver. 2.0\0 more data");
        var r = CopyProtectionCatalog.Identify(
            new[] { "/CDCOPS.DLL;1" },
            new[] { new CopyProtectionCatalog.ScannedBinary("CDCOPS.DLL", bytes) });
        var d = Assert.Single(r.Detections);
        Assert.Equal("CD-Cops", d.Scheme);
        Assert.Equal("2.0", d.Version);
    }

    [Fact]
    public void An_exe_only_scheme_is_found_by_its_signature_string()
    {
        var r = CopyProtectionCatalog.Identify(
            new[] { "/GAME.EXE;1" },
            new[] { Bin("GAME.EXE", "VOB ProtectCD") });
        Assert.Contains(r.Detections, d => d.Scheme == "VOB ProtectCD");
    }

    [Fact]
    public void A_clean_disc_reports_nothing()
    {
        var r = CopyProtectionCatalog.Identify(new[] { "/GAME.EXE;1", "/DATA/LEVEL1.DAT;1", "/README.TXT;1" });
        Assert.False(r.AnyFound);
        Assert.Contains("No known", r.Summary());
    }

    [Fact]
    public void LibCrypt_is_detected_from_failing_subchannel_crc()
    {
        byte[] ValidQ(byte seed)
        {
            var q = new byte[12];
            for (int i = 0; i < 10; i++) q[i] = (byte)(seed + i);
            ushort crc = Crc16.ComputeInverted(q.AsSpan(0, 10));
            q[10] = (byte)(crc >> 8);
            q[11] = (byte)crc;
            return q;
        }

        var frames = new List<byte[]>();
        for (int i = 0; i < 20; i++) frames.Add(ValidQ((byte)i));
        // Corrupt two frames (LibCrypt writes them in pairs).
        var bad1 = ValidQ(100); bad1[3] ^= 0xFF; frames.Add(bad1);
        var bad2 = ValidQ(101); bad2[4] ^= 0xFF; frames.Add(bad2);

        var d = CopyProtectionCatalog.DetectLibCrypt(frames);
        Assert.NotNull(d);
        Assert.Equal("LibCrypt", d!.Scheme);
        Assert.Equal(ProtectionConfidence.Confirmed, d.Confidence);
        Assert.Contains("2 corrupted", d.Parameters);
    }

    [Fact]
    public void A_clean_subchannel_yields_no_libcrypt()
    {
        byte[] ValidQ(byte seed)
        {
            var q = new byte[12];
            for (int i = 0; i < 10; i++) q[i] = (byte)(seed + i);
            ushort crc = Crc16.ComputeInverted(q.AsSpan(0, 10));
            q[10] = (byte)(crc >> 8);
            q[11] = (byte)crc;
            return q;
        }
        var frames = Enumerable.Range(0, 30).Select(i => ValidQ((byte)i)).ToList();
        Assert.Null(CopyProtectionCatalog.DetectLibCrypt(frames));
    }

    [Fact]
    public void From_iso_detects_a_safedisc_disc_end_to_end()
    {
        var sig = Encoding.ASCII.GetBytes("BoG_ *90.0&!!  Yy>");
        var exe = new byte[64];
        System.Array.Copy(sig, 0, exe, 10, sig.Length);
        void I32(int at, int v) { exe[at] = (byte)v; exe[at + 1] = (byte)(v >> 8); exe[at + 2] = (byte)(v >> 16); exe[at + 3] = (byte)(v >> 24); }
        I32(30, 2); I32(34, 90); I32(38, 40);

        var image = IsoBuilder.Build("SAFEDISC", new List<IsoBuilder.FileEntry>
        {
            new("00000001.TMP", new byte[16]),
            new("GAME.ICD", new byte[32]),
            new("GAME.EXE", exe),
        }, joliet: false).Image;

        var r = CopyProtectionCatalog.FromIso(image);
        var d = Assert.Single(r.Detections);
        Assert.Equal("SafeDisc", d.Scheme);
        Assert.Equal(ProtectionConfidence.Confirmed, d.Confidence);
        Assert.Equal("2.90.0040", d.Version);
    }

    [Fact]
    public void Detections_never_claim_to_bypass()
    {
        var r = CopyProtectionCatalog.Identify(new[] { "/00000001.TMP;1", "/GAME.ICD;1" });
        Assert.All(r.Detections, d => Assert.Contains("does not bypass", d.Note));
    }
}
