// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Rom;

/// <summary>
/// Game Boy / Game Boy Color cartridge header (at 0x100). The reader validates the 48-byte
/// Nintendo logo at 0x104 against the known constant (a mismatch is a warning, not a rejection),
/// then reads the title (0x134, up to 16 bytes — 11 for newer carts that follow it with a
/// manufacturer code), the CGB flag (0x143: 0x80 = enhanced, 0xC0 = GBC-only), SGB flag (0x146),
/// cartridge type (0x147), ROM size (0x148), RAM size (0x149) and destination (0x14A).
///
/// It recomputes the 8-bit header checksum (0x14D) over 0x134..0x14C and compares, and reads the
/// 16-bit global checksum (0x14E, big-endian). Both checks surface as warnings on mismatch.
/// </summary>
public static class GameBoyRom
{
    /// <summary>The 48-byte Nintendo boot logo every genuine Game Boy cartridge carries at 0x104.</summary>
    public static readonly byte[] NintendoLogo =
    {
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
        0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E, 0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
        0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    };

    public static RomId? TryRead(byte[] rom)
    {
        if (rom.Length < 0x150) return null;
        bool logoOk = LogoMatches(rom);
        byte headerChk = rom[0x14D];
        byte computed = ComputeHeaderChecksum(rom);
        // Require either a good logo or a good header checksum before claiming this is a GB ROM,
        // so an arbitrary 32 KiB buffer is not mislabelled.
        if (!logoOk && headerChk != computed) return null;

        byte cgb = rom[0x143];
        int titleLen = (cgb == 0x80 || cgb == 0xC0) ? 15 : 16;   // CGB flag steals the last title byte
        string title = RomIdentify.Ascii(rom.AsSpan(0x134, titleLen));

        var warnings = new List<string>();
        if (!logoOk) warnings.Add("Nintendo logo at 0x104 does not match — likely a bad dump or homebrew");
        if (headerChk != computed)
            warnings.Add($"header checksum mismatch: stored 0x{headerChk:X2}, computed 0x{computed:X2}");

        ushort globalChk = RomIdentify.U16Be(rom, 0x14E);
        string platform = (cgb == 0x80 || cgb == 0xC0) ? "Game Boy Color" : "Game Boy";

        var extra = new Dictionary<string, string>
        {
            ["CgbFlag"] = cgb switch { 0x80 => "GBC-enhanced (0x80)", 0xC0 => "GBC-only (0xC0)", _ => $"0x{cgb:X2}" },
            ["SgbFlag"] = rom[0x146] == 0x03 ? "SGB (0x03)" : $"0x{rom[0x146]:X2}",
            ["CartType"] = $"0x{rom[0x147]:X2}",
            ["RomSize"] = $"0x{rom[0x148]:X2} ({32 << rom[0x148]} KiB)",
            ["RamSize"] = $"0x{rom[0x149]:X2}",
            ["Destination"] = rom[0x14A] == 0 ? "Japanese (0x00)" : "Non-Japanese (0x01)",
            ["HeaderChecksum"] = $"stored 0x{headerChk:X2}, computed 0x{computed:X2}",
            ["GlobalChecksum"] = $"{globalChk:X4}",
        };

        return new RomId
        {
            Platform = platform,
            Title = title,
            Region = rom[0x14A] == 0 ? "Japan" : "World",
            Extra = extra,
            Warnings = warnings,
        };
    }

    public static bool LogoMatches(byte[] rom)
    {
        if (rom.Length < 0x104 + NintendoLogo.Length) return false;
        for (int i = 0; i < NintendoLogo.Length; i++)
            if (rom[0x104 + i] != NintendoLogo[i]) return false;
        return true;
    }

    /// <summary>The Game Boy header checksum: <c>x = 0; for i in 0x134..0x14C: x = x - rom[i] - 1</c>.</summary>
    public static byte ComputeHeaderChecksum(byte[] rom)
    {
        byte x = 0;
        for (int i = 0x134; i <= 0x14C; i++) x = (byte)(x - rom[i] - 1);
        return x;
    }
}
