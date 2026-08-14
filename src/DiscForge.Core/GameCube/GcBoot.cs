// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.GameCube;

/// <summary>The GameCube apploader header — the small loader the console runs before the game's DOL.</summary>
public sealed record GcApploader(string Date, uint EntryPoint, uint Size, uint TrailerSize);

/// <summary>Summary of the game's DOL executable: its entry point, section counts and total size.</summary>
public sealed record GcDol(uint EntryPoint, int TextSections, int DataSections, long TotalSize, uint BssSize);

/// <summary>
/// GameCube boot metadata — the apploader and the DOL executable. Every GameCube disc places its
/// apploader at a fixed 0x2440 (a build-date string, entry point and size) and points at its main
/// executable, the DOL, from the boot header. The DOL header lists up to seven code and eleven data
/// sections, a BSS region and an entry point. This reads that metadata — the shape of what the disc boots
/// — from the disc's own unencrypted structures. Reads and reports; changes nothing.
/// </summary>
public static class GcBoot
{
    /// <summary>The apploader always sits at this fixed offset on a GameCube disc.</summary>
    public const int ApploaderOffset = 0x2440;

    public static GcApploader ReadApploader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buf = new byte[0x20];
        stream.Seek(ApploaderOffset, SeekOrigin.Begin);
        if (!ReadFull(stream, buf)) throw new GameCubeFormatException("Image is too small to hold an apploader.");
        string date = Encoding.ASCII.GetString(buf, 0, 16).TrimEnd('\0', ' ');
        return new GcApploader(
            Date: date,
            EntryPoint: BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0x10)),
            Size: BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0x14)),
            TrailerSize: BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0x18)));
    }

    public static GcDol ReadDol(Stream stream, long dolOffset)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (dolOffset <= 0 || dolOffset + 0x100 > stream.Length)
            throw new GameCubeFormatException($"DOL offset {dolOffset} is outside the image.");
        var h = new byte[0x100];
        stream.Seek(dolOffset, SeekOrigin.Begin);
        if (!ReadFull(stream, h)) throw new GameCubeFormatException("Truncated DOL header.");

        uint U(int o) => BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(o));

        // 7 text + 11 data sections: file offsets @0x00, sizes @0x90.
        int text = 0, data = 0;
        long maxEnd = 0;
        for (int i = 0; i < 18; i++)
        {
            uint off = U(i * 4);                 // 0x00..0x44 = 18 file offsets
            uint size = U(0x90 + i * 4);         // 0x90..0xD4 = 18 sizes
            if (size == 0) continue;
            if (i < 7) text++; else data++;
            maxEnd = Math.Max(maxEnd, (long)off + size);
        }

        return new GcDol(
            EntryPoint: U(0xE0),
            TextSections: text, DataSections: data,
            TotalSize: maxEnd, BssSize: U(0xDC));
    }

    private static bool ReadFull(Stream s, byte[] buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf, read, buf.Length - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}
