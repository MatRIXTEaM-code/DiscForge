// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Rom;

/// <summary>Thrown when a buffer is not a well-formed Famicom Disk System image.</summary>
public sealed class FdsFormatException(string message) : Exception(message);

/// <summary>One file recorded on an FDS disk side.</summary>
public sealed record FdsFile(int Number, int Id, string Name, int Address, int Size, string Kind);

/// <summary>One side of an FDS disk: its header identity plus the files it carries.</summary>
public sealed record FdsSide
{
    public required string GameName { get; init; }
    public required string MakerCode { get; init; }
    public required int SideNumber { get; init; }
    public required int DiskNumber { get; init; }
    public required int FileCount { get; init; }
    public required IReadOnlyList<FdsFile> Files { get; init; }
}

/// <summary>A parsed FDS image (one or more disk sides), and whether it carried the fwNES wrapper header.</summary>
public sealed record FdsImage
{
    public required bool HadFwNesHeader { get; init; }
    public required int SideCount { get; init; }
    public required IReadOnlyList<FdsSide> Sides { get; init; }
}

/// <summary>
/// fds-info — read a Famicom Disk System (.fds) disk image. The FDS stored games on Nintendo's Quick Disk
/// magnetic media; a dump is a plain concatenation of the disk's block stream (no gaps or CRCs), optionally
/// wrapped in a 16-byte fwNES header. Each 65500-byte side opens with a disk-info block stamped
/// "*NINTENDO-HVC*", then a file-count block, then a header+data block per file. This walks that structure and
/// reports each side's identity and file table (name, type, load address, size). Read-only; it identifies and
/// lists, decrypts nothing and defeats no protection.
/// </summary>
public static class Fds
{
    /// <summary>Bytes in one FDS disk side.</summary>
    public const int SideBytes = 65500;

    /// <summary>The verification string at the head of every side's disk-info block.</summary>
    private static readonly byte[] NintendoHvc = "*NINTENDO-HVC*"u8.ToArray();

    /// <summary>True if the buffer starts with the fwNES header or a raw disk-info block.</summary>
    public static bool IsFds(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 'F' && data[1] == 'D' && data[2] == 'S' && data[3] == 0x1A)
            return true;
        return StartsWithDiskInfo(data);
    }

    private static bool StartsWithDiskInfo(ReadOnlySpan<byte> data) =>
        data.Length >= 1 + NintendoHvc.Length && data[0] == 0x01 &&
        data.Slice(1, NintendoHvc.Length).SequenceEqual(NintendoHvc);

    public static FdsImage Read(ReadOnlySpan<byte> data)
    {
        bool fwNes = data.Length >= 16 && data[0] == 'F' && data[1] == 'D' && data[2] == 'S' && data[3] == 0x1A;
        int headerSides = fwNes ? data[4] : 0;
        ReadOnlySpan<byte> body = fwNes ? data[16..] : data;

        if (!StartsWithDiskInfo(body))
            throw new FdsFormatException("Not an FDS image — the disk-info block does not begin with \"*NINTENDO-HVC*\".");

        int sideCount = fwNes && headerSides > 0 ? headerSides : Math.Max(1, body.Length / SideBytes);
        var sides = new List<FdsSide>();

        for (int s = 0; s < sideCount; s++)
        {
            int baseOff = s * SideBytes;
            if (baseOff + 56 > body.Length) break;
            var side = body.Slice(baseOff, Math.Min(SideBytes, body.Length - baseOff));
            if (!StartsWithDiskInfo(side)) break;
            sides.Add(ReadSide(side));
        }

        if (sides.Count == 0)
            throw new FdsFormatException("No readable FDS disk sides were found.");

        return new FdsImage { HadFwNesHeader = fwNes, SideCount = sides.Count, Sides = sides };
    }

    private static FdsSide ReadSide(ReadOnlySpan<byte> side)
    {
        // Disk-info block (56 bytes): maker@0x0F, 3-char game name@0x10, side@0x15, disk@0x16.
        string maker = $"0x{side[0x0F]:X2}";
        string game = Ascii(side.Slice(0x10, 3));
        int sideNumber = side[0x15];
        int diskNumber = side[0x16];

        int pos = 56;
        // File-amount block (2 bytes): 0x02, count.
        int declared = 0;
        if (pos + 2 <= side.Length && side[pos] == 0x02)
        {
            declared = side[pos + 1];
            pos += 2;
        }

        var files = new List<FdsFile>();
        while (files.Count < declared && pos + 16 <= side.Length && side[pos] == 0x03)
        {
            var h = side.Slice(pos, 16);
            int number = h[1];
            int id = h[2];
            string name = Ascii(h.Slice(3, 8));
            int address = h[11] | (h[12] << 8);
            int size = h[13] | (h[14] << 8);
            string kind = h[15] switch { 0 => "PRG", 1 => "CHR", 2 => "VRAM", _ => $"type {h[15]}" };
            pos += 16;

            // File-data block: 0x04 then `size` bytes.
            if (pos < side.Length && side[pos] == 0x04)
                pos += 1 + size;

            files.Add(new FdsFile(number, id, name, address, size, kind));
        }

        return new FdsSide
        {
            GameName = game,
            MakerCode = maker,
            SideNumber = sideNumber,
            DiskNumber = diskNumber,
            FileCount = declared,
            Files = files,
        };
    }

    private static string Ascii(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[bytes.Length];
        int n = 0;
        foreach (byte b in bytes)
            chars[n++] = b is >= 0x20 and < 0x7F ? (char)b : ' ';
        return new string(chars[..n]).TrimEnd();
    }
}
