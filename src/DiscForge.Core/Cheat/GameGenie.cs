// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Cheat;

/// <summary>
/// Game Genie code decode/encode for the NES, SNES, Genesis/Mega Drive and Game Boy
/// Game Genie devices. Every routine is a pure transform between a code string and a
/// <see cref="CheatCode"/> (address / value / optional compare); decode and encode are
/// exact inverses, so a code round-trips.
///
/// The bit layouts implemented here are the published Game Genie code *formats* — the
/// nibble/bit shuffles the devices use — not anyone's source. Nothing here defeats copy
/// protection: it only translates a printed code into the address+value it names.
/// </summary>
public static class GameGenie
{
    // ---- Alphabets -------------------------------------------------------

    /// <summary>NES: 16 letters, each a 4-bit value (index = nibble).</summary>
    private const string NesAlphabet = "APZLGITYEOXUKSVN";

    /// <summary>SNES: 16 symbols, each a 4-bit value.</summary>
    private const string SnesAlphabet = "DF4709156BC8A23E";

    /// <summary>Genesis / Mega Drive: 32 symbols, each a 5-bit value (I,O,Q,U omitted).</summary>
    private const string GenesisAlphabet = "ABCDEFGHJKLMNPRSTVWXYZ0123456789";

    // ---- Dispatcher ------------------------------------------------------

    /// <summary>Decode a Game Genie code for the given platform.</summary>
    public static CheatCode Decode(CheatPlatform platform, string code) => platform switch
    {
        CheatPlatform.Nes => DecodeNes(code),
        CheatPlatform.Snes => DecodeSnes(code),
        CheatPlatform.Genesis => DecodeGenesis(code),
        CheatPlatform.GameBoy => DecodeGameBoy(code),
        _ => throw new CheatFormatException($"{platform} is not a Game Genie platform."),
    };

    /// <summary>Encode a cheat back into a Game Genie code for its platform.</summary>
    public static string Encode(CheatCode code) => code.Platform switch
    {
        CheatPlatform.Nes => EncodeNes(code),
        CheatPlatform.Snes => EncodeSnes(code),
        CheatPlatform.Genesis => EncodeGenesis(code),
        CheatPlatform.GameBoy => EncodeGameBoy(code),
        _ => throw new CheatFormatException($"{code.Platform} is not a Game Genie platform."),
    };

    // =====================================================================
    //  NES
    // =====================================================================
    //
    //  6 letters = 15-bit CPU address (OR'd with 0x8000) + 8-bit data.
    //  8 letters add an 8-bit compare byte.  Each letter is a 4-bit value; bit 3
    //  (0x8) of most letters is a "chain" bit that belongs to a neighbouring field.
    //  The high bit of the 3rd letter is the length flag: 0 = 6-letter, 1 = 8-letter.
    //
    //  Address bits:  a0-2=n4lo a3=n3.3 a4-6=n2lo a7=n1.3 a8-10=n5lo a11=n4.3 a12-14=n3lo
    //  Data bits:     d0-2=n0lo d4-6=n1lo d7=n0.3  ; d3 = n5.3 (6-letter) or n7.3 (8-letter)
    //  Compare bits:  c0-2=n6lo c4-6=n7lo c7=n6.3  ; c3 = n5.3

    /// <summary>Decode a 6- or 8-letter NES Game Genie code.</summary>
    public static CheatCode DecodeNes(string code)
    {
        int[] n = DecodeLetters(code, NesAlphabet, "NES Game Genie", len => len is 6 or 8);

        long address = 0x8000
            | ((long)(n[3] & 7) << 12)
            | ((long)(n[5] & 7) << 8) | ((long)(n[4] & 8) << 8)
            | ((long)(n[2] & 7) << 4) | ((long)(n[1] & 8) << 4)
            | (uint)(n[4] & 7) | (uint)(n[3] & 8);

        if (n.Length == 6)
        {
            long data = ((n[1] & 7) << 4) | ((n[0] & 8) << 4) | (n[0] & 7) | (n[5] & 8);
            return new CheatCode { Platform = CheatPlatform.Nes, Address = address, Value = data };
        }

        long data8 = ((n[1] & 7) << 4) | ((n[0] & 8) << 4) | (n[0] & 7) | (n[7] & 8);
        long compare = ((n[7] & 7) << 4) | ((n[6] & 8) << 4) | (n[6] & 7) | (n[5] & 8);
        return new CheatCode
        {
            Platform = CheatPlatform.Nes,
            Address = address,
            Value = data8,
            Compare = compare,
        };
    }

