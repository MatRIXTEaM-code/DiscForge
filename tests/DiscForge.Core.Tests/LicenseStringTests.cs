// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Iso;
using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the sector-4 license-text reader: byte-layout self-consistency (the documented
/// offsets must sum to exactly one 2048-byte sector for every region), exact parsing per region,
/// mismatch/garbage detection, the SYSTEM.CNF cross-check, and reading sector 4 out of a real image.
/// </summary>
public class LicenseStringTests
{
    // ---- byte-layout self-consistency ----------------------------------------

    [Theory]
    [InlineData("Japan", 32, 33, 1983)]
    [InlineData("Europe", 32, 38, 1978)]
    [InlineData("America", 32, 38, 1978)]
    public void The_documented_line_and_padding_lengths_sum_to_exactly_one_sector(
        string region, int line1, int line2, int padding)
    {
        Assert.Equal(2048, line1 + line2 + padding);
        _ = region; // documents which region the lengths belong to
    }

    // ---- parsing --------------------------------------------------------------

    [Fact]
    public void A_genuine_japan_sector_parses_as_well_formed()
    {
        var sector = BuildSector(LicenseRegion.Japan);
        var r = LicenseString.Parse(sector);

        Assert.Equal(LicenseRegion.Japan, r.Region);
        Assert.True(r.Line1Matches);
        Assert.True(r.Line2Matches);
        Assert.True(r.WellFormed);
        Assert.Contains("Sony Computer Entertainment Inc.", r.Line2Text);
    }

    [Theory]
    [InlineData(LicenseRegion.Europe)]
    [InlineData(LicenseRegion.America)]
    public void A_genuine_pal_or_ntsc_u_sector_parses_as_well_formed(LicenseRegion region)
    {
        var sector = BuildSector(region);
        var r = LicenseString.Parse(sector);

        Assert.Equal(region, r.Region);
        Assert.True(r.WellFormed);
        Assert.True(r.PaddingLooksStandard);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Garbage_sector_data_is_reported_as_unrecognised_not_misidentified()
    {
        var sector = new byte[2048];
        new Random(1).NextBytes(sector);
        var r = LicenseString.Parse(sector);

        Assert.Equal(LicenseRegion.Unknown, r.Region);
        Assert.False(r.WellFormed);
        Assert.NotEmpty(r.Issues);
    }

    [Fact]
    public void An_all_zero_sector_is_reported_as_unrecognised()
    {
        var r = LicenseString.Parse(new byte[2048]);
        Assert.Equal(LicenseRegion.Unknown, r.Region);
        Assert.False(r.Line1Matches);
        Assert.False(r.WellFormed);
    }

    [Fact]
    public void A_corrupted_line_2_is_flagged_even_though_line_1_is_intact()
    {
        var sector = BuildSector(LicenseRegion.America);
        sector[40] ^= 0xFF; // inside line 2
        var r = LicenseString.Parse(sector);

        Assert.True(r.Line1Matches);
        Assert.False(r.Line2Matches);
        Assert.Equal(LicenseRegion.Unknown, r.Region);
        Assert.False(r.WellFormed);
        Assert.Contains(r.Issues, i => i.Contains("line 2"));
    }

    [Fact]
    public void Non_standard_padding_is_a_note_not_a_misidentification()
    {
        var sector = BuildSector(LicenseRegion.Europe);
        // Corrupt a few padding bytes after line 2 (Europe padding should be all zero).
        sector[100] = 0x7F;
        sector[101] = 0x7F;
        var r = LicenseString.Parse(sector);

        // Region is still correctly identified from lines 1+2; only padding is flagged.
        Assert.Equal(LicenseRegion.Europe, r.Region);
        Assert.True(r.Line1Matches);
        Assert.True(r.Line2Matches);
        Assert.False(r.PaddingLooksStandard);
        Assert.False(r.WellFormed);
        Assert.Contains(r.Issues, i => i.Contains("padding") && i.Contains("informational"));
    }

    [Fact]
    public void Parse_rejects_a_sector_shorter_than_2048_bytes()
    {
        Assert.Throws<ArgumentException>(() => LicenseString.Parse(new byte[100]));
    }

    // ---- SYSTEM.CNF cross-check ------------------------------------------------

    [Fact]
    public void CrossCheck_is_null_when_the_license_text_and_system_cnf_agree()
    {
        var license = LicenseString.Parse(BuildSector(LicenseRegion.Europe));
        Assert.Null(LicenseString.CrossCheck(license, "Europe (PAL)"));
    }

    [Fact]
    public void CrossCheck_flags_a_boot_area_that_disagrees_with_system_cnf()
    {
        var license = LicenseString.Parse(BuildSector(LicenseRegion.Japan));
        var diff = LicenseString.CrossCheck(license, "USA (NTSC-U)");

        Assert.NotNull(diff);
        Assert.Contains("Japan", diff);
        Assert.Contains("USA (NTSC-U)", diff);
    }

    [Fact]
    public void CrossCheck_is_null_when_the_license_text_is_unrecognised_nothing_to_compare()
    {
        var license = LicenseString.Parse(new byte[2048]);
        Assert.Null(LicenseString.CrossCheck(license, "USA (NTSC-U)"));
    }

    [Fact]
    public void CrossCheck_is_null_for_a_system_cnf_region_with_no_dedicated_license_block()
    {
        // Korea/Asia PS1 discs reuse another region's license block; there is nothing
        // dedicated to compare against, so this must not be reported as a mismatch.
        var license = LicenseString.Parse(BuildSector(LicenseRegion.Japan));
        Assert.Null(LicenseString.CrossCheck(license, "Korea"));
    }

    // ---- read from an image ----------------------------------------------------

    [Fact]
    public void FromImage_reads_sector_4_out_of_a_plain_iso()
    {
        var cnf = Encoding.ASCII.GetBytes("BOOT2 = cdrom0:\\SLES_512.34;1\r\nVER = 1.01\r\nVMODE = PAL\r\n");
        var iso = IsoBuilder.BuildTree("PS2GAME", new[]
        {
            IsoBuilder.Node.File("SYSTEM.CNF", cnf),
            IsoBuilder.Node.File("SLES_512.34", new byte[2048]),
        }, joliet: true).Image;

        // Stamp the genuine Europe license text into sector 4 of the system area, which
        // ISO 9660 leaves unused ahead of the volume descriptors at sector 16.
        var sector = BuildSector(LicenseRegion.Europe);
        Array.Copy(sector, 0, iso, 4L * 2048, sector.Length);

        string path = Path.Combine(Path.GetTempPath(), "lic_" + Guid.NewGuid().ToString("N") + ".iso");
        File.WriteAllBytes(path, iso);
        try
        {
            var r = LicenseString.FromImage(path);
            Assert.NotNull(r);
            Assert.Equal(LicenseRegion.Europe, r!.Region);
            Assert.True(r.WellFormed);

            var id = DiscForge.Core.PlayStation.SystemCnf.FromImage(path);
            Assert.NotNull(id);
            Assert.Null(LicenseString.CrossCheck(r, id!.Region)); // both say Europe
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FromImage_returns_null_for_a_missing_file()
    {
        Assert.Null(LicenseString.FromImage(Path.Combine(Path.GetTempPath(), "no_such_" + Guid.NewGuid().ToString("N") + ".iso")));
    }

    // ---- fixture ----------------------------------------------------------------

    /// <summary>Build a genuine 2048-byte sector 4 for the given region, byte-for-byte per the
    /// documented layout (see LicenseString's class doc).</summary>
    private static byte[] BuildSector(LicenseRegion region)
    {
        var sector = new byte[2048];
        var line1 = Encoding.ASCII.GetBytes(new string(' ', 10) + "Licensed" + new string(' ', 2) + "by" + new string(' ', 10));
        Array.Copy(line1, sector, line1.Length);

        byte[] line2;
        int pos = line1.Length;
        switch (region)
        {
            case LicenseRegion.Japan:
                line2 = Encoding.ASCII.GetBytes("Sony Computer Entertainment Inc.");
                Array.Copy(line2, 0, sector, pos, line2.Length);
                sector[pos + line2.Length] = 0x0A;
                pos += line2.Length + 1;
                FillJapanPadding(sector, pos);
                break;
            case LicenseRegion.Europe:
                line2 = Encoding.ASCII.GetBytes("Sony Computer Entertainment Euro" + " pe   ");
                Array.Copy(line2, 0, sector, pos, line2.Length);
                // Remainder is already zero-initialised — matches the documented EU/US padding.
                break;
            case LicenseRegion.America:
                line2 = Encoding.ASCII.GetBytes("Sony Computer Entertainment Amer" + "  ica ");
                Array.Copy(line2, 0, sector, pos, line2.Length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(region));
        }
        return sector;
    }

    private static void FillJapanPadding(byte[] sector, int start)
    {
        byte[] tile = { 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30,
                         0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30,
                         0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30,
                         0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30,
                         0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30,
                         0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30,
                         0x30, 0x30, // 62x0x30
                         0x0A, 0x30 };
        Assert.Equal(64, tile.Length);
        int i = start, t = 0;
        while (i < sector.Length)
        {
            sector[i++] = tile[t];
            t = (t + 1) % tile.Length;
        }
    }
}
