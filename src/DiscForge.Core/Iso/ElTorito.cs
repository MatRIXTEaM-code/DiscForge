// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Iso;

/// <summary>The firmware/architecture a boot entry targets.</summary>
public enum ElToritoPlatform : byte { X86 = 0x00, PowerPc = 0x01, Mac = 0x02, Efi = 0xEF }

/// <summary>How the BIOS/firmware presents the boot image to the loaded code.</summary>
public enum ElToritoEmulation : byte
{
    NoEmulation = 0, Floppy1200 = 1, Floppy1440 = 2, Floppy2880 = 3, HardDisk = 4,
}

/// <summary>One boot option in the catalog — the default entry, or one drawn from a section.</summary>
public sealed record ElToritoBootEntry(
    bool Bootable, ElToritoPlatform Platform, ElToritoEmulation Media,
    int LoadSegment, byte SystemType, int SectorCount, uint LoadRba, string? SectionId)
{
    /// <summary>The default BIOS load segment (0x07C0) when the entry records 0.</summary>
    public int EffectiveLoadSegment => LoadSegment == 0 ? 0x07C0 : LoadSegment;

    public override string ToString()
    {
        string emu = Media switch
        {
            ElToritoEmulation.NoEmulation => "no-emulation",
            ElToritoEmulation.Floppy1200 => "1.2MB floppy",
            ElToritoEmulation.Floppy1440 => "1.44MB floppy",
            ElToritoEmulation.Floppy2880 => "2.88MB floppy",
            ElToritoEmulation.HardDisk => "hard disk",
            _ => $"media 0x{(byte)Media:X2}",
        };
        return $"{(Bootable ? "bootable" : "non-boot")} {Platform} {emu}: {SectorCount} sector(s) @ LBA {LoadRba}" +
               (SectionId is { Length: > 0 } ? $" [{SectionId}]" : "");
    }
}

/// <summary>The parsed El Torito boot catalog of a disc image.</summary>
public sealed record ElToritoCatalog
{
    public required ElToritoPlatform Platform { get; init; }
    public required string ManufacturerId { get; init; }
    /// <summary>The validation entry's 16-bit words must sum to zero — a false here means a corrupt catalog.</summary>
    public required bool ChecksumValid { get; init; }
    public required uint CatalogLba { get; init; }
    public required IReadOnlyList<ElToritoBootEntry> Entries { get; init; }

    public bool AnyBootable => Entries.Any(e => e.Bootable);

    public string Summary()
        => $"El Torito: platform {Platform}, {Entries.Count} entry(ies), " +
           $"validation {(ChecksumValid ? "OK" : "BAD")}" +
           (ManufacturerId.Length > 0 ? $", id \"{ManufacturerId}\"" : "") + ".";
}

/// <summary>
/// el-torito — the reader for a bootable CD/DVD's boot structure, the counterpart to the ISO 9660 volume
/// grammar. A bootable disc plants a Boot Record volume descriptor at sector 17 that points to a boot
/// catalog; the catalog opens with a validation entry (whose sixteen 16-bit words must sum to zero) and
/// then lists boot options — the default entry plus any platform sections for a multi-boot disc (a BIOS
/// x86 image and a UEFI image side by side, say). Each entry says whether it is bootable, what firmware it
/// targets, whether it emulates a floppy or hard disk or boots with no emulation, and where its boot image
/// lives. This finds the catalog, verifies the checksum, and decodes every entry.
///
/// It reads and reports the disc's own boot metadata for identification and preservation — it parses, it
/// changes nothing, and a non-bootable image simply yields null.
/// </summary>
public static class ElTorito
{
    private const int SectorSize = 2048;
    private const int BootRecordSector = 17;
    private static readonly byte[] BootSystemId = Encoding.ASCII.GetBytes("EL TORITO SPECIFICATION");

    /// <summary>Read the boot catalog from a 2048-byte/sector ISO image, or null if the image is not
    /// bootable (no El Torito boot record) or is too small.</summary>
    public static ElToritoCatalog? Read(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length % SectorSize != 0) return null;
        long sectors = image.Length / SectorSize;
        if (sectors <= BootRecordSector) return null;

