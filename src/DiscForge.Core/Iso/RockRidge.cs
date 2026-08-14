// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Iso;

/// <summary>
/// The POSIX metadata a Rock Ridge (SUSP / RRIP) directory record carries — the layer plain ISO 9660
/// throws away when it flattens names to 8.3 and drops ownership, permissions, symlinks and real timestamps.
/// Any field is null when its SUSP entry was absent.
/// </summary>
public sealed record RockRidgeInfo
{
    /// <summary>The real POSIX name (NM entry), reassembled across CONTINUE fragments.</summary>
    public string? Name { get; init; }
    /// <summary>st_mode from a PX entry (file-type bits + permission bits).</summary>
    public uint? Mode { get; init; }
    /// <summary>Hard-link count (PX).</summary>
    public uint? Links { get; init; }
    public uint? Uid { get; init; }
    public uint? Gid { get; init; }
    /// <summary>Inode number (PX, RRIP 1.12 only).</summary>
    public uint? Inode { get; init; }
    /// <summary>Symlink target path (SL entry), reassembled from its component list.</summary>
    public string? SymlinkTarget { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public DateTimeOffset? Accessed { get; init; }
    /// <summary>True if this record is a relocated-directory placeholder (RE) — the real directory is elsewhere.</summary>
    public bool Relocated { get; init; }
    /// <summary>Child directory location (CL) when a deep directory was relocated to fit the ISO depth limit.</summary>
    public uint? ChildLocation { get; init; }
    /// <summary>True if any recognised SUSP/RRIP entry was present.</summary>
    public bool Present { get; init; }

    public bool IsSymlink => Mode is { } m && (m & 0xF000) == 0xA000;

    /// <summary>The classic ten-character mode string, e.g. "-rwxr-xr-x" or "drwxr-xr-x". Empty when no PX.</summary>
    public string ModeString => Mode is { } m ? RockRidge.FormatMode(m) : "";
}

/// <summary>
/// Reader for the ISO 9660 <b>System Use Sharing Protocol</b> (SUSP, IEEE P1281) and the <b>Rock Ridge</b>
/// interchange protocol (RRIP, IEEE P1282) layered on top of it. Rock Ridge is how a Unix/Linux CD keeps its
/// real filesystem — long case-sensitive names, POSIX permissions and ownership, symbolic links, device nodes
/// and true timestamps — hidden in the "System Use" bytes after each directory record's identifier, invisible
/// to a plain ISO 9660 reader that sees only truncated 8.3 names. This walks that area (following CE
/// continuation blocks into other sectors) and decodes the RRIP entries: NM (name), PX (mode/links/uid/gid),
/// SL (symlink), TF (timestamps), and the CL/PL/RE deep-directory relocation markers. Reads and reports only.
/// </summary>
public static class RockRidge
{
    private const int MaxContinuations = 32;   // CE-chain loop guard
    private const int MaxSuspEntries = 4096;    // per-record entry guard

