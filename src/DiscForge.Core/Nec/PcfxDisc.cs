// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Nec;

/// <summary>What DiscForge could read from a candidate NEC PC-FX disc.</summary>
public sealed record PcfxDisc
{
    public required bool IsPcfx { get; init; }
    /// <summary>Byte offset in the scanned image where the boot signature was found, or -1.</summary>
    public required long SignatureOffset { get; init; }
    /// <summary>Readable ASCII text from the boot header (title / copyright), best-effort.</summary>
    public required string BootText { get; init; }

    public string Summary() => IsPcfx
        ? $"NEC PC-FX disc — boot signature at 0x{SignatureOffset:X}" +
          (BootText.Length > 0 ? $"; boot text: {BootText}" : "")
        : "Not a PC-FX disc (no \"PC-FX:Hu_CD-ROM\" boot signature found).";
}

/// <summary>
/// pcfx-info — identify a NEC PC-FX disc. Every PC-FX game boots from a data sector stamped with the ASCII
/// signature "PC-FX:Hu_CD-ROM"; this locates that signature anywhere in the disc's data area (so it works
/// whatever the sector framing) and surfaces the readable boot-header text — the title and copyright the boot
/// area carries — for cataloguing. Identification and reporting only; it reads nothing protected and defeats
/// no protection.
/// </summary>
public static class Pcfx
{
    /// <summary>The boot signature every PC-FX disc carries at the head of its boot sector.</summary>
    private static readonly byte[] Signature = "PC-FX:Hu_CD-ROM"u8.ToArray();

    /// <summary>True if the buffer contains the PC-FX boot signature.</summary>
    public static bool IsPcfx(ReadOnlySpan<byte> data) => IndexOf(data, Signature) >= 0;

    /// <summary>Identify a PC-FX disc from a leading slice of its image, reading the boot-header text if present.</summary>
    public static PcfxDisc Identify(ReadOnlySpan<byte> data)
    {
        int at = IndexOf(data, Signature);
        if (at < 0)
            return new PcfxDisc { IsPcfx = false, SignatureOffset = -1, BootText = "" };

        // Surface the printable ASCII in the boot sector following the signature (title / publisher / copyright).
        int end = Math.Min(data.Length, at + 0x800);
        string text = PrintableRuns(data[at..end], minRun: 4, take: 6);
        return new PcfxDisc { IsPcfx = true, SignatureOffset = at, BootText = text };
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack[i] != needle[0]) continue;
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }

    /// <summary>Join the first <paramref name="take"/> printable-ASCII runs of at least <paramref name="minRun"/> chars.</summary>
    private static string PrintableRuns(ReadOnlySpan<byte> data, int minRun, int take)
    {
        var runs = new List<string>();
        var sb = new StringBuilder();
        foreach (byte b in data)
        {
            if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
            else
            {
                if (sb.Length >= minRun) { runs.Add(sb.ToString().Trim()); if (runs.Count >= take) break; }
                sb.Clear();
            }
        }
        if (sb.Length >= minRun && runs.Count < take) runs.Add(sb.ToString().Trim());
        return string.Join(" | ", runs);
    }
}