    /// <summary>Encode an NES cheat into a 6-letter (no compare) or 8-letter (compare) code.</summary>
    public static string EncodeNes(CheatCode code)
    {
        if (code.Address is < 0x8000 or > 0xFFFF)
            throw new CheatFormatException(
                $"NES Game Genie address must be 0x8000-0xFFFF, got 0x{code.Address:X}.");
        RequireByte(code.Value, "NES value");
        if (code.Compare is { } cmp) RequireByte(cmp, "NES compare");

        int a = (int)(code.Address & 0x7FFF);
        int d = (int)(code.Value & 0xFF);

        int[] n = new int[code.Compare.HasValue ? 8 : 6];
        n[0] = (d & 7) | (((d >> 7) & 1) << 3);
        n[1] = ((d >> 4) & 7) | (((a >> 7) & 1) << 3);
        n[2] = ((a >> 4) & 7) | (code.Compare.HasValue ? 8 : 0);
        n[3] = ((a >> 12) & 7) | (((a >> 3) & 1) << 3);
        n[4] = (a & 7) | (((a >> 11) & 1) << 3);

        if (!code.Compare.HasValue)
        {
            n[5] = ((a >> 8) & 7) | (((d >> 3) & 1) << 3);
        }
        else
        {
            int c = (int)(code.Compare.Value & 0xFF);
            n[5] = ((a >> 8) & 7) | (((c >> 3) & 1) << 3);
            n[6] = (c & 7) | (((c >> 7) & 1) << 3);
            n[7] = ((c >> 4) & 7) | (((d >> 3) & 1) << 3);
        }

        return LettersToString(n, NesAlphabet);
    }

    // =====================================================================
    //  SNES
    // =====================================================================
    //
    //  8 symbols (4 bits each) = 32 bits.  The first byte (symbols 0-1) is the data;
    //  the remaining 24 bits carry the address, bit-scrambled per the published SNES
    //  table.  Data lives in the high 8 bits of the raw word.

    // Real-address bit i is taken from raw24 bit SnesAddrMap[i]. A bijection of 0..23.
    private static readonly int[] SnesAddrMap =
    {
        // low nibble .. high nibble (published SNES address scramble, nibble-shuffled)
        16, 17, 18, 19,   8,  9, 10, 11,   0,  1,  2,  3,
        12, 13, 14, 15,  20, 21, 22, 23,   4,  5,  6,  7,
    };

    /// <summary>Decode an 8-symbol SNES Game Genie code.</summary>
    public static CheatCode DecodeSnes(string code)
    {
        int[] n = DecodeLetters(code, SnesAlphabet, "SNES Game Genie", len => len == 8);

        int data = (n[0] << 4) | n[1];
        int raw24 = (n[2] << 20) | (n[3] << 16) | (n[4] << 12) | (n[5] << 8) | (n[6] << 4) | n[7];
        long address = Permute(raw24, SnesAddrMap);

        return new CheatCode { Platform = CheatPlatform.Snes, Address = address, Value = data };
    }

    /// <summary>Encode an SNES cheat into an 8-symbol code.</summary>
    public static string EncodeSnes(CheatCode code)
    {
        if (code.Address is < 0 or > 0xFFFFFF)
            throw new CheatFormatException(
                $"SNES Game Genie address must be 0x000000-0xFFFFFF, got 0x{code.Address:X}.");
        RequireByte(code.Value, "SNES value");
        if (code.Compare.HasValue)
            throw new CheatFormatException("SNES Game Genie codes have no compare byte.");

        int raw24 = (int)Unpermute(code.Address, SnesAddrMap);
        int d = (int)(code.Value & 0xFF);

        int[] n =
        {
            (d >> 4) & 0xF, d & 0xF,
            (raw24 >> 20) & 0xF, (raw24 >> 16) & 0xF, (raw24 >> 12) & 0xF,
            (raw24 >> 8) & 0xF, (raw24 >> 4) & 0xF, raw24 & 0xF,
        };
        return LettersToString(n, SnesAlphabet);
    }

