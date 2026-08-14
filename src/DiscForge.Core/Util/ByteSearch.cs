// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Util;

/// <summary>
/// Find a byte pattern in a file — the "Search" job. Takes a needle as raw bytes,
/// an ASCII string, or a hex string ("4d 5a" / "4D5A"), and returns every offset
/// where it occurs, scanning a stream in chunks so a whole disc image never has to
/// be held in memory. Overlapping matches are reported.
/// </summary>
public static class ByteSearch
{
    /// <summary>Parse a hex pattern ("4d5a" or "4d 5a" or "4D 5A"). Whitespace is
    /// ignored; an odd digit count or a non-hex character is an error.</summary>
    public static byte[] ParseHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var cleaned = new StringBuilder(hex.Length);
        foreach (char c in hex)
            if (!char.IsWhiteSpace(c)) cleaned.Append(c);
        if (cleaned.Length % 2 != 0)
            throw new FormatException("A hex pattern needs an even number of digits.");

        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(cleaned.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    public static byte[] FromAscii(string text) => Encoding.ASCII.GetBytes(text ?? "");

    /// <summary>All offsets of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    public static IReadOnlyList<long> FindAll(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        var hits = new List<long>();
        if (needle.Length == 0 || haystack.Length < needle.Length) return hits;
        int start = 0;
        while (true)
        {
            int idx = haystack[start..].IndexOf(needle);
            if (idx < 0) break;
            hits.Add(start + idx);
            start += idx + 1;
        }
        return hits;
    }

    /// <summary>Scan a stream in chunks, keeping a (needle-1)-byte overlap between
    /// chunks so matches straddling a boundary are still found. Returns absolute
    /// offsets from the stream's start.</summary>
    public static IReadOnlyList<long> FindAll(Stream stream, byte[] needle, int chunkSize = 1 << 20)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(needle);
        var hits = new List<long>();
        if (needle.Length == 0) return hits;
        if (chunkSize < needle.Length * 2) chunkSize = Math.Max(needle.Length * 2, 4096);

        int overlap = needle.Length - 1;
        var buf = new byte[chunkSize];
        long basePos = 0;
        int carried = 0;

        while (true)
        {
            int want = buf.Length - carried;
            int read = ReadUpTo(stream, buf, carried, want);
            int available = carried + read;
            if (available < needle.Length) break;

            foreach (var off in FindAll(buf.AsSpan(0, available), needle))
                hits.Add(basePos + off);

            if (read == 0) break;

            // Carry the last `overlap` bytes forward so a boundary-straddling match
            // is caught, and advance basePos past the bytes we won't re-scan.
            int consumed = available - overlap;
            Array.Copy(buf, consumed, buf, 0, overlap);
            carried = overlap;
            basePos += consumed;
        }

        // De-duplicate (a match can be seen twice through the carried overlap).
        hits.Sort();
        var unique = new List<long>(hits.Count);
        long last = -1;
        foreach (var h in hits) { if (h != last) unique.Add(h); last = h; }
        return unique;
    }

    private static int ReadUpTo(Stream s, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (count > 0)
        {
            int n = s.Read(buffer, offset, count);
            if (n <= 0) break;
            offset += n; count -= n; total += n;
        }
        return total;
    }
}
