// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Rom;

/// <summary>
/// Super Nintendo (SNES / Super Famicom) cartridge header. The SNES has no offset-0 magic, so
/// the reader locates the header by validating its checksum: the internal header sits at 0x7FC0
/// (LoROM), 0xFFC0 (HiROM) or 0x40FFC0 (ExHiROM), and the layout whose 16-bit checksum and
/// complement (LE, at header+0x1C / header+0x1E) sum to 0xFFFF is the real one.
///
/// An optional 512-byte copier (SMC/SWC) header is present when <c>len % 1024 == 512</c>; it is
/// skipped before locating the header. The reader reads the 21-byte title (header+0x00), the
/// mapping/speed byte (0x15), ROM size (0x17), RAM size (0x18) and country (0x19), and reports
/// the chosen layout.
/// </summary>
public static class SnesRom
{
    private const int LoRom = 0x7FC0, HiRom = 0xFFC0, ExHiRom = 0x40FFC0;

    public static RomId? TryRead(byte[] rom)
    {
        int skip = SmcHeaderSize(rom.Length);
        int start = skip;                       // start of the actual ROM data
        int dataLen = rom.Length - start;
        if (dataLen < 0x8000) return null;

        // Score each candidate layout by checksum validity; the first valid wins in the usual
        // LoROM->HiROM->ExHiROM order.
        foreach (var (layout, off) in new[] { ("LoROM", LoRom), ("HiROM", HiRom), ("ExHiROM", ExHiRom) })
        {
            int hdr = start + off;
            if (hdr + 0x20 > rom.Length) continue;
            ushort checksum = RomIdentify.U16Le(rom, hdr + 0x1C);
            ushort complement = RomIdentify.U16Le(rom, hdr + 0x1E);
            if ((ushort)(checksum + complement) != 0xFFFF) continue;
            if (!PlausibleTitle(rom, hdr)) continue;

            var warnings = new List<string>();
            if (skip != 0) warnings.Add($"skipped {skip}-byte SMC copier header before parsing");

            var extra = new Dictionary<string, string>
            {
                ["Layout"] = layout,
                ["MapMode"] = $"0x{rom[hdr + 0x15]:X2}",
                ["RomSize"] = $"{1 << rom[hdr + 0x17]} KiB (code 0x{rom[hdr + 0x17]:X2})",
                ["RamSize"] = rom[hdr + 0x18] == 0 ? "none" : $"{1 << rom[hdr + 0x18]} KiB (code 0x{rom[hdr + 0x18]:X2})",
                ["CountryCode"] = $"0x{rom[hdr + 0x19]:X2}",
                ["Checksum"] = $"{checksum:X4}",
                ["Complement"] = $"{complement:X4}",
                ["CopierHeader"] = skip == 0 ? "none" : $"{skip} bytes (excluded from hashing)",
            };

            return new RomId
            {
                Platform = "SNES",
                Title = RomIdentify.Ascii(rom.AsSpan(hdr + 0x00, 21)),
                Region = Region(rom[hdr + 0x19]),
                Extra = extra,
                Warnings = warnings,
            };
        }
        return null;
    }

    /// <summary>Size of a copier header to skip (512 when <c>len % 1024 == 512</c>, else 0).</summary>
    public static int SmcHeaderSize(int length) => (length % 1024) == 512 ? 512 : 0;

    // The title field is 21 bytes of the cartridge name; require it to be mostly printable so a
    // random buffer that happens to satisfy the checksum sum does not masquerade as SNES.
    private static bool PlausibleTitle(byte[] rom, int hdr)
    {
        int printable = 0;
        for (int i = 0; i < 21; i++)
        {
            byte b = rom[hdr + i];
            if (b >= 0x20 && b <= 0x7E) printable++;
            else if (b != 0x00) return false;
        }
        return printable >= 1;
    }

    private static string Region(byte c) => c switch
    {
        0x00 => "Japan",
        0x01 => "USA",
        0x02 => "Europe",
        0x03 => "Sweden/Scandinavia",
        0x06 => "France",
        0x07 => "Netherlands",
        0x08 => "Spain",
        0x09 => "Germany",
        0x0A => "Italy",
        0x0B => "China",
        0x0D => "Korea",
        0x0F => "Canada",
        0x10 => "Brazil",
        0x11 => "Australia",
        _ => "",
    };
}
