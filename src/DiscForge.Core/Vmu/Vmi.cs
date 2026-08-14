// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Vmu;

/// <summary>
/// Writes and reads a Dreamcast VMI file — the small metadata descriptor that
/// accompanies a VMS save for download/transfer (it names the VMS resource, the
/// on-card filename, size and flags). This produces the descriptor from a VMS; the
/// VMS data itself lives in its own file. Clean-room, from the public VMI layout.
///
///   0x00  4  checksum (from the resource name AND'd with "SEGA")
///   0x04 32  description
///   0x24 32  copyright
///   0x44  8  creation date/time fields
///   0x4C  2  VMI version (0)
///   0x4E  2  file number (1)
///   0x50 12  VMS resource name
///   0x5C 12  on-card VMU filename
///   0x68  2  file mode (bit0 copy-protect, bit1 game)
///   0x6A  2  reserved
///   0x6C  4  VMS file size in bytes
/// </summary>
public static class Vmi
{
    public const int Size = 0x70;   // 112 bytes

    public sealed record VmiInfo
    {
        public required string Description { get; init; }
        public required string ResourceName { get; init; }
        public required string VmuFileName { get; init; }
        public required int FileSize { get; init; }
        public required bool IsGame { get; init; }
        public required bool CopyProtected { get; init; }
    }

    public static byte[] Create(
        string resourceName, string vmuFileName, string description, int fileSize,
        bool isGame = false, bool copyProtected = false, string copyright = "DiscForge")
    {
        ArgumentNullException.ThrowIfNull(resourceName);
        ArgumentNullException.ThrowIfNull(vmuFileName);

        var v = new byte[Size];
        Ascii(v, 0x04, 32, description ?? "");
        Ascii(v, 0x24, 32, copyright);
        // Fixed date (2026-01-01) for deterministic output.
        BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(0x44), 2026);
        v[0x46] = 0; v[0x47] = 1;   // month (0-based), day
        BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(0x4C), 0);   // VMI version
        BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(0x4E), 1);   // file number
        Ascii(v, 0x50, 12, resourceName);
        Ascii(v, 0x5C, 12, vmuFileName);
        ushort mode = 0;
        if (copyProtected) mode |= 0x01;
        if (isGame) mode |= 0x02;
        BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(0x68), mode);
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(0x6C), (uint)fileSize);

        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(0x00), Checksum(v));
        return v;
    }

    public static VmiInfo Read(byte[] vmi)
    {
        ArgumentNullException.ThrowIfNull(vmi);
        if (vmi.Length < Size) throw new VmuFormatException($"A VMI is {Size} bytes; got {vmi.Length}.");
        ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(vmi.AsSpan(0x68));
        return new VmiInfo
        {
            Description = ReadAscii(vmi, 0x04, 32),
            ResourceName = ReadAscii(vmi, 0x50, 12),
            VmuFileName = ReadAscii(vmi, 0x5C, 12),
            FileSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(vmi.AsSpan(0x6C)),
            CopyProtected = (mode & 0x01) != 0,
            IsGame = (mode & 0x02) != 0,
        };
    }

    /// <summary>The VMI checksum: the first four resource-name bytes AND'd with the
    /// ASCII of "SEGA", little-endian.</summary>
    public static uint Checksum(byte[] vmi)
    {
        byte s = (byte)(vmi[0x50] & 'S');
        byte e = (byte)(vmi[0x51] & 'E');
        byte g = (byte)(vmi[0x52] & 'G');
        byte a = (byte)(vmi[0x53] & 'A');
        return (uint)(s | (e << 8) | (g << 16) | (a << 24));
    }

    private static void Ascii(byte[] buf, int at, int len, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value ?? "");
        Array.Copy(bytes, 0, buf, at, Math.Min(len, bytes.Length));
    }

    private static string ReadAscii(byte[] buf, int at, int len) =>
        Encoding.ASCII.GetString(buf, at, len).TrimEnd('\0', ' ');
}
