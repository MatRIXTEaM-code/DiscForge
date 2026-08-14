// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using System.Text.Json;

namespace DiscForge.Core.Library;

/// <summary>
/// Exports a scanned library as a portable, machine-readable catalog — the single index you keep beside a
/// NAS or cloud copy of an optical archive. For every file it records the identity, size, CRC-32/MD5/SHA-1,
/// verification status against the DAT and canonical name, so any tool (or person) can find, audit and
/// re-verify a disc from the index alone, without re-reading it. This is the bridge between DiscForge's
/// local cataloguing and an off-site backup: DiscForge is the librarian; a sync tool moves the bytes.
/// The JSON form is for programs; the CSV form drops straight into a spreadsheet or NAS index.
/// </summary>
public static class CatalogExport
{
    /// <summary>Serialise the scan as a portable JSON catalog. <paramref name="generatedUtc"/> is stamped verbatim.</summary>
    public static string ToJson(LibraryReport report, string? generatedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var doc = new
        {
            catalog = "discforge/1",
            generated = generatedUtc,
            root = report.Root,
            dat = report.DatName,
            summary = new
            {
                total = report.Total,
                verified = report.Verified,
                misnamed = report.Misnamed,
                duplicates = report.Duplicates,
                unknown = report.Unknown,
                missing = report.Missing.Count,
            },
            entries = report.Entries.Select(e => new
            {
                path = Rel(report.Root, e.Path),
                name = e.FileName,
                size = e.Size,
                format = e.Format,
                platform = string.IsNullOrEmpty(e.RomPlatform) ? null : e.RomPlatform,
                crc32 = e.Crc32Hex,
                md5 = e.Md5,
                sha1 = e.Sha1,
                status = e.Status.ToString(),
                match = e.Match?.Name,
                game = e.Match?.Game,
                canonicalName = e.SuggestedName,
            }),
            missing = report.Missing.Select(m => new { m.Game, m.Name, m.Size, m.Sha1 }),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Serialise the scanned files as CSV — one row per file, for a spreadsheet or NAS index.</summary>
    public static string ToCsv(LibraryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        // Fixed '\n' line endings (not AppendLine's platform newline) so an exported
        // catalog is byte-identical whether it's generated on Windows, macOS or Linux.
        sb.Append("path,name,size,format,platform,crc32,md5,sha1,status,match").Append('\n');
        foreach (var e in report.Entries)
        {
            sb.Append(Csv(Rel(report.Root, e.Path))).Append(',')
              .Append(Csv(e.FileName)).Append(',')
              .Append(e.Size).Append(',')
              .Append(Csv(e.Format)).Append(',')
              .Append(Csv(e.RomPlatform)).Append(',')
              .Append(e.Crc32Hex).Append(',')
              .Append(e.Md5).Append(',')
              .Append(e.Sha1).Append(',')
              .Append(e.Status).Append(',')
              .Append(Csv(e.Match?.Name ?? ""))
              .Append('\n');
        }
        return sb.ToString();
    }

    private static string Rel(string root, string full)
    {
        try { return Path.GetRelativePath(root, full).Replace('\\', '/'); }
        catch { return full.Replace('\\', '/'); }
    }

    /// <summary>Minimal RFC 4180 CSV quoting: wrap in quotes and double any embedded quote when needed.</summary>
    private static string Csv(string s)
    {
        if (s.Length == 0) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
