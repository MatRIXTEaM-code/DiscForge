// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class ElToritoTests
{
    private const int SS = 2048;

    private static void WriteBootRecord(byte[] img, uint catalogLba)
    {
        var br = img.AsSpan(17 * SS, SS);
        br[0] = 0x00;
        Encoding.ASCII.GetBytes("CD001").CopyTo(br[1..]);
        br[6] = 0x01;
        Encoding.ASCII.GetBytes("EL TORITO SPECIFICATION").CopyTo(br[7..]);
        br[71] = (byte)catalogLba; br[72] = (byte)(catalogLba >> 8);
        br[73] = (byte)(catalogLba >> 16); br[74] = (byte)(catalogLba >> 24);
    }

    private static void WriteValidation(Span<byte> cat, ElToritoPlatform platform, string id, bool goodChecksum = true)
    {
        cat[0] = 0x01;
        cat[1] = (byte)platform;
        Encoding.ASCII.GetBytes(id).CopyTo(cat[4..]);
        cat[30] = 0x55; cat[31] = 0xAA;
        // Choose the checksum word so all sixteen 16-bit words sum to zero.
        int sum = 0;
        for (int i = 0; i < 32; i += 2) if (i != 28) sum += cat[i] | (cat[i + 1] << 8);
        ushort chk = (ushort)((0x10000 - (sum & 0xFFFF)) & 0xFFFF);
        if (!goodChecksum) chk ^= 0x1234;
        cat[28] = (byte)chk; cat[29] = (byte)(chk >> 8);
    }

    private static void WriteEntry(Span<byte> e, bool bootable, ElToritoEmulation media, int sectorCount, uint loadRba)
    {
        e[0] = (byte)(bootable ? 0x88 : 0x00);
        e[1] = (byte)media;
        e[6] = (byte)sectorCount; e[7] = (byte)(sectorCount >> 8);
        e[8] = (byte)loadRba; e[9] = (byte)(loadRba >> 8); e[10] = (byte)(loadRba >> 16); e[11] = (byte)(loadRba >> 24);
    }

    private static byte[] BuildBootableIso(int catalogLba = 19)
    {
        var img = new byte[40 * SS];
        WriteBootRecord(img, (uint)catalogLba);
        var cat = img.AsSpan(catalogLba * SS, SS);
        WriteValidation(cat, ElToritoPlatform.X86, "DISCFORGE");
        WriteEntry(cat.Slice(32, 32), bootable: true, ElToritoEmulation.NoEmulation, sectorCount: 4, loadRba: 20);
        return img;
    }

    [Fact]
    public void Reads_a_bootable_disc_default_entry()
    {
        var cat = ElTorito.Read(BuildBootableIso());
        Assert.NotNull(cat);
        Assert.True(cat!.ChecksumValid);
        Assert.Equal(ElToritoPlatform.X86, cat.Platform);
        Assert.Equal("DISCFORGE", cat.ManufacturerId);
        Assert.Single(cat.Entries);
        Assert.True(cat.Entries[0].Bootable);
        Assert.Equal(ElToritoEmulation.NoEmulation, cat.Entries[0].Media);
        Assert.Equal(4, cat.Entries[0].SectorCount);
        Assert.Equal(20u, cat.Entries[0].LoadRba);
        Assert.Equal(0x07C0, cat.Entries[0].EffectiveLoadSegment);
    }

    [Fact]
    public void A_non_bootable_image_yields_null()
    {
        var img = new byte[40 * SS];   // no boot record at sector 17
        Assert.Null(ElTorito.Read(img));
    }

    [Fact]
    public void A_tiny_image_yields_null()
    {
        Assert.Null(ElTorito.Read(new byte[10 * SS]));
    }

    [Fact]
    public void A_corrupt_validation_checksum_is_reported()
    {
        var img = new byte[40 * SS];
        WriteBootRecord(img, 19);
        var cat = img.AsSpan(19 * SS, SS);
        WriteValidation(cat, ElToritoPlatform.X86, "DISCFORGE", goodChecksum: false);
        WriteEntry(cat.Slice(32, 32), true, ElToritoEmulation.NoEmulation, 4, 20);

        var result = ElTorito.Read(img);
        Assert.NotNull(result);
        Assert.False(result!.ChecksumValid);
    }

    [Fact]
    public void Parses_a_multi_boot_uefi_section()
    {
        var img = BuildBootableIso();
        var cat = img.AsSpan(19 * SS, SS);
        // Final section header (0x91) for one EFI entry after the default x86 entry.
        var hdr = cat.Slice(64, 32);
        hdr[0] = 0x91;
        hdr[1] = (byte)ElToritoPlatform.Efi;
        hdr[2] = 1; hdr[3] = 0;                          // one section entry
        Encoding.ASCII.GetBytes("UEFI").CopyTo(hdr[4..]);
        WriteEntry(cat.Slice(96, 32), bootable: true, ElToritoEmulation.NoEmulation, sectorCount: 100, loadRba: 24);

        var result = ElTorito.Read(img);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Entries.Count);
        Assert.Equal(ElToritoPlatform.Efi, result.Entries[1].Platform);
        Assert.Equal("UEFI", result.Entries[1].SectionId);
        Assert.Equal(24u, result.Entries[1].LoadRba);
    }

    [Fact]
    public void A_catalog_pointer_past_the_image_is_rejected()
    {
        var img = new byte[40 * SS];
        WriteBootRecord(img, 9999);   // pointer beyond the image
        Assert.Null(ElTorito.Read(img));
    }

    [Fact]
    public void Render_lists_every_entry()
    {
        var cat = ElTorito.Read(BuildBootableIso())!;
        string text = ElTorito.Render(cat);
        Assert.Contains("El Torito", text);
        Assert.Contains("bootable", text);
    }
}
