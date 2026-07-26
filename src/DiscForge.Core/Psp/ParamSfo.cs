// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Psp;

/// <summary>Raised when a blob is not a well-formed PARAM.SFO.</summary>
public sealed class ParamSfoFormatException(string message) : Exception(message);

/// <summary>
/// One value out of a PARAM.SFO. A value is either a string (data formats
/// 0x0004 / 0x0204) or an unsigned 32-bit integer (format 0x0404). Which one is
/// live is told by <see cref="IsInt"/>.
/// </summary>
public sealed record SfoValue
{
    /// <summary>The text of a string value, or null for an integer value.</summary>
    public string? Text { get; init; }
    /// <summary>The number of an integer value, or null for a string value.</summary>
    public uint? Number { get; init; }
    /// <summary>True when this value is an integer (format 0x0404).</summary>
    public bool IsInt { get; init; }

    public static SfoValue OfString(string text) => new() { Text = text, IsInt = false };
    public static SfoValue OfInt(uint value) => new() { Number = value, IsInt = true };

    public override string ToString() => IsInt ? (Number ?? 0).ToString() : (Text ?? "");
}

/// <summary>
/// Parses PARAM.SFO — the little-endian key/value metadata table Sony's platforms
/// (PSP, PS3, PS Vita, PS4) drop next to a title to describe it: its disc id,
/// human title, category, disc version, parental level and so on. Reading it is
/// purely descriptive; nothing here is protection- or decryption-related.
///
/// Clean-room, from the public PARAM.SFO description:
///
///   Header (20 bytes, little-endian):
///     0x00  4  magic            00 'P' 'S' 'F'  (00 50 53 46)
///     0x04  4  version          e.g. 0x00000101
///     0x08  4  key_table_start  offset of the key table, from the file start
///     0x0C  4  data_table_start offset of the data table, from the file start
///     0x10  4  entry_count      number of index entries
///
///   Index table (entry_count × 16 bytes, immediately after the header):
///     0x00  2  key_offset       key's offset, relative to key_table_start
///     0x02  2  data_format      0x0004 UTF-8 (special, not NUL-terminated)
///                               0x0204 UTF-8 (NUL-terminated string)
///                               0x0404 uint32 (little-endian)
///     0x04  4  data_len         used length of the value
///     0x08  4  data_max_len     reserved length of the value
///     0x0C  4  data_offset      value's offset, relative to data_table_start
///
///   Key table:  NUL-terminated ASCII keys, packed back to back.
///   Data table: each value at its data_offset — a string (trailing NULs trimmed)
///               for 0x0004 / 0x0204, or a little-endian uint32 for 0x0404.
/// </summary>
public sealed class ParamSfo
{
    /// <summary>The magic that opens every PARAM.SFO: 00 'P' 'S' 'F'.</summary>
    private static readonly byte[] Magic = { 0x00, (byte)'P', (byte)'S', (byte)'F' };

    private const ushort FormatUtf8Special = 0x0004;
    private const ushort FormatUtf8String = 0x0204;
    private const ushort FormatUint32 = 0x0404;

    private readonly Dictionary<string, SfoValue> _entries;

    private ParamSfo(Dictionary<string, SfoValue> entries) => _entries = entries;

    /// <summary>Every key/value pair, in the order the index table listed them.</summary>
    public IReadOnlyDictionary<string, SfoValue> Entries => _entries;

    /// <summary>The version word from the header (e.g. 0x00000101 for 1.01).</summary>
    public uint Version { get; private init; }

    /// <summary>The string value for <paramref name="key"/>, or "" when the key is
    /// absent or holds an integer.</summary>
    public string GetString(string key)
        => _entries.TryGetValue(key, out var v) && !v.IsInt ? v.Text ?? "" : "";

    /// <summary>The integer value for <paramref name="key"/>, or null when the key
    /// is absent or holds a string.</summary>
    public uint? GetInt(string key)
        => _entries.TryGetValue(key, out var v) && v.IsInt ? v.Number : null;

    /// <summary>True when <paramref name="key"/> is present (of either type).</summary>
    public bool Contains(string key) => _entries.ContainsKey(key);