        var br = image.AsSpan(BootRecordSector * SectorSize, SectorSize);
        if (br[0] != 0x00) return null;                                  // boot record type
        if (!(br[1] == 'C' && br[2] == 'D' && br[3] == '0' && br[4] == '0' && br[5] == '1')) return null;
        if (br[6] != 0x01) return null;                                  // version
        if (!br.Slice(7, BootSystemId.Length).SequenceEqual(BootSystemId)) return null;

        uint catalogLba = U32(br, 71);
        if (catalogLba == 0 || catalogLba >= sectors) return null;

        var block = image.AsSpan((int)(catalogLba * SectorSize), SectorSize);
        return ParseCatalog(block, catalogLba);
    }

    /// <summary>Parse a 2048-byte boot-catalog block (the unit-test seam and the reader's core).</summary>
    public static ElToritoCatalog ParseCatalog(ReadOnlySpan<byte> block, uint catalogLba = 0)
    {
        if (block.Length < 32) throw new ArgumentException("Boot catalog block is too small.", nameof(block));

        // ---- validation entry (offset 0) -----------------------------------
        var platform = (ElToritoPlatform)block[1];
        string manufacturer = Ascii(block.Slice(4, 24));
        bool keyOk = block[30] == 0x55 && block[31] == 0xAA && block[0] == 0x01;
        int sum = 0;
        for (int i = 0; i < 32; i += 2) sum += block[i] | (block[i + 1] << 8);
        bool checksumOk = keyOk && (sum & 0xFFFF) == 0;

        var entries = new List<ElToritoBootEntry>();

        // ---- initial / default entry (offset 32) ---------------------------
        int o = 32;
        if (o + 32 <= block.Length)
        {
            entries.Add(ParseEntry(block.Slice(o, 32), platform, sectionId: null));
            o += 32;
        }

        // ---- optional section headers + their entries ----------------------
        while (o + 32 <= block.Length)
        {
            byte id = block[o];
            if (id != 0x90 && id != 0x91) break;                         // 0x90 = more, 0x91 = final
            var sectionPlatform = (ElToritoPlatform)block[o + 1];
            int count = block[o + 2] | (block[o + 3] << 8);
            string sectionId = Ascii(block.Slice(o + 4, 28));
            o += 32;

            for (int k = 0; k < count && o + 32 <= block.Length; k++)
            {
                entries.Add(ParseEntry(block.Slice(o, 32), sectionPlatform, sectionId));
                o += 32;
            }
            if (id == 0x91) break;
        }

        return new ElToritoCatalog
        {
            Platform = platform,
            ManufacturerId = manufacturer,
            ChecksumValid = checksumOk,
            CatalogLba = catalogLba,
            Entries = entries,
        };
    }

    public static string Render(ElToritoCatalog c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var sb = new StringBuilder();
        sb.AppendLine(c.Summary());
        foreach (var e in c.Entries) sb.AppendLine($"  {e}");
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    private static ElToritoBootEntry ParseEntry(ReadOnlySpan<byte> e, ElToritoPlatform platform, string? sectionId)
    {
        bool bootable = e[0] == 0x88;
        var media = (ElToritoEmulation)(byte)(e[1] & 0x0F);
        int loadSeg = e[2] | (e[3] << 8);
        byte sysType = e[4];
        int sectorCount = e[6] | (e[7] << 8);
        uint loadRba = U32(e, 8);
        return new ElToritoBootEntry(bootable, platform, media, loadSeg, sysType, sectorCount, loadRba, sectionId);
    }

    private static uint U32(ReadOnlySpan<byte> b, int o)
        => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    private static string Ascii(ReadOnlySpan<byte> b)
    {
        int end = b.Length;
        while (end > 0 && (b[end - 1] == 0x00 || b[end - 1] == 0x20)) end--;
        return Encoding.ASCII.GetString(b[..end]);
    }
}
