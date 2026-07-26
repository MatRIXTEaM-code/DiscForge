// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Rom;

/// <summary>
/// Sega Master System / Game Gear. Both carry a "TMR SEGA" signature at 0x1FF0, 0x3FF0 or 0x7FF0
/// (the offset depends on ROM size). The byte at signature+0x0F splits into a region nibble (high)
/// and a ROM-size nibble (low); the region nibble tells SMS from Game Gear.
/// </summary>
public static class MasterSystemRom
{
    public static RomId? TryRead(byte[] rom)
    {
        foreach (int at in new[] { 0x1FF0, 0x3FF0, 0x7FF0 })
        {
            if (at + 16 > rom.Length) continue;
            if (!RomIdentify.AsciiEquals(rom, at, "TMR SEGA")) continue;

            byte regionSize = rom[at + 0x0F];
            int regionNibble = regionSize >> 4;
            int sizeNibble = regionSize & 0x0F;
            ushort checksum = RomIdentify.U16Le(rom, at + 0x0A);

            (string platform, string region) = regionNibble switch
            {
                3 => ("Sega Master System", "Japan"),
                4 => ("Sega Master System", "Export"),
                5 => ("Sega Game Gear", "Japan"),
                6 => ("Sega Game Gear", "Export"),
                7 => ("Sega Game Gear", "International"),
                _ => ("Sega Master System / Game Gear", ""),
            };

            var extra = new Dictionary<string, string>
            {
                ["SignatureOffset"] = $"0x{at:X}",
                ["RegionNibble"] = regionNibble.ToString(),
                ["RomSizeCode"] = $"0x{sizeNibble:X}",
                ["Checksum"] = $"{checksum:X4}",
            };
            return new RomId { Platform = platform, Region = region, Extra = extra };
        }
        return null;
    }
}

/// <summary>
/// Bandai WonderSwan / WonderSwan Color. The internal header is the last 16 bytes of the file:
/// developer id (+0x00), colour flag (+0x01), cart id (+0x02), and a 16-bit checksum (+0x0E, LE)
/// that is the low 16 bits of the sum of every preceding byte. That checksum is used as the
/// identity gate, since WonderSwan has no offset-0 magic.
/// </summary>
public static class WonderSwanRom
{
    public static RomId? TryRead(byte[] rom)
    {
        // WonderSwan mask ROMs are power-of-two sizes from ~4 Mbit up to 128 Mbit (16 MB).
        // Gating on that (plus a non-zero checksum) stops large disc tracks and arbitrary
        // data from matching on the 16-bit checksum alone — a 37 MB CD track is not a cart.
        if (rom.Length < 1024 || rom.Length > 16 * 1024 * 1024) return null;
        if ((rom.Length & (rom.Length - 1)) != 0) return null;   // must be a power of two
        int f = rom.Length - 16;                            // start of the 16-byte footer
        ushort stored = RomIdentify.U16Le(rom, f + 0x0E);
        if (stored == 0) return null;                       // a real header's checksum is never zero

        int sum = 0;
        for (int i = 0; i < rom.Length - 2; i++) sum += rom[i];
        if ((ushort)sum != stored) return null;             // conservative: require the checksum to hold

        byte colour = rom[f + 0x01];
        var extra = new Dictionary<string, string>
        {
            ["Developer"] = $"0x{rom[f + 0x00]:X2}",
            ["CartId"] = $"0x{rom[f + 0x02]:X2}",
            ["Checksum"] = $"{stored:X4}",
        };
        return new RomId
        {
            Platform = colour == 0x00 ? "WonderSwan" : "WonderSwan Color",
            Extra = extra,
        };
    }
}

/// <summary>
/// SNK Neo Geo Pocket / Color. Begins with a 28-byte licence string, either
/// "COPYRIGHT BY SNK CORPORATION" or "LICENSED BY SNK CORPORATION". The 12-byte game name sits at
/// 0x24 and the colour flag at 0x23 (0x10 = colour).
/// </summary>
public static class NeoGeoPocketRom
{
    private const string Copyright = "COPYRIGHT BY SNK CORPORATION";
    private const string Licensed = "LICENSED BY SNK CORPORATION";

