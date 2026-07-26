// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Iso;
using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the PlayStation disc identifier. Parsing SYSTEM.CNF is pinned
/// directly, and the "read it from an image" path is exercised end to end by
/// building a small ISO that contains a SYSTEM.CNF and reading it back — the same
/// approach the ISO reader itself is tested with.
/// </summary>
public class SystemCnfTests
{
    // ---- parsing ------------------------------------------------------------

    [Fact]
    public void A_ps2_system_cnf_identifies_the_console_id_and_region()
    {
        var id = SystemCnf.Parse(
            "BOOT2 = cdrom0:\\SLUS_200.02;1\r\nVER = 1.00\r\nVMODE = NTSC\r\n");

        Assert.Equal(PsConsole.Ps2, id.Console);
        Assert.Equal("SLUS-20002", id.GameId);
        Assert.Equal("USA (NTSC-U)", id.Region);
        Assert.Equal("NTSC", id.VideoMode);
        Assert.Equal("1.00", id.Version);
    }

    [Fact]
    public void A_ps1_system_cnf_uses_the_boot_key_and_is_detected_as_ps1()
    {
        var id = SystemCnf.Parse("BOOT = cdrom:\\SLPS_004.35;1\nTCB = 4\nEVENT = 10\nVMODE = NTSC\n");

        Assert.Equal(PsConsole.Ps1, id.Console);
        Assert.Equal("SLPS-00435", id.GameId);
        Assert.Equal("Japan (NTSC-J)", id.Region);
    }

    [Theory]
    [InlineData("SLUS_200.02", "USA (NTSC-U)")]
    [InlineData("SCES_500.02", "Europe (PAL)")]
    [InlineData("SLES_512.34", "Europe (PAL)")]
    [InlineData("SLPS_012.34", "Japan (NTSC-J)")]
    [InlineData("SLPM_620.01", "Japan (NTSC-J)")]
    [InlineData("SLKA_250.01", "Korea")]
    public void The_region_comes_from_the_serials_third_letter(string serial, string expectedRegion)
    {
        var id = SystemCnf.Parse($"BOOT2 = cdrom0:\\{serial};1\n");
        Assert.Equal(expectedRegion, id.Region);
    }

    [Fact]
    public void The_serial_normalises_underscore_and_dot_to_the_dashed_form()
    {
        Assert.Equal("SCUS-97328", SystemCnf.NormaliseSerial("SCUS_973.28"));
        Assert.Equal("SLES-51234", SystemCnf.NormaliseSerial("SLES_512.34"));
    }

    [Fact]
    public void The_serial_is_pulled_from_the_boot_path()
    {
        Assert.Equal("SLUS_200.02", SystemCnf.ExtractSerial("cdrom0:\\SLUS_200.02;1"));
        Assert.Equal("SLES_512.34", SystemCnf.ExtractSerial("cdrom0:/SLES_512.34;1"));
    }

    [Fact]
    public void Keys_are_case_insensitive_and_spacing_is_tolerated()
    {
        var id = SystemCnf.Parse("boot2=cdrom0:\\SLUS_200.02;1\n  ver  =  2.00  \n");
        Assert.Equal(PsConsole.Ps2, id.Console);
        Assert.Equal("2.00", id.Version);
    }

    [Fact]
    public void A_non_standard_boot_file_yields_an_empty_game_id_but_still_parses()
    {
        var id = SystemCnf.Parse("BOOT2 = cdrom0:\\BOOT.ELF;1\n");
        Assert.Equal("", id.GameId);
        Assert.Equal(PsConsole.Ps2, id.Console);
    }

    [Fact]
    public void A_file_without_a_boot_line_is_refused()
    {
        Assert.Throws<SystemCnfException>(() => SystemCnf.Parse("VER = 1.00\nVMODE = NTSC\n"));
    }

    // ---- read from an image -------------------------------------------------

    [Fact]
    public void The_identifier_reads_system_cnf_out_of_an_iso_image()
    {
        // Build a small ISO carrying a PS2-style SYSTEM.CNF, then identify it.
        var cnf = Encoding.ASCII.GetBytes("BOOT2 = cdrom0:\\SLES_512.34;1\r\nVER = 1.01\r\nVMODE = PAL\r\n");
        var iso = IsoBuilder.BuildTree("PS2GAME", new[]
        {
            IsoBuilder.Node.File("SYSTEM.CNF", cnf),
            IsoBuilder.Node.File("SLES_512.34", new byte[2048]),
        }, joliet: true).Image;

        string path = Path.Combine(Path.GetTempPath(), "ps2_" + Guid.NewGuid().ToString("N") + ".iso");
        File.WriteAllBytes(path, iso);
        try
        {
            var id = SystemCnf.FromImage(path);
            Assert.NotNull(id);
            Assert.Equal(PsConsole.Ps2, id!.Console);
            Assert.Equal("SLES-51234", id.GameId);
            Assert.Equal("Europe (PAL)", id.Region);
            Assert.Equal("PAL", id.VideoMode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_image_without_system_cnf_returns_null()
    {
        var iso = IsoBuilder.BuildTree("DATA", new[]
        {
            IsoBuilder.Node.File("README.TXT", Encoding.ASCII.GetBytes("not a game")),
        }, joliet: true).Image;

        string path = Path.Combine(Path.GetTempPath(), "nodata_" + Guid.NewGuid().ToString("N") + ".iso");
        File.WriteAllBytes(path, iso);
        try { Assert.Null(SystemCnf.FromImage(path)); }
        finally { File.Delete(path); }
    }
}
