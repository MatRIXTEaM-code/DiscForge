// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Util;

/// <summary>
/// CRC-16/CCITT (polynomial 0x1021, init 0x0000, no reflection) — the checksum
/// the CD world runs on. The Q sub-channel frame and every CD-TEXT pack carry
/// this CRC with all bits INVERTED (ones' complement), per Red Book convention;
/// <see cref="ComputeInverted"/> gives that form directly.
///
/// The non-inverted form is the well-known CRC-16/XMODEM, which is what makes
/// this testable against published vectors ("123456789" → 0x31C3).
/// </summary>
public static class Crc16
{
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort c = (ushort)(i << 8);
            for (int b = 0; b < 8; b++)
                c = (ushort)((c & 0x8000) != 0 ? (c << 1) ^ 0x1021 : c << 1);
            t[i] = c;
        }
        return t;
    }

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
            crc = (ushort)((crc << 8) ^ Table[((crc >> 8) ^ b) & 0xFF]);
        return crc;
    }

    /// <summary>The CRC as stored on disc: computed, then all bits inverted.</summary>
    public static ushort ComputeInverted(ReadOnlySpan<byte> data)
        => (ushort)~Compute(data);
}
