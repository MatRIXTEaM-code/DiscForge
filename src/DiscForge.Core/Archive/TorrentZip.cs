// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DiscForge.Core.Util;

namespace DiscForge.Core.Archive;

/// <summary>One file to place in a TorrentZip archive.</summary>
public sealed record ZipEntry(string Name, byte[] Data);

/// <summary>
/// A deterministic ZIP writer following the TorrentZip rules ROM managers
/// (clrmamepro, RomVault) rely on: entries sorted case-insensitively, a fixed
/// timestamp (1996-12-24 23:32:00), no extra fields, DEFLATE compression, and the
/// end-of-archive comment <c>TORRENTZIPPED-XXXXXXXX</c> whose hex is the CRC-32 of the
/// central directory. The same input always yields byte-identical output on a given
/// build, so a set has a stable hash.
///
/// Caveat (documented, honest): byte-for-byte identity with a *different* tool's
/// TorrentZip also requires the identical DEFLATE encoder (classic zlib level 9). The
/// structure, ordering, timestamps and the TORRENTZIPPED comment here are exact and
/// verifiable; the compressed byte stream depends on this build's DEFLATE, which the
/// offline build cannot pin to classic zlib. Use <see cref="IsTorrentZipStructured"/>
/// to check an archive's TorrentZip structure independently of the encoder.
/// </summary>
public static class TorrentZip
{
    // TorrentZip's canonical timestamp: 1996-12-24 23:32:00, as DOS date/time.
    private const ushort DosDate = (16 << 9) | (12 << 5) | 24;      // 1996-12-24
    private const ushort DosTime = (23 << 11) | (32 << 5) | 0;       // 23:32:00

    public static byte[] Create(IEnumerable<ZipEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // TorrentZip orders entries by their lower-cased name, byte-ordinal.
        var ordered = entries
            .Select(e => e with { Name = e.Name.Replace('\\', '/').TrimStart('/') })
            .OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();

        using var ms = new MemoryStream();
        var localOffsets = new List<long>(ordered.Count);
        var compressed = new List<byte[]>(ordered.Count);
        var crcs = new List<uint>(ordered.Count);

        foreach (var e in ordered)
        {
            byte[] comp = Deflate(e.Data);
            uint crc = Crc32.Compute(e.Data);
            compressed.Add(comp);
            crcs.Add(crc);

            localOffsets.Add(ms.Position);
            var name = Encoding.UTF8.GetBytes(e.Name);
            WriteLocalHeader(ms, name, crc, comp.Length, e.Data.Length);
            ms.Write(comp);
        }

        long cdStart = ms.Position;
        using (var cd = new MemoryStream())
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                var name = Encoding.UTF8.GetBytes(ordered[i].Name);
                WriteCentralHeader(cd, name, crcs[i], compressed[i].Length, ordered[i].Data.Length, localOffsets[i]);
            }
            byte[] cdBytes = cd.ToArray();
            ms.Write(cdBytes);

            uint cdCrc = Crc32.Compute(cdBytes);
            string comment = $"TORRENTZIPPED-{cdCrc:X8}";
            WriteEndRecord(ms, ordered.Count, cdBytes.Length, cdStart, comment);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Verifies an archive's TorrentZip *structure*: the end comment is
    /// <c>TORRENTZIPPED-XXXXXXXX</c> and that hex equals the CRC-32 of the central
    /// directory. This is exact and encoder-independent. Returns false for a normal zip.
    /// </summary>
    public static bool IsTorrentZipStructured(byte[] zip)
    {
        ArgumentNullException.ThrowIfNull(zip);
        try
        {
            // Locate EOCD (0x06054b50), scanning back over the (short) comment.
            int eocd = -1;
            for (int i = zip.Length - 22; i >= 0 && i >= zip.Length - 22 - 0xFFFF; i--)
                if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
            if (eocd < 0) return false;

            uint cdSize = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 12));
            uint cdOff = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 16));
            ushort commentLen = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(eocd + 20));
            if (commentLen == 0 || cdOff + cdSize > zip.Length) return false;

            string comment = Encoding.ASCII.GetString(zip, eocd + 22, commentLen);
            if (!comment.StartsWith("TORRENTZIPPED-", StringComparison.Ordinal) || comment.Length != 22) return false;

            uint declared = uint.Parse(comment.AsSpan("TORRENTZIPPED-".Length), System.Globalization.NumberStyles.HexNumber);
            uint actual = Crc32.Compute(zip.AsSpan((int)cdOff, (int)cdSize));
            return declared == actual;
        }
        catch { return false; }
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            ds.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static void WriteLocalHeader(Stream s, byte[] name, uint crc, int compSize, int rawSize)
    {
        Span<byte> h = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 20);      // version needed
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 0);       // flags
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], 8);       // deflate
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[14..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h[18..], (uint)compSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[22..], (uint)rawSize);
        BinaryPrimitives.WriteUInt16LittleEndian(h[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], 0);      // extra len
        s.Write(h);
        s.Write(name);
    }

    private static void WriteCentralHeader(Stream s, byte[] name, uint crc, int compSize, int rawSize, long localOffset)
    {
        Span<byte> h = stackalloc byte[46];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 0);       // version made by
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 20);      // version needed
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], 0);       // flags
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], 8);      // deflate
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)compSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)rawSize);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[30..], 0);      // extra len
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], 0);      // comment len
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 0);      // disk start
        BinaryPrimitives.WriteUInt16LittleEndian(h[36..], 0);      // internal attrs
        BinaryPrimitives.WriteUInt32LittleEndian(h[38..], 0);      // external attrs
        BinaryPrimitives.WriteUInt32LittleEndian(h[42..], (uint)localOffset);
        s.Write(h);
        s.Write(name);
    }

    private static void WriteEndRecord(Stream s, int count, int cdSize, long cdOffset, string comment)
    {
        var commentBytes = Encoding.ASCII.GetBytes(comment);
        Span<byte> h = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 0);       // disk num
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 0);       // disk with cd
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)cdSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)cdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], (ushort)commentBytes.Length);
        s.Write(h);
        s.Write(commentBytes);
    }
}
