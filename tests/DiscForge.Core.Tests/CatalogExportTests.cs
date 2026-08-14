// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text.Json;
using DiscForge.Core.Library;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// catalog-export turns a library scan into a portable index. These tests build a small LibraryReport and
/// confirm the JSON carries the catalog version, a stamped timestamp, the summary, and per-file identity +
/// hashes with paths made relative to the archive root, and that the CSV has the expected header and a row
/// per file. (Validated end-to-end in-cloud too: exported hashes matched sha1sum/md5sum exactly.)
/// </summary>
public class CatalogExportTests
{
    private static LibraryReport Sample()
    {
        var e1 = new LibraryEntry
        {
            Path = "/archive/games/game1.iso", FileName = "game1.iso", Size = 53248,
            Format = "ISO 9660", Crc32 = 0x0F249D3A, Md5 = "e47120b77878144d23ad0a6c377bf419",
            Sha1 = "252757c1da8fa6b6664dc8e4c2b5109b1e39e34c", Status = LibraryStatus.Unchecked,
        };
        var e2 = new LibraryEntry
        {
            Path = "/archive/misc.bin", FileName = "misc.bin", Size = 3000,
            Format = "Unknown", Crc32 = 0xB26B8940, Md5 = "957a5bf1626328596e72a93e8ca010ad",
            Sha1 = "082c09359c0438e3930263c933b2de011feb2320", Status = LibraryStatus.Unchecked,
        };
        return new LibraryReport
        {
            Root = "/archive", Entries = new[] { e1, e2 }, Missing = System.Array.Empty<DiscForge.Core.Dat.DatRom>(),
        };
    }

    [Fact]
    public void Json_carries_version_summary_and_relative_paths()
    {
        var json = CatalogExport.ToJson(Sample(), generatedUtc: "2026-08-08T00:00:00.0000000Z");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("discforge/1", root.GetProperty("catalog").GetString());
        Assert.Equal("2026-08-08T00:00:00.0000000Z", root.GetProperty("generated").GetString());
        Assert.Equal(2, root.GetProperty("summary").GetProperty("total").GetInt32());

        var entries = root.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        var first = entries[0];
        Assert.Equal("games/game1.iso", first.GetProperty("path").GetString());   // relative to root
        Assert.Equal("ISO 9660", first.GetProperty("format").GetString());
        Assert.Equal("252757c1da8fa6b6664dc8e4c2b5109b1e39e34c", first.GetProperty("sha1").GetString());
        Assert.Equal("Unchecked", first.GetProperty("status").GetString());
    }

    [Fact]
    public void Csv_has_a_header_and_a_row_per_file()
    {
        var csv = CatalogExport.ToCsv(Sample());
        var lines = csv.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("path,name,size,format,platform,crc32,md5,sha1,status,match", lines[0]);
        Assert.Equal(3, lines.Length);   // header + 2 files
        Assert.Contains("games/game1.iso", lines[1]);
        Assert.Contains("252757c1da8fa6b6664dc8e4c2b5109b1e39e34c", lines[1]);
    }
}
