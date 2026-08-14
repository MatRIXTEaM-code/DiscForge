// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Hfs;

/// <summary>One resource inside a classic Mac resource fork — its four-character type, ID, optional name,
/// length and attribute byte. The bytes themselves are located by <see cref="DataOffset"/> within the fork.</summary>
public sealed record HfsResource
{
    /// <summary>The four-character resource type (OSType), e.g. "vers", "ICN#", "CODE", "snd ".</summary>
    public required string Type { get; init; }
    /// <summary>Signed 16-bit resource ID.</summary>
    public required short Id { get; init; }
    /// <summary>The resource's name, or null when it is unnamed.</summary>
    public required string? Name { get; init; }
    /// <summary>Length of the resource's data in bytes.</summary>
    public required int Length { get; init; }
    /// <summary>Resource attribute flags (bit 6 = purgeable, bit 5 = locked, bit 4 = protected, and so on).</summary>
    public required byte Attributes { get; init; }
    /// <summary>Absolute byte offset of the resource's data within the fork (past its 4-byte length prefix).</summary>
    public required int DataOffset { get; init; }
}

/// <summary>A decoded 'vers' resource — the human-readable version stamp Mac software carries.</summary>
public sealed record HfsVersion(int Major, int Minor, string Stage, string ShortText, string LongText);

/// <summary>A parsed resource fork: the flat list of every resource it contains.</summary>
public sealed record HfsResourceForkInfo
{
    public required IReadOnlyList<HfsResource> Resources { get; init; }

    /// <summary>Distinct resource types present, in first-seen order.</summary>
    public IEnumerable<string> Types => Resources.Select(r => r.Type).Distinct();
    public int Count => Resources.Count;
}

/// <summary>
/// Reader for the classic Mac <b>resource fork</b> — the second data stream every Mac file carries and that
/// every ISO 9660 / Joliet / data-fork tool silently discards. Its internal structure (Inside Macintosh: More
/// Macintosh Toolbox) is a small header pointing at a resource-data area and a resource map; the map holds a
/// type list and, per type, a reference list of (id, name-offset, data-offset). This walks all of it and hands
/// back a flat catalogue of every resource — icons ('ICN#', 'icl8'), version stamps ('vers'), code ('CODE'),
/// dialogs ('DITL'), sounds ('snd '), Finder bundle info ('BNDL'), and the rest — with each one's type, id,
/// name and length. That surfaces the Mac half of a hybrid disc's contents that would otherwise be invisible.
/// Reads and reports only; it decodes structure and changes nothing.
/// </summary>
public static class HfsResourceFork
{
    private const int HeaderSize = 16;

    /// <summary>True if <paramref name="fork"/> looks like a resource fork with a self-consistent header.</summary>
    public static bool Looks(ReadOnlySpan<byte> fork)
    {
        if (fork.Length < HeaderSize) return false;
        long dataOff = ReadU32(fork, 0);
        long mapOff = ReadU32(fork, 4);
        long dataLen = ReadU32(fork, 8);
        long mapLen = ReadU32(fork, 12);
        if (dataOff < HeaderSize || mapOff < HeaderSize) return false;
        if (mapLen < 28) return false;
        if (dataOff + dataLen > fork.Length) return false;
        if (mapOff + mapLen > fork.Length) return false;
        return true;
    }