    // ---- parsing ------------------------------------------------------------

    /// <summary>Parse a PARAM.SFO from a byte buffer.</summary>
    public static ParamSfo Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < 20)
            throw new ParamSfoFormatException(
                $"Too short to be a PARAM.SFO — {data.Length} bytes, need at least the 20-byte header.");

        if (!data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new ParamSfoFormatException(
                "Bad PARAM.SFO magic — the first four bytes are not 00 'P' 'S' 'F'.");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x04, 4));
        uint keyTableStart = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x08, 4));
        uint dataTableStart = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x0C, 4));
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x10, 4));

        // The index table sits between the header and the key table.
        long indexEnd = 20L + (long)entryCount * 16;
        if (entryCount > int.MaxValue / 16 || indexEnd > data.Length)
            throw new ParamSfoFormatException(
                $"PARAM.SFO claims {entryCount} entries — its {indexEnd}-byte index table runs past " +
                $"the end of the {data.Length}-byte file.");

        if (keyTableStart > data.Length || dataTableStart > data.Length)
            throw new ParamSfoFormatException(
                $"PARAM.SFO key/data table offsets (key={keyTableStart}, data={dataTableStart}) lie " +
                $"past the end of the {data.Length}-byte file.");

        var entries = new Dictionary<string, SfoValue>(StringComparer.Ordinal);

        for (int i = 0; i < entryCount; i++)
        {
            var idx = data.AsSpan(20 + i * 16, 16);
            ushort keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(idx.Slice(0, 2));
            ushort dataFormat = BinaryPrimitives.ReadUInt16LittleEndian(idx.Slice(2, 2));
            uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(idx.Slice(4, 4));
            // data_max_len at 0x08 is not needed for reading.
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(idx.Slice(12, 4));

            string key = ReadKey(data, keyTableStart, keyOffset, i);

            long valueStart = (long)dataTableStart + dataOffset;
            if (valueStart > data.Length || valueStart + dataLen > data.Length)
                throw new ParamSfoFormatException(
                    $"PARAM.SFO entry '{key}' points at bytes {valueStart}..{valueStart + dataLen} " +
                    $"which run past the end of the {data.Length}-byte file.");

            SfoValue value = dataFormat switch
            {
                FormatUint32 => ReadUint32(data, key, (int)valueStart, dataLen),
                FormatUtf8String or FormatUtf8Special => ReadString(data, (int)valueStart, (int)dataLen),
                _ => throw new ParamSfoFormatException(
                    $"PARAM.SFO entry '{key}' has an unknown data format 0x{dataFormat:X4}."),
            };

            entries[key] = value;
        }

        return new ParamSfo(entries) { Version = version };
    }

    /// <summary>Parse a PARAM.SFO from a stream (read to the end).</summary>
    public static ParamSfo Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Parse(ms.ToArray());
    }

    private static string ReadKey(byte[] data, uint keyTableStart, ushort keyOffset, int entryIndex)
    {
        long start = (long)keyTableStart + keyOffset;
        if (start < 0 || start >= data.Length)
            throw new ParamSfoFormatException(
                $"PARAM.SFO entry #{entryIndex} names a key at offset {start}, past the end of the file.");

        int end = (int)start;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, (int)start, end - (int)start);
    }

    private static SfoValue ReadString(byte[] data, int start, int length)
    {
        // Format 0x0204 is NUL-terminated; 0x0004 is not. Either way, trailing
        // NULs within the used length are not part of the text.
        var span = data.AsSpan(start, length);
        int actual = span.IndexOf((byte)0);
        if (actual < 0) actual = length;
        return SfoValue.OfString(Encoding.UTF8.GetString(span.Slice(0, actual)));
    }

    private static SfoValue ReadUint32(byte[] data, string key, int start, uint dataLen)
    {
        if (dataLen < 4 || start + 4 > data.Length)
            throw new ParamSfoFormatException(
                $"PARAM.SFO entry '{key}' is a uint32 but only {dataLen} byte(s) of value are available.");
        return SfoValue.OfInt(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start, 4)));
    }
}
