// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Saves;

public sealed class N64SaveFormatException(string message) : Exception(message);

/// <summary>The kind of Nintendo 64 save a raw file's size implies.</summary>
public enum N64SaveType
{
    Unknown = 0,
    /// <summary>EEPROM 4 kbit — 512 bytes.</summary>
    Eeprom4k,
    /// <summary>EEPROM 16 kbit — 2 KB.</summary>
    Eeprom16k,
    /// <summary>SRAM — 32 KB (indistinguishable by size alone from a Controller Pak).</summary>
    Sram,
    /// <summary>FlashRAM — 128 KB.</summary>
    FlashRam,
    /// <summary>Controller Pak (Mempak) — 32 KB with a filesystem header.</summary>
    ControllerPak,
}

/// <summary>One note (saved game) on an N64 Controller Pak.</summary>
public sealed record N64Note
{
    /// <summary>Four-character game code (ASCII), e.g. "NSME".</summary>
    public required string GameCode { get; init; }
    /// <summary>Two-character publisher code (ASCII).</summary>
    public required string Publisher { get; init; }
    /// <summary>Note name, decoded from the N64 controller-pak character set.</summary>
    public required string Name { get; init; }
    /// <summary>First data page of the note (5-based).</summary>
    public required int StartPage { get; init; }
}

/// <summary>The contents of an N64 Controller Pak (Mempak) filesystem.</summary>
public sealed record N64ControllerPak
{
    public required IReadOnlyList<N64Note> Notes { get; init; }

    private const int PageSize = 256;
    private const int NoteTableOffset = 0x300;   // page 3
    private const int NoteEntrySize = 0x20;
    private const int NoteCount = 16;
    private const int FirstDataPage = 5;
    private const int TotalPages = 128;
    private static readonly int[] IdBlockOffsets = { 0x20, 0x60, 0x80, 0xC0 };