    /// <summary>Parse a resource fork's bytes into its catalogue of resources.</summary>
    public static HfsResourceForkInfo Parse(byte[] fork)
    {
        ArgumentNullException.ThrowIfNull(fork);
        if (fork.Length < HeaderSize)
            throw new HfsFormatException("Resource fork is too small to hold a 16-byte header.");

        int dataOff = (int)ReadU32(fork, 0);
        int mapOff = (int)ReadU32(fork, 4);
        int dataLen = (int)ReadU32(fork, 8);
        int mapLen = (int)ReadU32(fork, 12);

        if (dataOff < HeaderSize || mapOff < HeaderSize)
            throw new HfsFormatException("Resource fork header offsets point into the header itself.");
        if ((long)dataOff + dataLen > fork.Length)
            throw new HfsFormatException("Resource data area runs past the end of the fork.");
        if ((long)mapOff + mapLen > fork.Length || mapLen < 28)
            throw new HfsFormatException("Resource map runs past the end of the fork.");

        // Resource map: type-list and name-list offsets are relative to the map's start.
        int typeListOff = mapOff + ReadU16(fork, mapOff + 24);
        int nameListOff = mapOff + ReadU16(fork, mapOff + 26);
        if (typeListOff < mapOff || typeListOff + 2 > mapOff + mapLen)
            throw new HfsFormatException("Type list lies outside the resource map.");

        int typeCountRaw = ReadU16(fork, typeListOff);
        // Stored as (count - 1); 0xFFFF means an empty fork (no types).
        int typeCount = typeCountRaw == 0xFFFF ? 0 : typeCountRaw + 1;

        var resources = new List<HfsResource>();
        int mapEnd = mapOff + mapLen;

        for (int t = 0; t < typeCount; t++)
        {
            int typeEntry = typeListOff + 2 + t * 8;
            if (typeEntry + 8 > mapEnd) break;

            string type = Latin1(fork, typeEntry, 4);
            int resCount = ReadU16(fork, typeEntry + 4) + 1;         // stored as (count - 1)
            int refListOff = typeListOff + ReadU16(fork, typeEntry + 6);

            for (int r = 0; r < resCount; r++)
            {
                int refEntry = refListOff + r * 12;
                if (refEntry < mapOff || refEntry + 12 > mapEnd) break;

                short id = unchecked((short)ReadU16(fork, refEntry));
                int nameOff = ReadU16(fork, refEntry + 2);
                byte attrs = fork[refEntry + 4];
                int resDataOff = (fork[refEntry + 5] << 16) | (fork[refEntry + 6] << 8) | fork[refEntry + 7];

                string? name = null;
                if (nameOff != 0xFFFF)
                {
                    int nameAt = nameListOff + nameOff;
                    if (nameAt >= mapOff && nameAt < mapEnd)
                    {
                        int nlen = fork[nameAt];
                        if (nameAt + 1 + nlen <= fork.Length)
                            name = MacRoman(fork, nameAt + 1, nlen);
                    }
                }

                int length = 0;
                int absData = dataOff + resDataOff;
                if (resDataOff >= 0 && absData >= HeaderSize && absData + 4 <= dataOff + dataLen)
                {
                    length = (int)ReadU32(fork, absData);
                    if (length < 0 || absData + 4L + length > dataOff + dataLen)
                        length = Math.Max(0, dataOff + dataLen - (absData + 4)); // clamp a bad length field
                }

                resources.Add(new HfsResource
                {
                    Type = type,
                    Id = id,
                    Name = name,
                    Length = length,
                    Attributes = attrs,
                    DataOffset = absData + 4,
                });
            }
        }

        return new HfsResourceForkInfo { Resources = resources };
    }

    /// <summary>The raw bytes of one resource, sliced out of the fork.</summary>
    public static ReadOnlySpan<byte> GetData(byte[] fork, HfsResource resource)
    {
        ArgumentNullException.ThrowIfNull(fork);
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.DataOffset < 0 || (long)resource.DataOffset + resource.Length > fork.Length)
            return ReadOnlySpan<byte>.Empty;
        return fork.AsSpan(resource.DataOffset, resource.Length);
    }

    /// <summary>
    /// Decode a 'vers' resource — the version stamp shown in the Finder's Get Info window. Layout: major/minor
    /// BCD bytes, a development-stage byte, a pre-release byte, a 2-byte region code, then a short and a long
    /// Pascal string (e.g. "1.0.4" and "1.0.4, © 1994 …").
    /// </summary>
    public static HfsVersion? DecodeVersion(ReadOnlySpan<byte> vers)
    {
        if (vers.Length < 8) return null;
        int major = FromBcd(vers[0]);
        int minor = FromBcd(vers[1]);
        string stage = vers[2] switch
        {
            0x20 => "development",
            0x40 => "alpha",
            0x60 => "beta",
            0x80 => "release",
            _ => $"0x{vers[2]:X2}",
        };
        int p = 6;
        string shortText = ReadPascal(vers, ref p);
        string longText = ReadPascal(vers, ref p);
        return new HfsVersion(major, minor, stage, shortText, longText);
    }

    private static string ReadPascal(ReadOnlySpan<byte> b, ref int p)
    {
        if (p >= b.Length) return "";
        int len = b[p];
        int start = p + 1;
        int avail = Math.Min(len, b.Length - start);
        p = start + avail;
        return avail <= 0 ? "" : MacRoman(b, start, avail);
    }

    private static int FromBcd(byte v) => (v >> 4) * 10 + (v & 0x0F);

    private static uint ReadU32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(off, 4));
    private static int ReadU16(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt16BigEndian(b.Slice(off, 2));

    private static string Latin1(ReadOnlySpan<byte> b, int off, int len)
    {
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append((char)b[off + i]);
        return sb.ToString();
    }

    // Classic Mac text is MacRoman; ASCII bytes decode identically, high bytes get a best-effort mapping.
    private static string MacRoman(ReadOnlySpan<byte> b, int off, int len)
    {
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte c = b[off + i];
            sb.Append(c < 0x80 ? (char)c : '.');
        }
        return sb.ToString();
    }
}
