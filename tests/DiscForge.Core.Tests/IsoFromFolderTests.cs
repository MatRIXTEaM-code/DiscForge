// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Files;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// iso-create builds a standard ISO 9660 image from a folder. The test builds an image from a small tree (a
/// root file, a nested file, and a binary blob), then reads it back with DiscForge's own image browser and
/// confirms the volume identifier, the file tree, and that a file's bytes survive the round-trip exactly.
/// </summary>
public class IsoFromFolderTests
{
    [Fact]
    public void Builds_a_readable_iso_that_round_trips_the_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "dforge_iso_" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(Path.Combine(src, "docs"));
            Directory.CreateDirectory(Path.Combine(src, "data"));
            File.WriteAllText(Path.Combine(src, "readme.txt"), "hello world");
            File.WriteAllText(Path.Combine(src, "docs", "intro.txt"), "chapter one");
            var blob = new byte[5000];
            for (int i = 0; i < blob.Length; i++) blob[i] = (byte)((i * 37 + 11) % 256);
            File.WriteAllBytes(Path.Combine(src, "data", "blob.bin"), blob);

            var iso = Path.Combine(root, "out.iso");
            IsoFromFolderResult result;
            using (var os = File.Create(iso))
                result = IsoFromFolder.Write(src, "MYDISC", os);

            Assert.Equal("MYDISC", result.VolumeId);
            Assert.Equal(3, result.Files);
            Assert.Equal(2, result.Directories);

            var listing = ImageBrowser.List(iso);
            Assert.Null(listing.Error);
            var names = listing.Files.Select(f => f.Path.Replace('\\', '/').TrimStart('/')).ToList();
            Assert.Contains(names, n => n.EndsWith("readme.txt"));
            Assert.Contains(names, n => n.EndsWith("docs/intro.txt"));
            Assert.Contains(names, n => n.EndsWith("data/blob.bin"));

            // The binary blob's bytes must come back identical.
            var blobEntry = listing.Files.First(f => f.Path.EndsWith("blob.bin", StringComparison.OrdinalIgnoreCase));
            var dest = Path.Combine(root, "out");
            ImageBrowser.Extract(iso, new[] { blobEntry }, dest, null);
            var extracted = Directory.EnumerateFiles(dest, "blob.bin", SearchOption.AllDirectories).First();
            Assert.Equal(blob, File.ReadAllBytes(extracted));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void An_empty_folder_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_iso_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            using var os = new MemoryStream();
            Assert.Throws<InvalidOperationException>(() => IsoFromFolder.Write(dir, "EMPTY", os));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A --boot image makes an El Torito bootable disc: the boot loader the caller supplies must round-trip
    /// byte-exact at the sector the boot catalog points to, the validation entry's 16-bit words must sum to
    /// zero with the 0x55AA key, and the default entry must be flagged bootable (0x88). This proves the boot
    /// wiring is real, not just a larger file.
    /// </summary>
    [Fact]
    public void A_boot_image_produces_a_valid_el_torito_catalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "dforge_iso_" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "readme.txt"), "bootable disc");
            var loaderBytes = new byte[3000];
            for (int i = 0; i < loaderBytes.Length; i++) loaderBytes[i] = (byte)((i * 13 + 7) % 256);
            var loader = Path.Combine(root, "loader.img");
            File.WriteAllBytes(loader, loaderBytes);

            using var ms = new MemoryStream();
            var result = IsoFromFolder.Write(src, "BOOTCD", ms, bootImagePath: loader);
            Assert.Equal("loader.img", result.BootImage);
            var iso = ms.ToArray();
            const int SS = 2048;

            // Find the Boot Record Volume Descriptor among the descriptors starting at sector 16.
            int brvd = -1;
            for (int s = 16; ; s++)
            {
                var d = new ReadOnlySpan<byte>(iso, s * SS, 6);
                if (!d.Slice(1, 5).SequenceEqual("CD001"u8)) break;
                if (d[0] == 0) brvd = s;
                if (d[0] == 255) break;
            }
            Assert.True(brvd > 0, "no Boot Record VD");
            var vd = new ReadOnlySpan<byte>(iso, brvd * SS, SS);
            Assert.True(vd.Slice(7, 23).SequenceEqual("EL TORITO SPECIFICATION"u8));

            int catSec = BitConverter.ToInt32(iso, brvd * SS + 71);
            int catOff = catSec * SS;
            // Validation entry: header 1, key 0x55 0xAA, and 16-bit words summing to zero.
            Assert.Equal(1, iso[catOff]);
            Assert.Equal(0x55, iso[catOff + 30]);
            Assert.Equal(0xAA, iso[catOff + 31]);
            int sum = 0;
            for (int i = 0; i < 32; i += 2) sum += BitConverter.ToUInt16(iso, catOff + i);
            Assert.Equal(0, sum & 0xFFFF);

            // Default entry marked bootable, image LBA points at a byte-exact copy of the loader.
            Assert.Equal(0x88, iso[catOff + 32]);
            int imgLba = BitConverter.ToInt32(iso, catOff + 32 + 8);
            Assert.Equal(loaderBytes, new ReadOnlySpan<byte>(iso, imgLba * SS, loaderBytes.Length).ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void An_empty_boot_image_is_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "dforge_iso_" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "readme.txt"), "x");
            var loader = Path.Combine(root, "empty.img");
            File.WriteAllBytes(loader, Array.Empty<byte>());
            using var ms = new MemoryStream();
            Assert.Throws<InvalidOperationException>(() => IsoFromFolder.Write(src, "BOOTCD", ms, bootImagePath: loader));
        }
        finally { Directory.Delete(root, true); }
    }
}
