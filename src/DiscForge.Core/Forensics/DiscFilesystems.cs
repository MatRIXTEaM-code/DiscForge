// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>One filesystem (or partition structure) recognised on a disc image.</summary>
public sealed record VolumeFilesystem(string Kind, string? Label, string Detail);

/// <summary>What filesystems a disc carries.</summary>
public sealed record FilesystemReport
{
    public required IReadOnlyList<VolumeFilesystem> Filesystems { get; init; }

    /// <summary>More than one filesystem <i>family</i> — a genuine hybrid disc (Mac + PC,
    /// or an ISO 9660 + UDF "bridge" DVD). Joliet and CD-XA are extensions of the ISO
    /// family, not separate filesystems, so they do not by themselves make a hybrid.</summary>
    public bool IsHybrid => Filesystems.Select(f => Family(f.Kind)).Distinct().Count() > 1;

    public bool Any => Filesystems.Count > 0;

    private static string Family(string kind) => kind switch
    {
        "ISO 9660" or "Joliet" or "CD-XA" => "iso",
        "UDF" => "udf",
        _ => "apple",   // Apple HFS / HFS+ / HFSX / Apple Partition Map
    };

    public string Summary()
    {
        if (!Any) return "No recognised filesystem — a raw or non-standard image.";
        string list = string.Join(", ", Filesystems.Select(f => f.Kind));
        return (IsHybrid ? "Hybrid disc: " : "") + list + ".";
    }
}

/// <summary>
/// Identifies every filesystem a disc image carries — not just the one a PC would
/// mount. Real discs are often hybrids: a Mac + PC CD pairs ISO 9660 with Apple HFS;
/// a video DVD is an ISO 9660 + UDF "bridge"; a PlayStation disc is ISO 9660 marked
/// CD-XA. Knowing all of them is the difference between preserving a disc and
/// preserving only the half your OS happened to read.
///
/// Detection is by the on-disc signatures at their standard offsets — the volume
/// descriptors (ISO 9660 / Joliet), the UDF Volume Recognition Sequence, the CD-XA
/// marker in the primary descriptor, the Apple Partition Map, and the HFS / HFS+
/// volume headers. It reads structure; it decodes nothing protected.
///
/// Expects a cooked image with 2048-byte sectors.
/// </summary>
public static class DiscFilesystems
{
    private const int SS = 2048;

    public static FilesystemReport Identify(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var found = new List<VolumeFilesystem>();

        // ISO 9660 / Joliet / CD-XA — walk the volume descriptor set at sector 16.
        for (long lba = 16; lba < 96; lba++)
        {
            long off = lba * SS;
            if (off + SS > image.Length) break;
            if (!Ascii(image, off + 1, "CD001")) break;

            byte type = image[off];
            if (type == 0xFF) break;   // terminator

            if (type == 1)   // Primary Volume Descriptor
            {
                string label = ReadString(image, off + 40, 32);
                found.Add(new VolumeFilesystem("ISO 9660", label.Length > 0 ? label : null,
                    "Primary volume descriptor at sector 16."));

                // CD-XA marks the PVD at byte offset 1024 with "CD-XA001".
                if (Ascii(image, off + 1024, "CD-XA001"))
                    found.Add(new VolumeFilesystem("CD-XA", label.Length > 0 ? label : null,
                        "CD-XA001 marker in the primary descriptor (Mode 2 Form 1/2 disc)."));
            }
            else if (type == 2 && image[off + 88] == (byte)'%' && image[off + 89] == (byte)'/')   // Supplementary — Joliet
            {
                char level = (char)image[off + 90];
                found.Add(new VolumeFilesystem("Joliet", ReadUnicode(image, off + 40, 32) is { Length: > 0 } l ? l : null,
                    $"UCS-2 supplementary descriptor (level {(level is '@' ? "1" : level is 'C' ? "2" : "3")})."));
            }
        }

        // UDF — the Volume Recognition Sequence: BEA01 … NSR02/NSR03 … TEA01, one per sector from 16.
        string? nsr = null;
        bool bea = false, tea = false;
        for (long lba = 16; lba < 64; lba++)
        {
            long off = lba * SS;
            if (off + SS > image.Length) break;
            if (Ascii(image, off + 1, "BEA01")) bea = true;
            else if (Ascii(image, off + 1, "NSR02")) nsr = "NSR02";
            else if (Ascii(image, off + 1, "NSR03")) nsr = "NSR03";
            else if (Ascii(image, off + 1, "TEA01")) tea = true;
        }
        if (nsr != null)
            found.Add(new VolumeFilesystem("UDF", null,
                $"Volume Recognition Sequence ({nsr}{(bea ? ", BEA01" : "")}{(tea ? ", TEA01" : "")})."));

        // Apple Partition Map — driver descriptor "ER" at sector 0, partition entries "PM" at sector 1.
        if (image.Length >= 2 * SS && image[SS] == (byte)'P' && image[SS + 1] == (byte)'M')
            found.Add(new VolumeFilesystem("Apple Partition Map", null,
                "Partition map at block 1 — an Apple-partitioned disc."));

        // Apple HFS / HFS+ volume header at byte offset 1024 (block 2).
        if (image.Length >= 1024 + 512)
        {
            ushort sig = (ushort)((image[1024] << 8) | image[1025]);
            if (sig == 0x4244)   // 'BD' — HFS Master Directory Block
            {
                string name = ReadPascal(image, 1024 + 36, 27);
                found.Add(new VolumeFilesystem("Apple HFS", name.Length > 0 ? name : null,
                    "HFS Master Directory Block at block 2."));
            }
            else if (sig == 0x482B || sig == 0x4858)   // 'H+' / 'HX'
                found.Add(new VolumeFilesystem(sig == 0x482B ? "Apple HFS+" : "Apple HFSX", null,
                    "HFS+ volume header at block 2."));
        }

        return new FilesystemReport { Filesystems = found };
    }

    // ---- helpers ------------------------------------------------------------

    private static bool Ascii(byte[] data, long offset, string token)
    {
        if (offset < 0 || offset + token.Length > data.Length) return false;
        for (int i = 0; i < token.Length; i++)
            if (data[offset + i] != (byte)token[i]) return false;
        return true;
    }

    private static string ReadString(byte[] data, long offset, int len)
    {
        if (offset < 0 || offset + len > data.Length) return "";
        return Encoding.ASCII.GetString(data, (int)offset, len).TrimEnd(' ', '\0');
    }

    private static string ReadUnicode(byte[] data, long offset, int len)
    {
        if (offset < 0 || offset + len > data.Length) return "";
        return Encoding.BigEndianUnicode.GetString(data, (int)offset, len).TrimEnd(' ', '\0');
    }

    private static string ReadPascal(byte[] data, long offset, int max)
    {
        if (offset < 0 || offset >= data.Length) return "";
        int n = Math.Min(data[offset], max);
        if (offset + 1 + n > data.Length) return "";
        return Encoding.ASCII.GetString(data, (int)offset + 1, n).TrimEnd(' ', '\0');
    }
}
