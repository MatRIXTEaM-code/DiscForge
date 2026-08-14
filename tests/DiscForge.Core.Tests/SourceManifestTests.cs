// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Sources;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>The cloud-source abstraction: manifest parsing, directory expansion, and staging.</summary>
public class SourceManifestTests
{
    [Fact]
    public void ParsesTabsAndDoubleSpaces_AndDetectsUrls()
    {
        string m = "# a comment\n" +
                   "Movies/a.mkv\thttps://example.com/a.mkv\n" +
                   "docs/read me.txt  /some/local/read me.txt\n";   // single space in path survives
        var entries = SourceManifest.Parse(m);
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].IsUrl);
        Assert.Equal("Movies/a.mkv", entries[0].Path);
        Assert.False(entries[1].IsUrl);
        Assert.Equal("docs/read me.txt", entries[1].Path);
        Assert.Equal("/some/local/read me.txt", entries[1].Location);
    }

    [Fact]
    public void RejectsLineWithoutSeparator()
        => Assert.Throws<FormatException>(() => SourceManifest.Parse("justapath\n"));

    [Fact]
    public void ExpandsLocalDirectory_AndStages()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "df_src_" + Guid.NewGuid().ToString("N"));
        string srcDir = Path.Combine(tmp, "src", "Album");
        Directory.CreateDirectory(srcDir);
        File.WriteAllBytes(Path.Combine(srcDir, "01.flac"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(srcDir, "02.flac"), new byte[2000]);
        string loneFile = Path.Combine(tmp, "cover.jpg");
        File.WriteAllBytes(loneFile, new byte[500]);

        try
        {
            var entries = SourceManifest.Parse($"Music\t{Path.Combine(tmp, "src", "Album")}\ncover.jpg\t{loneFile}\n");
            var source = new ManifestSource(entries);

            var listed = source.Enumerate().ToList();
            Assert.Equal(3, listed.Count);
            Assert.Contains(listed, e => e.Path == "Music/01.flac" && e.SizeBytes == 1000);
            Assert.Contains(listed, e => e.Path == "cover.jpg" && e.SizeBytes == 500);

            string stage = Path.Combine(tmp, "stage");
            var res = SourceStager.Stage(source, stage);
            Assert.Equal(3, res.Files);
            Assert.Equal(3500, res.Bytes);
            Assert.Empty(res.Failures);
            Assert.True(File.Exists(Path.Combine(stage, "Music", "01.flac")));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