    public static bool IsControllerPak(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length != N64SaveReader.MempakSize) return false;
        // Sane if any redundant ID-block copy has a matching checksum.
        foreach (int off in IdBlockOffsets)
            if (IdBlockValid(data, off)) return true;
        return false;
    }

    public static N64ControllerPak Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length != N64SaveReader.MempakSize)
            throw new N64SaveFormatException($"A Controller Pak is {N64SaveReader.MempakSize:N0} bytes; got {data.Length:N0}.");
        if (!IsControllerPak(data))
            throw new N64SaveFormatException("No valid Controller Pak ID block — not a Mempak image.");

        var notes = new List<N64Note>();
        for (int i = 0; i < NoteCount; i++)
        {
            int at = NoteTableOffset + i * NoteEntrySize;
            int startPage = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 0x06));
            if (startPage is < FirstDataPage or >= TotalPages) continue;   // empty / invalid slot

            notes.Add(new N64Note
            {
                GameCode = Ascii(data, at + 0x00, 4),
                Publisher = Ascii(data, at + 0x04, 2),
                Name = DecodeName(data, at + 0x10, 16),
                StartPage = startPage,
            });
        }
        return new N64ControllerPak { Notes = notes };
    }

    // The ID-block checksum: big-endian u16 sum of the first 14 words, stored at word 14
    // (0x1C); word 15 (0x1E) holds (0xFFF2 - sum). A copy is valid when both match.
    private static bool IdBlockValid(byte[] data, int off)
    {
        if (off + N64Blk.IdBlockSize > data.Length) return false;
        uint sum = 0;
        for (int i = 0; i < 14; i++) sum += BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(off + i * 2));
        ushort a = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(off + 0x1C));
        ushort b = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(off + 0x1E));
        return a == (ushort)(sum & 0xFFFF) && b == (ushort)((0xFFF2 - sum) & 0xFFFF);
    }

    private static string Ascii(byte[] d, int at, int len)
    {
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte b = d[at + i];
            if (b == 0) break;
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : ' ');
        }
        return sb.ToString().TrimEnd();
    }

    // Decode a note name from the N64 controller-pak character set. Documented mapping
    // of the printable subset:
    //   0x00        = terminator
    //   0x0F        = space
    //   0x10..0x19  = '0'..'9'
    //   0x1A..0x33  = 'A'..'Z'
    //   0x34..0x4D  = 'a'..'z'
    // Any other code is rendered '?' (best-effort). Trailing spaces are trimmed.
    private static string DecodeName(byte[] d, int at, int len)
    {
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte c = d[at + i];
            if (c == 0x00) break;
            sb.Append(N64Blk.FromFont(c));
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Reads Nintendo 64 saves. Raw cartridge saves (EEPROM, SRAM, FlashRAM) are plain
/// blobs identified only by size; the Controller Pak (Mempak) is a small filesystem
/// whose notes (saved games) are enumerated. Everything is BIG-ENDIAN.
///
/// Clean-room, from the public N64 Controller Pak description:
///   32 KB = 128 pages of 256 bytes. Page 0 is the label/header, holding several
///   redundant copies of a checksummed 32-byte ID block (at 0x20, 0x60, 0x80, 0xC0).
///   Pages 1–2 are the INODE (page-usage) table and its backup; pages 3–4 are the
///   note table: 16 entries of 0x20 bytes at offset 0x300. A note entry: game code
///   (4 @0x00), publisher (2 @0x04), start page (u16 @0x06), status (@0x0B),
///   extension (4 @0x0C), and a 16-byte name (@0x10) in the N64 controller-pak font.
///   The ID-block checksum is the big-endian u16 sum of the first 14 words, stored at
///   0x1C, with (0xFFF2 - sum) stored at 0x1E.
/// </summary>
public static class N64SaveReader
{
    public const int Eeprom4kSize = 512;
    public const int Eeprom16kSize = 2048;
    public const int SramSize = 32 * 1024;
    public const int MempakSize = 32 * 1024;
    public const int FlashRamSize = 128 * 1024;

    /// <summary>
    /// Identify an N64 save purely by file size. Note the ambiguities: a 32 KB file is
    /// reported as <see cref="N64SaveType.Sram"/> but may equally be a Controller Pak —
    /// use <see cref="N64ControllerPak.IsControllerPak"/> to tell them apart by header.
    /// </summary>
    public static N64SaveType IdentifyBySize(long length) => length switch
    {
        Eeprom4kSize => N64SaveType.Eeprom4k,
        Eeprom16kSize => N64SaveType.Eeprom16k,
        SramSize => N64SaveType.Sram,          // or ControllerPak — size cannot decide
        FlashRamSize => N64SaveType.FlashRam,
        _ => N64SaveType.Unknown,
    };

    public static string Describe(N64SaveType type) => type switch
    {
        N64SaveType.Eeprom4k => "EEPROM 4 kbit (512 B)",
        N64SaveType.Eeprom16k => "EEPROM 16 kbit (2 KB)",
        N64SaveType.Sram => "SRAM (32 KB) — or a Controller Pak",
        N64SaveType.FlashRam => "FlashRAM (128 KB)",
        N64SaveType.ControllerPak => "Controller Pak / Mempak (32 KB)",
        _ => "unknown size",
    };
}

/// <summary>Small shared constants / N64 font helpers.</summary>
internal static class N64Blk
{
    public const int IdBlockSize = 0x20;

    public static char FromFont(byte c) => c switch
    {
        0x0F => ' ',
        >= 0x10 and <= 0x19 => (char)('0' + (c - 0x10)),
        >= 0x1A and <= 0x33 => (char)('A' + (c - 0x1A)),
        >= 0x34 and <= 0x4D => (char)('a' + (c - 0x34)),
        _ => '?',
    };

    public static byte ToFont(char ch) => ch switch
    {
        ' ' => 0x0F,
        >= '0' and <= '9' => (byte)(0x10 + (ch - '0')),
        >= 'A' and <= 'Z' => (byte)(0x1A + (ch - 'A')),
        >= 'a' and <= 'z' => (byte)(0x34 + (ch - 'a')),
        _ => 0x00,
    };
}