    // =====================================================================
    //  Genesis / Mega Drive
    // =====================================================================
    //
    //  "XXXX-YYYY" = 8 symbols (5 bits each) = 40 bits = 24-bit address + 16-bit data,
    //  bit-shuffled per the published Genesis table.

    // Output bit i (0..23 = address, 24..39 = data) is taken from raw40 bit GenesisMap[i].
    private static readonly int[] GenesisMap =
    {
        // address (24 bits)
        3,  4,  5,  6,  7,  35, 36, 37,  38, 39, 20, 21,
        22, 23, 24, 15, 16, 17,  18, 19,  8,  9, 10, 11,
        // data (16 bits)
        25, 26, 27, 28, 29, 30, 31, 32,  33, 34,  0,  1,
        2, 12, 13, 14,
    };

    /// <summary>Decode a Genesis / Mega Drive Game Genie code ("XXXX-YYYY").</summary>
    public static CheatCode DecodeGenesis(string code)
    {
        int[] n = DecodeLetters(code, GenesisAlphabet, "Genesis Game Genie", len => len == 8);

        long raw40 = 0;
        for (int i = 0; i < 8; i++) raw40 = (raw40 << 5) | (uint)n[i];

        long combined = Permute(raw40, GenesisMap);
        long address = combined & 0xFFFFFF;
        long data = (combined >> 24) & 0xFFFF;

        return new CheatCode { Platform = CheatPlatform.Genesis, Address = address, Value = data };
    }

    /// <summary>Encode a Genesis cheat into a "XXXX-YYYY" code.</summary>
    public static string EncodeGenesis(CheatCode code)
    {
        if (code.Address is < 0 or > 0xFFFFFF)
            throw new CheatFormatException(
                $"Genesis Game Genie address must be 0x000000-0xFFFFFF, got 0x{code.Address:X}.");
        if (code.Value is < 0 or > 0xFFFF)
            throw new CheatFormatException(
                $"Genesis Game Genie value must be a 16-bit word, got 0x{code.Value:X}.");
        if (code.Compare.HasValue)
            throw new CheatFormatException("Genesis Game Genie codes have no compare word.");

        long combined = (code.Address & 0xFFFFFF) | ((code.Value & 0xFFFF) << 24);
        long raw40 = Unpermute(combined, GenesisMap);

        int[] n = new int[8];
        for (int i = 7; i >= 0; i--) { n[i] = (int)(raw40 & 0x1F); raw40 >>= 5; }

        string s = LettersToString(n, GenesisAlphabet);
        return s[..4] + "-" + s[4..];
    }

    // =====================================================================
    //  Game Boy / Game Boy Color
    // =====================================================================
    //
    //  "ABC-DEF-GHI" = 9 hex digits.  AB = value.  The address is 16 bits from digits
    //  C,D,E,F where the high nibble (from F) is XOR'd with 0xF and C,D,E are the low
    //  12 bits.  A 6-digit code (AB-CDE-F, no last group) has no compare.  A 9-digit
    //  code carries an 8-bit compare in digits G and I (rotate-right-2 then XOR 0xBA);
    //  digit H is a check nibble, ignored on decode.

    /// <summary>Decode a 6- or 9-digit Game Boy Game Genie code.</summary>
    public static CheatCode DecodeGameBoy(string code)
    {
        int[] d = DecodeHexDigits(code, "Game Boy Game Genie", len => len is 6 or 9);

        int value = (d[0] << 4) | d[1];
        long address = ((d[5] ^ 0xF) << 12) | (d[2] << 8) | (d[3] << 4) | d[4];

        if (d.Length == 6)
            return new CheatCode { Platform = CheatPlatform.GameBoy, Address = address, Value = value };

        int t = (d[6] << 4) | d[8];
        int rotated = ((t >> 2) | (t << 6)) & 0xFF;   // rotate right 2
        long compare = rotated ^ 0xBA;
        return new CheatCode
        {
            Platform = CheatPlatform.GameBoy,
            Address = address,
            Value = value,
            Compare = compare,
        };
    }

