// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Cheat;

/// <summary>
/// Parser for raw (unencrypted) GameShark / Action Replay codes. A code is a
/// <c>AAAAAAAA VVVV</c> pair: an 8-hex-digit address word whose top byte is a
/// type/opcode selecting the operation (8-bit write, 16-bit write, conditional,
/// …) and a 16-bit value word.
///
/// This reads the raw form only. Later GameShark revisions (V3+ on PS1) ship the
/// address/value words XOR/rotate-encrypted; those must be un-encrypted before
/// they reach this parser — decryption is out of scope here (and not needed to
/// describe the format).
/// </summary>
public static class GameShark
{
    /// <summary>
    /// Parse one raw GameShark line for the given platform (currently the PS1 form).
    /// Returns a <see cref="CheatCode"/> whose <see cref="CheatCode.Address"/> is the
    /// 24-bit target address, <see cref="CheatCode.Value"/> the value word, and
    /// <see cref="CheatCode.Description"/> the decoded operation. The type byte is
    /// stashed in the description; the raw 32-bit address word is preserved in
    /// <see cref="CheatCode.Compare"/> when it is a conditional's tested value.
    /// </summary>
    public static CheatCode Parse(string line, CheatPlatform platform)
    {
        if (platform != CheatPlatform.GameSharkPs1)
            throw new CheatFormatException($"GameShark parsing is only implemented for {CheatPlatform.GameSharkPs1}.");

        if (line is null) throw new CheatFormatException("Code line is null.");

        // Split into the address word and value word (whitespace, comma or colon separated).
        string[] parts = line.Trim()
            .Split(new[] { ' ', '\t', ',', ':' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new CheatFormatException(
                $"Expected 'AAAAAAAA VVVV' (two hex words), got {parts.Length} token(s): '{line}'.");

        long addrWord = ParseHex(parts[0], 8, "address word");
        long valueWord = ParseHex(parts[1], 4, "value word");

        int type = (int)((addrWord >> 24) & 0xFF);
        long address = addrWord & 0x00FFFFFF;

        (string description, long? compare) = Describe(type, address, valueWord);

        return new CheatCode
        {
            Platform = CheatPlatform.GameSharkPs1,
            Address = address,
            Value = valueWord,
            Compare = compare,
            Description = description,
        };
    }

    /// <summary>The raw type/opcode byte of a GameShark address word (top 8 bits).</summary>
    public static int TypeCode(long addressWord) => (int)((addressWord >> 24) & 0xFF);

    private static (string Description, long? Compare) Describe(int type, long address, long value) => type switch
    {
        0x30 => ($"8-bit constant write: [0x{address:X6}] = 0x{value & 0xFF:X2}", null),
        0x80 => ($"16-bit constant write: [0x{address:X6}] = 0x{value & 0xFFFF:X4}", null),
        0x50 => ($"repeat/slide block: {(address >> 16) & 0xFF} entries, address step "
                 + $"0x{address & 0xFFFF:X4}, value step 0x{value & 0xFFFF:X4}", null),
        0xD0 => ($"conditional: execute next line if 16-bit [0x{address:X6}] == 0x{value & 0xFFFF:X4}", value),
        0xD1 => ($"conditional: execute next line if 16-bit [0x{address:X6}] != 0x{value & 0xFFFF:X4}", value),
        0xD2 => ($"conditional: execute next line if 16-bit [0x{address:X6}] < 0x{value & 0xFFFF:X4}", value),
        0xD3 => ($"conditional: execute next line if 16-bit [0x{address:X6}] > 0x{value & 0xFFFF:X4}", value),
        0xE0 => ($"conditional: execute next line if 8-bit [0x{address:X6}] == 0x{value & 0xFF:X2}", value),
        0xE1 => ($"conditional: execute next line if 8-bit [0x{address:X6}] != 0x{value & 0xFF:X2}", value),
        0xE2 => ($"conditional: execute next line if 8-bit [0x{address:X6}] < 0x{value & 0xFF:X2}", value),
        0xE3 => ($"conditional: execute next line if 8-bit [0x{address:X6}] > 0x{value & 0xFF:X2}", value),
        0x10 => ($"16-bit increment: [0x{address:X6}] += 0x{value & 0xFFFF:X4}", null),
        0x11 => ($"16-bit decrement: [0x{address:X6}] -= 0x{value & 0xFFFF:X4}", null),
        0x20 => ($"8-bit increment: [0x{address:X6}] += 0x{value & 0xFF:X2}", null),
        0x21 => ($"8-bit decrement: [0x{address:X6}] -= 0x{value & 0xFF:X2}", null),
        0xC0 or 0xC1 => ($"enable/joker code (type 0x{type:X2}): [0x{address:X6}] vs 0x{value & 0xFFFF:X4}", value),
        0xF0 => ($"master/enable code (type 0xF0): 0x{address:X6} 0x{value & 0xFFFF:X4}", null),
        _ => ($"unknown type 0x{type:X2}: [0x{address:X6}] value 0x{value & 0xFFFF:X4}", null),
    };

    private static long ParseHex(string token, int expectedDigits, string what)
    {
        if (token.Length != expectedDigits)
            throw new CheatFormatException(
                $"GameShark {what} must be {expectedDigits} hex digits, got '{token}' ({token.Length}).");

        long value = 0;
        foreach (char ch in token)
        {
            char c = char.ToUpperInvariant(ch);
            int v = c is >= '0' and <= '9' ? c - '0'
                  : c is >= 'A' and <= 'F' ? c - 'A' + 10
                  : -1;
            if (v < 0) throw new CheatFormatException($"'{ch}' is not a hex digit in {what} '{token}'.");
            value = (value << 4) | (uint)v;
        }
        return value;
    }
}
