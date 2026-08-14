// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Identify;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the format-identification expansion: virtual disks, non-disc filesystems, extra archives,
/// audio and image types, ROM/patch formats and retro disk/tape images. Each is presented as the minimal
/// bytes carrying its documented signature — at offset 0, at a fixed offset, or in the 512-byte footer —
/// and must be named correctly. Validated in-cloud against real mkfs.ext4 / mkfs.hfsplus / ImageMagick
/// samples too; these lock the signatures into CI.
/// </summary>
public class NewFormatIdentifierTests
{
    private static byte[] At(int off, byte[] magic, int size = 4096)
    {
        var b = new byte[Math.Max(size, off + magic.Length)];
        magic.CopyTo(b, off);
        return b;
    }
    private static byte[] Ascii(string s, int off = 0, int size = 4096) => At(off, Encoding.ASCII.GetBytes(s), size);
    private static byte[] Bytes(int off, params byte[] m) => At(off, m);

    [Theory]
    // virtual disks
    [InlineData("conectix", "VHD")]
    [InlineData("vhdxfile", "VHDX")]
    [InlineData("KDMV", "VMDK")]
    [InlineData("# Disk DescriptorFile", "VMDK")]
    [InlineData("<<< Oracle VM VirtualBox Disk Image >>>", "VDI")]
    // archives
    [InlineData("MSCF", "CAB")]
    // audio
    [InlineData("MAC ", "Monkey's Audio")]
    [InlineData("wvpk", "WavPack")]
    [InlineData("MPCK", "Musepack")]
    [InlineData("TTA1", "TTA")]
    [InlineData("#!AMR", "AMR")]
    // images
    [InlineData("DDS ", "DDS")]
    [InlineData("8BPS", "PSD")]
    [InlineData("gimp xcf", "XCF")]
    // patches
    [InlineData("PATCH", "IPS")]
    [InlineData("BPS1", "BPS")]
    [InlineData("UPS1", "UPS")]
    // retro disk / tape
    [InlineData("WOZ2", "WOZ")]
    [InlineData("GCR-1541", "G64")]
    [InlineData("ZXTape!", "TZX")]
    // documents
    [InlineData("{\\rtf", "RTF")]
    // Xbox 360 STFS / GOD packages
    [InlineData("CON ", "STFS")]
    [InlineData("LIVE", "STFS")]
    [InlineData("PIRS", "STFS")]
    public void Offset_zero_ascii_magics_are_named(string magic, string expected)
    {
        Assert.Equal(expected, FormatIdentifier.Identify(Ascii(magic)).Name);
    }

    [Fact]
    public void Byte_magics_are_named()
    {
        Assert.Equal("QCOW", FormatIdentifier.Identify(Bytes(0, (byte)'Q', (byte)'F', (byte)'I', 0xFB)).Name);
        Assert.Equal("ASF/WMA", FormatIdentifier.Identify(Bytes(0, 0x30, 0x26, 0xB2, 0x75)).Name);
        Assert.Equal("OLE2", FormatIdentifier.Identify(Bytes(0, 0xD0, 0xCF, 0x11, 0xE0)).Name);
        Assert.Equal("LZ4", FormatIdentifier.Identify(Bytes(0, 0x04, 0x22, 0x4D, 0x18)).Name);
        // ECM: "ECM\0" (the trailing NUL keeps it off ordinary text).
        Assert.Equal("ECM", FormatIdentifier.Identify(Bytes(0, (byte)'E', (byte)'C', (byte)'M', 0x00)).Name);
    }

    [Fact]
    public void Fixed_offset_filesystems_and_roms_are_named()
    {
        Assert.Equal("NTFS", FormatIdentifier.Identify(Ascii("NTFS    ", off: 3)).Name);
        Assert.Equal("exFAT", FormatIdentifier.Identify(Ascii("EXFAT   ", off: 3)).Name);
        Assert.Equal("ext2/3/4", FormatIdentifier.Identify(Bytes(0x438, 0x53, 0xEF)).Name);
        Assert.Equal("HFS+", FormatIdentifier.Identify(Bytes(0x400, 0x48, 0x2B)).Name);
        Assert.Equal("Game Boy", FormatIdentifier.Identify(Bytes(0x104, 0xCE, 0xED, 0x66, 0x66)).Name);
        Assert.Equal("Master System / Game Gear", FormatIdentifier.Identify(Ascii("TMR SEGA", off: 0x1FF0, size: 0x2000)).Name);
    }

    [Fact]
    public void Footer_signatures_are_named()
    {
        // DMG's "koly" trailer lives in the last 512 bytes.
        var dmg = new byte[4096];
        Encoding.ASCII.GetBytes("koly").CopyTo(dmg, dmg.Length - 512);
        Assert.Equal("DMG", FormatIdentifier.Identify(dmg).Name);
    }
}