    /// <summary>Encode a Game Boy cheat into a 6-digit (no compare) or 9-digit (compare) code.</summary>
    public static string EncodeGameBoy(CheatCode code)
    {
        if (code.Address is < 0 or > 0xFFFF)
            throw new CheatFormatException(
                $"Game Boy Game Genie address must be 0x0000-0xFFFF, got 0x{code.Address:X}.");
        RequireByte(code.Value, "Game Boy value");
        if (code.Compare is { } cmp) RequireByte(cmp, "Game Boy compare");

        int addr = (int)(code.Address & 0xFFFF);
        int val = (int)(code.Value & 0xFF);

        int[] d = new int[code.Compare.HasValue ? 9 : 6];
        d[0] = (val >> 4) & 0xF;
        d[1] = val & 0xF;
        d[2] = (addr >> 8) & 0xF;
        d[3] = (addr >> 4) & 0xF;
        d[4] = addr & 0xF;
        d[5] = ((addr >> 12) & 0xF) ^ 0xF;

        if (code.Compare.HasValue)
        {
            int c = (int)(code.Compare.Value & 0xFF) ^ 0xBA;
            int t = ((c << 2) | (c >> 6)) & 0xFF;   // rotate left 2 (inverse of decode)
            d[6] = (t >> 4) & 0xF;
            d[8] = t & 0xF;
            d[7] = (d[2] ^ 0x8) & 0xF;              // canonical check nibble (ignored on decode)
        }

        string s = LettersToString(d, "0123456789ABCDEF");
        return d.Length == 6
            ? s[..3] + "-" + s[3..]                     // ABC-DEF
            : s[..3] + "-" + s[3..6] + "-" + s[6..];    // ABC-DEF-GHI
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    /// <summary>result bit i = value bit map[i] (map is a bijection over the bit range).</summary>
    private static long Permute(long value, int[] map)
    {
        long result = 0;
        for (int i = 0; i < map.Length; i++)
            result |= ((value >> map[i]) & 1L) << i;
        return result;
    }

    /// <summary>Inverse of <see cref="Permute"/>: value bit map[i] = result bit i.</summary>
    private static long Unpermute(long result, int[] map)
    {
        long value = 0;
        for (int i = 0; i < map.Length; i++)
            value |= ((result >> i) & 1L) << map[i];
        return value;
    }

    private static int[] DecodeLetters(string code, string alphabet, string label, Func<int, bool> lenOk)
    {
        string clean = Clean(code);
        if (!lenOk(clean.Length))
            throw new CheatFormatException($"{label} code has an invalid length ({clean.Length}): '{code}'.");

        int[] n = new int[clean.Length];
        for (int i = 0; i < clean.Length; i++)
        {
            int idx = alphabet.IndexOf(char.ToUpperInvariant(clean[i]));
            if (idx < 0)
                throw new CheatFormatException($"'{clean[i]}' is not a valid {label} symbol.");
            n[i] = idx;
        }
        return n;
    }

    private static int[] DecodeHexDigits(string code, string label, Func<int, bool> lenOk)
    {
        string clean = Clean(code);
        if (!lenOk(clean.Length))
            throw new CheatFormatException($"{label} code has an invalid length ({clean.Length}): '{code}'.");

        int[] d = new int[clean.Length];
        for (int i = 0; i < clean.Length; i++)
        {
            char c = char.ToUpperInvariant(clean[i]);
            int v = c is >= '0' and <= '9' ? c - '0'
                  : c is >= 'A' and <= 'F' ? c - 'A' + 10
                  : -1;
            if (v < 0) throw new CheatFormatException($"'{clean[i]}' is not a valid {label} hex digit.");
            d[i] = v;
        }
        return d;
    }

    /// <summary>Strip separators (hyphen, space) so callers may pass grouped or ungrouped codes.</summary>
    private static string Clean(string code)
    {
        if (code is null) throw new CheatFormatException("Code is null.");
        return code.Replace("-", "").Replace(" ", "").Trim();
    }

    private static string LettersToString(int[] n, string alphabet)
    {
        var chars = new char[n.Length];
        for (int i = 0; i < n.Length; i++) chars[i] = alphabet[n[i]];
        return new string(chars);
    }

    private static void RequireByte(long v, string what)
    {
        if (v is < 0 or > 0xFF)
            throw new CheatFormatException($"{what} must be a byte 0x00-0xFF, got 0x{v:X}.");
    }
}
