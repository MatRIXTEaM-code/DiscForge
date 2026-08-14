// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Rom;

/// <summary>One file the Neo Geo CD IPL loads at boot: the file name and where it is placed in memory.</summary>
public sealed record IplEntry(string FileName, int? Bank, int? Offset, string RawLine)
{
    public override string ToString()
        => Bank is { } b && Offset is { } o
            ? $"{FileName} → bank {b:X2}:{o:X4}"
            : FileName;
}

/// <summary>The parsed IPL.TXT boot script of a Neo Geo CD disc.</summary>
public sealed record NeoGeoCdBoot
{
    public required IReadOnlyList<IplEntry> Entries { get; init; }

    public IEnumerable<string> Files => Entries.Select(e => e.FileName);
    public bool IsBoot => Entries.Count > 0;

    public string Summary()
        => IsBoot
            ? $"Neo Geo CD IPL: {Entries.Count} boot file(s) — {string.Join(", ", Files.Take(6))}" +
              (Entries.Count > 6 ? ", …" : "") + "."
            : "No Neo Geo CD boot entries (empty IPL.TXT).";
}

/// <summary>
/// neogeo-ipl — the reader for a Neo Geo CD disc's boot script, IPL.TXT. When the console boots a CD it
/// reads IPL.TXT — a plain comma-separated list of the files to load and where each goes in memory (the
/// target bank and offset). This parses that list into an ordered load table: the program, fix, sprite,
/// sound and Z80 files a title pulls in at startup, which both identifies the disc and shows exactly what
/// it loads. Lines are "NAME,bank,offset" (bank and offset in hex); blank lines, comments and the trailing
/// terminator are ignored. Read-only; it parses and reports.
/// </summary>
public static class NeoGeoCdIpl
{
    /// <summary>Parse IPL.TXT text into the boot table.</summary>
    public static NeoGeoCdBoot Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<IplEntry>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            string name = parts[0];
            if (name.Length == 0) continue;                       // terminator / blank name → skip

            int? bank = parts.Length > 1 ? ParseHex(parts[1]) : null;
            int? offset = parts.Length > 2 ? ParseHex(parts[2]) : null;
            entries.Add(new IplEntry(name, bank, offset, line));
        }
        return new NeoGeoCdBoot { Entries = entries };
    }

    /// <summary>Parse IPL.TXT from its raw bytes (ASCII/Latin-1).</summary>
    public static NeoGeoCdBoot Parse(ReadOnlySpan<byte> bytes)
        => Parse(Encoding.Latin1.GetString(bytes));

    /// <summary>A quick heuristic that a blob looks like an IPL.TXT (printable, comma-structured lines).</summary>
    public static bool LooksLikeIpl(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 64 * 1024) return false;
        int printable = 0, commas = 0;
        foreach (byte b in bytes)
        {
            if (b is (>= 0x20 and < 0x7F) or (byte)'\r' or (byte)'\n' or (byte)'\t') printable++;
            if (b == (byte)',') commas++;
        }
        return printable == bytes.Length && commas >= 1;
    }

    public static string Render(NeoGeoCdBoot boot)
    {
        ArgumentNullException.ThrowIfNull(boot);
        var sb = new StringBuilder();
        sb.AppendLine(boot.Summary());
        foreach (var e in boot.Entries) sb.AppendLine($"  {e}");
        return sb.ToString().TrimEnd();
    }

    private static int? ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : null;
    }
}