    public static RomId? TryRead(byte[] rom)
    {
        if (rom.Length < 0x30) return null;
        bool copyright = RomIdentify.AsciiEquals(rom, 0, Copyright);
        if (!copyright && !RomIdentify.AsciiEquals(rom, 0, Licensed)) return null;

        byte colour = rom[0x23];
        var extra = new Dictionary<string, string>
        {
            ["Licence"] = copyright ? "COPYRIGHT BY SNK CORPORATION" : "LICENSED BY SNK CORPORATION",
            ["ColourFlag"] = $"0x{colour:X2}",
        };
        return new RomId
        {
            Platform = colour == 0x10 ? "Neo Geo Pocket Color" : "Neo Geo Pocket",
            Title = RomIdentify.Ascii(rom.AsSpan(0x24, 12)),
            Extra = extra,
        };
    }
}

/// <summary>
/// Atari Lynx .lnx image. The 64-byte header begins with the ASCII magic "LYNX"; the cartridge
/// name (32 bytes at 0x06) and manufacturer (16 bytes at 0x26) follow.
/// </summary>
public static class LynxRom
{
    public static RomId? TryRead(byte[] rom)
    {
        if (rom.Length < 0x40) return null;
        if (!RomIdentify.AsciiEquals(rom, 0, "LYNX")) return null;

        var extra = new Dictionary<string, string>
        {
            ["Manufacturer"] = RomIdentify.Ascii(rom.AsSpan(0x26, 16)),
        };
        return new RomId
        {
            Platform = "Atari Lynx",
            Title = RomIdentify.Ascii(rom.AsSpan(0x06, 32)),
            Extra = extra,
        };
    }
}

/// <summary>
/// Atari 7800 .a78 image. The header carries the ASCII tag "ATARI7800" starting at offset 1
/// (byte 0 is the header version). The 32-byte cartridge title sits at 0x11.
/// </summary>
public static class Atari7800Rom
{
    public static RomId? TryRead(byte[] rom)
    {
        if (rom.Length < 0x40) return null;
        if (!RomIdentify.AsciiEquals(rom, 1, "ATARI7800")) return null;

        var extra = new Dictionary<string, string>
        {
            ["HeaderVersion"] = rom[0].ToString(),
        };
        return new RomId
        {
            Platform = "Atari 7800",
            Title = RomIdentify.Ascii(rom.AsSpan(0x11, 32)),
            Extra = extra,
        };
    }
}

/// <summary>
/// Nintendo DS card image. Identified by the Nintendo-logo CRC-16 at 0x15C, a fixed 0xCF56 in
/// every genuine card. The reader then reads the 12-byte game title (0x00), 4-byte game code
/// (0x0C) and 2-byte maker code (0x10).
/// </summary>
public static class NintendoDsRom
{
    public static RomId? TryRead(byte[] rom)
    {
        if (rom.Length < 0x160) return null;
        ushort logoCrc = RomIdentify.U16Le(rom, 0x15C);
        if (logoCrc != 0xCF56) return null;

        string gameCode = RomIdentify.Ascii(rom.AsSpan(0x0C, 4));
        var extra = new Dictionary<string, string>
        {
            ["Maker"] = RomIdentify.Ascii(rom.AsSpan(0x10, 2)),
            ["LogoCrc"] = $"{logoCrc:X4}",
        };
        return new RomId
        {
            Platform = "Nintendo DS",
            Title = RomIdentify.Ascii(rom.AsSpan(0x00, 12)),
            GameCode = gameCode,
            Region = gameCode.Length == 4 ? RegionFromCode(gameCode[3]) : "",
            Extra = extra,
        };
    }

    private static string RegionFromCode(char c) => c switch
    {
        'E' => "USA",
        'J' => "Japan",
        'P' => "Europe",
        'D' => "Germany",
        'F' => "France",
        'I' => "Italy",
        'S' => "Spain",
        'K' => "Korea",
        'O' => "World",
        _ => "",
    };
}