    /// <summary>
    /// Parse the System Use area of one directory record. <paramref name="readAbsolute"/> fetches bytes from
    /// the image at an absolute byte offset so CE continuation areas can be followed; pass null to decode only
    /// the entries present in <paramref name="systemUse"/> itself (no continuation).
    /// </summary>
    public static RockRidgeInfo Parse(byte[] systemUse, Func<long, int, byte[]>? readAbsolute = null)
    {
        ArgumentNullException.ThrowIfNull(systemUse);

        string? name = null;
        string? symlink = null;
        uint? mode = null, links = null, uid = null, gid = null, inode = null, childLoc = null;
        DateTimeOffset? created = null, modified = null, accessed = null;
        bool relocated = false, present = false;
        bool nameDone = false, symlinkDone = false;

        var area = systemUse;
        int hops = 0;
        int totalEntries = 0;

        while (area != null)
        {
            byte[]? nextArea = null;
            int p = 0;
            while (p + 4 <= area.Length)
            {
                if (++totalEntries > MaxSuspEntries) { area = null; break; }

                byte s1 = area[p], s2 = area[p + 1];
                int len = area[p + 2];
                if (len < 4 || p + len > area.Length) break;
                var body = area.AsSpan(p, len);

                // "ST" terminates the current System Use area.
                if (s1 == 'S' && s2 == 'T') { p = area.Length; break; }

                if (s1 == 'N' && s2 == 'M' && len >= 5)
                {
                    present = true;
                    byte flags = body[4];
                    if (!nameDone)
                    {
                        // bit0 CONTINUE, bit1 CURRENT ("."), bit2 PARENT ("..").
                        if ((flags & 0x06) == 0)
                            name = (name ?? "") + Utf8(body.Slice(5, len - 5));
                        if ((flags & 0x01) == 0) nameDone = true;
                    }
                }
                else if (s1 == 'P' && s2 == 'X' && len >= 36)
                {
                    present = true;
                    mode = BothU32(body, 4);
                    links = BothU32(body, 12);
                    uid = BothU32(body, 20);
                    gid = BothU32(body, 28);
                    if (len >= 44) inode = BothU32(body, 36);
                }
                else if (s1 == 'S' && s2 == 'L' && len >= 5)
                {
                    present = true;
                    if (!symlinkDone)
                    {
                        byte recFlags = body[4];
                        symlink = AppendSymlink(symlink ?? "", body.Slice(5, len - 5));
                        if ((recFlags & 0x01) == 0) symlinkDone = true;   // bit0 CONTINUE (more SL records follow)
                    }
                }
                else if (s1 == 'T' && s2 == 'F' && len >= 5)
                {
                    present = true;
                    DecodeTf(body, ref created, ref modified, ref accessed);
                }
                else if (s1 == 'C' && s2 == 'L' && len >= 12)
                {
                    present = true;
                    childLoc = BothU32(body, 4);
                }
                else if (s1 == 'R' && s2 == 'E')
                {
                    present = true;
                    relocated = true;
                }
                else if ((s1 == 'S' && s2 == 'P') || (s1 == 'E' && s2 == 'R') ||
                         (s1 == 'P' && s2 == 'D') || (s1 == 'R' && s2 == 'R') ||
                         (s1 == 'P' && s2 == 'L') || (s1 == 'P' && s2 == 'N'))
                {
                    present = true;   // structural / not surfaced here
                }
                else if (s1 == 'C' && s2 == 'E' && len >= 28 && readAbsolute != null && hops < MaxContinuations)
                {
                    // Continuation area: LOCATION (block), OFFSET, LENGTH — all "both-endian" u32.
                    uint block = BothU32(body, 4);
                    uint offset = BothU32(body, 12);
                    uint length = BothU32(body, 20);
                    if (length > 0 && length <= 1 << 20)
                    {
                        try
                        {
                            var fetched = readAbsolute((long)block * IsoReader.SectorSize + offset, (int)length);
                            if (fetched.Length > 0) { nextArea = fetched; hops++; }
                        }
                        catch (IsoFormatException) { /* truncated image: stop following */ }
                    }
                }

                p += len;
            }

            area = nextArea;
        }

        return new RockRidgeInfo
        {
            Name = name,
            Mode = mode, Links = links, Uid = uid, Gid = gid, Inode = inode,
            SymlinkTarget = string.IsNullOrEmpty(symlink) ? null : symlink,
            Created = created, Modified = modified, Accessed = accessed,
            Relocated = relocated, ChildLocation = childLoc,
            Present = present,
        };
    }

    /// <summary>Format a POSIX st_mode as the ten-character "drwxr-xr-x" string.</summary>
    public static string FormatMode(uint mode)
    {
        char type = (mode & 0xF000) switch
        {
            0x4000 => 'd', 0xA000 => 'l', 0x2000 => 'c', 0x6000 => 'b',
            0x1000 => 'p', 0xC000 => 's', 0x8000 => '-', _ => '?',
        };
        Span<char> s = stackalloc char[10];
        s[0] = type;
        const string rwx = "rwxrwxrwx";
        for (int i = 0; i < 9; i++)
            s[i + 1] = (mode & (1u << (8 - i))) != 0 ? rwx[i] : '-';
        if ((mode & 0x800) != 0) s[3] = (mode & 0x40) != 0 ? 's' : 'S';   // setuid
        if ((mode & 0x400) != 0) s[6] = (mode & 0x08) != 0 ? 's' : 'S';   // setgid
        if ((mode & 0x200) != 0) s[9] = (mode & 0x01) != 0 ? 't' : 'T';   // sticky
        return new string(s);
    }

