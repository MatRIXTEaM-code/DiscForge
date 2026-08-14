// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Dat;
using DiscForge.Core.Files;
using DiscForge.Core.Library;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The DAT-audit HTML report renders a LibraryReport as a friendly, self-contained page. This scans a small
/// folder against a DAT (one verified file, one mis-named, one unknown) and confirms the rendered page is
/// well-formed enough to carry the verdict: the audit title, the status badges, and the staged rename preview.
/// </summary>
public class LibraryReportHtmlTests
{
    [Fact]
    public void Renders_the_audit_with_statuses_and_a_staged_rename()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dforge_lh_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "Good Game (USA).bin"), Encoding.ASCII.GetBytes(new string('A', 5000)));
            File.WriteAllBytes(Path.Combine(dir, "wrongname.bin"), Encoding.ASCII.GetBytes(new string('B', 6000)));
            File.WriteAllBytes(Path.Combine(dir, "mystery.bin"), Encoding.ASCII.GetBytes(new string('C', 7000)));

            DatBuildRom Rom(string game, string onDisk, string datName)
            {
                var s = ImageChecksums.ComputeFile(Path.Combine(dir, onDisk));
                return new DatBuildRom(game, datName, s.Length, s.Crc32, s.Md5, s.Sha1);
            }
            var dat = DatFile.ParseText(DatBuilder.Build("Ref", new[]
            {
                Rom("Good Game (USA)", "Good Game (USA).bin", "Good Game (USA).bin"),
                Rom("Cool Game (USA)", "wrongname.bin", "Cool Game (USA).bin"),
            }));

            var report = LibraryScanner.Scan(dir, dat);
            var html = LibraryReportHtml.Render(report);

            Assert.Contains("DAT audit", html);
            Assert.Contains("VERIFIED", html);
            Assert.Contains("RENAME", html);                       // the mis-named file's badge
            Assert.Contains("Cool Game (USA).bin", html);          // the staged rename target
            Assert.Contains("<table", html);
            Assert.EndsWith("</html>\n", html);
        }
        finally { Directory.Delete(dir, true); }
    }
}
