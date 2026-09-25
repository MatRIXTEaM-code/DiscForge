// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Devices;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The GDR-816x recogniser gates raw GameCube/Wii/GD-ROM dumping to the drive
/// family whose vendor command set supports it. These pin the INQUIRY-string
/// matching: the whole 816x family, hyphen or not, case- and space-insensitive,
/// and a clean no on everything else.
/// </summary>
public class RawDumpDriveTests
{
    [Theory]
    [InlineData("HL-DT-ST", "DVD-ROM GDR-8164B")]
    [InlineData("HL-DT-ST", "DVD-ROM GDR-8161B")]
    [InlineData("HL-DT-ST", "DVD-ROM GDR-8162B")]
    [InlineData("HL-DT-ST", "DVD-ROM GDR-8163B")]
    [InlineData("hl-dt-st", "dvd-rom gdr-8164b")]   // case-insensitive
    [InlineData("HL-DT-ST ", " GDR8164B")]          // no hyphen, stray spaces
    public void Recognises_the_GDR_816x_family(string vendor, string product)
    {
        Assert.True(RawDumpDrive.IsSupported(vendor, product));
        Assert.Contains("GDR-816x", RawDumpDrive.Describe(vendor, product));
    }

    [Theory]
    [InlineData("HL-DT-ST", "DVD-ROM GDR-8081N")]   // a different GDR, not 816x
    [InlineData("HL-DT-ST", "BD-RE  WH16NS40")]     // an LG Blu-ray writer, not the family
    [InlineData("PIONEER", "BD-RW   BDR-212")]      // different vendor
    [InlineData("HL-DT-ST", "DVD-RAM GH24NSD1")]    // an LG burner, not GDR-816x
    [InlineData(null, null)]
    [InlineData("HL-DT-ST", "")]
    public void Rejects_everything_else(string? vendor, string? product)
    {
        Assert.False(RawDumpDrive.IsSupported(vendor, product));
    }
}