    private static void DecodeTf(ReadOnlySpan<byte> body, ref DateTimeOffset? created,
                                 ref DateTimeOffset? modified, ref DateTimeOffset? accessed)
    {
        byte flags = body[4];
        bool longForm = (flags & 0x80) != 0;
        int stampLen = longForm ? 17 : 7;
        int at = 5;
        // Flag bits, in order: 0 CREATION, 1 MODIFY, 2 ACCESS, 3 ATTRIBUTES, 4 BACKUP, 5 EXPIRATION, 6 EFFECTIVE.
        for (int bit = 0; bit < 7; bit++)
        {
            if ((flags & (1 << bit)) == 0) continue;
            if (at + stampLen > body.Length) break;
            var stamp = longForm ? ParseLongTime(body.Slice(at, 17)) : ParseShortTime(body.Slice(at, 7));
            switch (bit)
            {
                case 0: created = stamp; break;
                case 1: modified = stamp; break;
                case 2: accessed = stamp; break;
            }
            at += stampLen;
        }
    }

    // SUSP 7-byte timestamp: years-since-1900, month, day, hour, minute, second, GMT offset (signed 15-min units).
    private static DateTimeOffset? ParseShortTime(ReadOnlySpan<byte> b)
    {
        int year = 1900 + b[0];
        int month = b[1], day = b[2], hour = b[3], minute = b[4], second = b[5];
        sbyte gmt = unchecked((sbyte)b[6]);
        if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 60) return null;
        try
        {
            var offset = TimeSpan.FromMinutes(gmt * 15);
            return new DateTimeOffset(year, month, day, hour, minute, Math.Min(second, 59), offset);
        }
        catch { return null; }
    }

    // ISO 8601 17-byte digit form: "YYYYMMDDHHMMSSCC" + 1-byte GMT offset.
    private static DateTimeOffset? ParseLongTime(ReadOnlySpan<byte> b)
    {
        static int N(ReadOnlySpan<byte> s, int off, int len)
        {
            int v = 0;
            for (int i = 0; i < len; i++)
            {
                byte c = s[off + i];
                if (c < '0' || c > '9') return -1;
                v = v * 10 + (c - '0');
            }
            return v;
        }
        int year = N(b, 0, 4), month = N(b, 4, 2), day = N(b, 6, 2);
        int hour = N(b, 8, 2), minute = N(b, 10, 2), second = N(b, 12, 2);
        sbyte gmt = unchecked((sbyte)b[16]);
        if (year < 1 || month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 60) return null;
        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, Math.Min(second, 59), TimeSpan.FromMinutes(gmt * 15));
        }
        catch { return null; }
    }

    // SL component list: each component is flags(1) + length(1) + content(length).
    private static string AppendSymlink(string acc, ReadOnlySpan<byte> comps)
    {
        int q = 0;
        while (q + 2 <= comps.Length)
        {
            byte cflags = comps[q];
            int clen = comps[q + 1];
            if (q + 2 + clen > comps.Length) break;
            var content = comps.Slice(q + 2, clen);

            string piece = (cflags & 0x08) != 0 ? "/"        // ROOT
                         : (cflags & 0x02) != 0 ? "."        // CURRENT
                         : (cflags & 0x04) != 0 ? ".."       // PARENT
                         : Utf8(content);

            if (piece == "/")
                acc = "/";
            else
            {
                if (acc.Length > 0 && !acc.EndsWith('/')) acc += "/";
                acc += piece;
            }
            // bit0 CONTINUE would mean this component continues in the next SL record — rare; treated as a whole piece here.
            q += 2 + clen;
        }
        return acc;
    }

    // "Both-endian" 8-byte field (ISO 9660 7.3.3): little-endian half first.
    private static uint BothU32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off, 4));

    private static string Utf8(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b);
}
