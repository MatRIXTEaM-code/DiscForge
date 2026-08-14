// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// El Torito boot-catalog authoring: a BIOS image, a UEFI image, or BOTH. The test that matters is
/// the hybrid — an x86 initial entry plus a final EFI section — read straight back through the
/// existing <see cref="ElTorito"/> parser: one disc that a legacy BIOS and UEFI firmware both boot.
/// UEFI-only must flip the validation platform to EFI; BIOS-only must stay exactly as before.
/// </summary>
public class IsoBootCatalogTests
{
    private static (string content, string bios, string efi, string root) Fixture(int biosLen, int efiLen)
    {
        string root = Path.Combine(Path.GetTempPath(), "dforge-iso-" + Guid.NewGuid().ToString("N"));
        string content = Path.Combine(root, "content");
        Directory.CreateDirectory(content);
        File.WriteAllBytes(Path.Combine(content, "readme.txt"), new byte[] { 1, 2, 3, 4, 5 });
        string bios = Path.Combine(root, "bios.img");
        string efi = Path.Combine(root, "efi.img");
        File.WriteAllBytes(bios, Enumerable.Repeat((byte)0xB1, biosLen).ToArray());
        File.WriteAllBytes(efi, Enumerable.Repeat((byte)0xE1, efiLen).ToArray());
        return (content, bios, efi, root);
    }

    [Fact]
    public void Bios_uefi_hybrid_catalog_round_trips()
    {
        var (content, bios, efi, root) = Fixture(1024, 2048);   // 2 × 512  and  4 × 512
        try
        {
            using var ms = new MemoryStream();
            IsoFromFolder.Write(content, "TESTVOL", ms, joliet: true, rockRidge: false,
                bootImagePath: bios, bootMedia: IsoBuilder.BootMediaType.NoEmulation, efiBootImagePath: efi);

            var cat = ElTorito.Read(ms.ToArray());
            Assert.NotNull(cat);
            Assert.True(cat!.ChecksumValid);
            Assert.Equal(ElToritoPlatform.X86, cat.Platform);          // validation = x86 (BIOS initial)
            Assert.Equal(2, cat.Entries.Count);
            Assert.Contains(cat.Entries, e => e.Platform == ElToritoPlatform.X86 && e.Bootable && e.SectorCount == 2);
            Assert.Contains(cat.Entries, e => e.Platform == ElToritoPlatform.Efi && e.Bootable && e.SectorCount == 4);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Uefi_only_makes_an_efi_platform_initial_entry()
    {
        var (content, _, efi, root) = Fixture(1024, 2048);
        try
        {
            using var ms = new MemoryStream();
            IsoFromFolder.Write(content, "TESTVOL", ms, joliet: true, rockRidge: false,
                bootImagePath: null, efiBootImagePath: efi);

            var cat = ElTorito.Read(ms.ToArray());
            Assert.NotNull(cat);
            Assert.True(cat!.ChecksumValid);
            Assert.Equal(ElToritoPlatform.Efi, cat.Platform);
            Assert.Single(cat.Entries);
            Assert.Equal(ElToritoPlatform.Efi, cat.Entries[0].Platform);
            Assert.True(cat.Entries[0].Bootable);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Bios_only_stays_a_single_x86_entry()
    {
        var (content, bios, _, root) = Fixture(1024, 2048);
        try
        {
            using var ms = new MemoryStream();
            IsoFromFolder.Write(content, "TESTVOL", ms, joliet: true, rockRidge: false, bootImagePath: bios);

            var cat = ElTorito.Read(ms.ToArray());
            Assert.NotNull(cat);
            Assert.Equal(ElToritoPlatform.X86, cat!.Platform);
            Assert.Single(cat.Entries);
            Assert.Equal(ElToritoPlatform.X86, cat.Entries[0].Platform);
        }
        finally { Directory.Delete(root, true); }
    }
}
