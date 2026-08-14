// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Files;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the image browser, run against the real fixture images rather than
/// synthetic ones.
///
/// That distinction matters. A synthetic image tests the reader against what the
/// writer believes the format to be, which passes happily while both are wrong
/// together. These fixtures were produced by genisoimage and cdi4dc — other
/// people's tools, following the specification independently — so reading them
/// correctly is evidence about the format rather than about our own consistency.
///
/// The fixtures are optional: a checkout without them skips rather than fails,
/// following the convention CdiExtractorTests established. A test that fails
/// because a large binary wasn't cloned teaches people to ignore red builds.
/// </summary>
public class ImageBrowserTests
{
    private static string FindFixtures()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return AppContext.BaseDirectory;
    }

    private static string? Fixture(params string[] parts)
    {
        var path = Path.Combine(new[] { FindFixtures() }.Concat(parts).ToArray());
        return File.Exists(path) ? path : null;
    }

    // --- ISO 9660 ------------------------------------------------------------

    [Fact]
    public void An_iso_lists_its_files()
    {
        var iso = Fixture("source.iso");
        if (iso is null) return;      // fixtures optional in some checkouts

        var listing = ImageBrowser.List(iso);

        Assert.Null(listing.Error);
        Assert.Contains("ISO 9660", listing.Filesystem);
        Assert.NotEmpty(listing.Files);
    }

    [Fact]
    public void An_iso_reports_its_volume_name()
    {
        var iso = Fixture("source.iso");
        if (iso is null) return;

        var listing = ImageBrowser.List(iso);

        // The fixture README records this: genisoimage was given OJTEST.
        Assert.Equal("OJTEST", listing.VolumeId);
    }

    [Fact]
    public void Listed_sizes_are_positive_and_total_correctly()
    {
        var iso = Fixture("source.iso");
        if (iso is null) return;

        var listing = ImageBrowser.List(iso);

        Assert.All(listing.Files, f => Assert.True(f.Size >= 0));
        Assert.Equal(listing.Files.Sum(f => f.Size), listing.TotalBytes);
    }

    [Fact]
    public void Listed_paths_are_absolute_and_use_forward_slashes()
    {
        // The rest of DiscForge assumes this shape — SafeTarget strips a leading
        // slash and swaps separators — so a reader that returned Windows-style
        // paths would produce extraction targets in the wrong place.
        var iso = Fixture("source.iso");
        if (iso is null) return;

        var listing = ImageBrowser.List(iso);

        Assert.All(listing.Files, f =>
        {
            Assert.StartsWith("/", f.Path);
            Assert.DoesNotContain('\\', f.Path);
        });
    }

    [Fact]
    public void Extracting_a_file_reproduces_its_declared_size()
    {
        var iso = Fixture("source.iso");
        if (iso is null) return;

        var listing = ImageBrowser.List(iso);
        var file = listing.Files.FirstOrDefault(f => f.Size > 0);
        if (file is null) return;

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = ImageBrowser.Extract(iso, new[] { file }, dir.FullName);

            Assert.Equal(1, result.Extracted);
            Assert.Equal(0, result.Failed);

            // The declared length is what the directory record says; the bytes
            // written must agree, or the extractor is reading the wrong extent.
            Assert.Equal(file.Size, result.BytesWritten);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Extracting_everything_writes_every_file()
    {
        var iso = Fixture("source.iso");
        if (iso is null) return;

        var listing = ImageBrowser.List(iso);
        if (listing.Files.Count == 0) return;

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = ImageBrowser.Extract(iso, listing.Files, dir.FullName);

            Assert.Equal(listing.Files.Count, result.Extracted);
            Assert.Empty(result.Problems);

            int onDisk = Directory.GetFiles(dir.FullName, "*", SearchOption.AllDirectories).Length;
            Assert.Equal(listing.Files.Count, onDisk);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Extraction_recreates_the_directory_structure()
    {
        var iso = Fixture("source.iso");
        if (iso is null) return;

        var listing = ImageBrowser.List(iso);
        var nested = listing.Files.FirstOrDefault(f => f.Path.TrimStart('/').Contains('/'));
        if (nested is null) return;      // this fixture may be flat

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            ImageBrowser.Extract(iso, new[] { nested }, dir.FullName);

            string expected = Path.Combine(dir.FullName,
                nested.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(expected),
                $"Expected '{expected}' to exist after extracting '{nested.Path}'.");
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    // --- UDF -----------------------------------------------------------------

    [Fact]
    public void A_udf_image_lists_its_files()
    {
        var udf = Fixture("udf", "udf_test.iso");
        if (udf is null) return;

        var listing = ImageBrowser.List(udf);

        Assert.Null(listing.Error);
        Assert.NotEmpty(listing.Files);
    }

    [Fact]
    public void The_udf_fixtures_known_files_are_all_found()
    {
        // The fixture README records exactly what this volume holds, which makes
        // it a stronger test than counting: a reader that found four files but
        // the wrong four would pass a count and fail this.
        var udf = Fixture("udf", "udf_test.iso");
        if (udf is null) return;

        var listing = ImageBrowser.List(udf);
        var paths = listing.Files.Select(f => f.Path).ToList();

        Assert.Contains("/readme.txt", paths);
        Assert.Contains("/data.bin", paths);
        Assert.Contains("/deep/inner.txt", paths);
        Assert.Contains("/deep/deeper/tiny.txt", paths);
    }

    [Fact]
    public void The_udf_fixtures_file_sizes_match_the_manifest()
    {
        var udf = Fixture("udf", "udf_test.iso");
        if (udf is null) return;

        var listing = ImageBrowser.List(udf);

        Assert.Equal(16, listing.Files.Single(f => f.Path == "/readme.txt").Size);
        Assert.Equal(5000, listing.Files.Single(f => f.Path == "/data.bin").Size);
        Assert.Equal(21, listing.Files.Single(f => f.Path == "/deep/inner.txt").Size);
        Assert.Equal(1, listing.Files.Single(f => f.Path == "/deep/deeper/tiny.txt").Size);
    }

    [Fact]
    public void Extracted_udf_content_matches_what_the_manifest_says_it_holds()
    {
        // The strongest test available: the README documents the exact bytes,
        // so this checks the extractor produced the right content rather than
        // merely the right length.
        var udf = Fixture("udf", "udf_test.iso");
        if (udf is null) return;

        var listing = ImageBrowser.List(udf);
        var readme = listing.Files.SingleOrDefault(f => f.Path == "/readme.txt");
        var data = listing.Files.SingleOrDefault(f => f.Path == "/data.bin");
        if (readme is null || data is null) return;

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            ImageBrowser.Extract(udf, new[] { readme, data }, dir.FullName);

            var text = File.ReadAllText(Path.Combine(dir.FullName, "readme.txt"));
            Assert.Equal("hello udf world\n", text.Replace("\r\n", "\n"));

            var bytes = File.ReadAllBytes(Path.Combine(dir.FullName, "data.bin"));
            Assert.Equal(5000, bytes.Length);
            for (int i = 0; i < bytes.Length; i++)
                Assert.Equal((byte)((i * 13 + 7) & 0xFF), bytes[i]);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    // --- refusals and edges --------------------------------------------------

    [Fact]
    public void A_missing_file_is_reported_rather_than_thrown()
    {
        var listing = ImageBrowser.List(@"C:\nowhere\nothing.iso");

        Assert.NotNull(listing.Error);
        Assert.Empty(listing.Files);
    }

    [Fact]
    public void A_raw_bin_with_no_filesystem_is_refused_with_an_explanation()
    {
        // A raw bin is now probed for its sector layout (the psxrip case), but
        // one that carries no ISO 9660 filesystem — here, all zeros — must still
        // fail with a clear message rather than an empty listing that reads as an
        // empty disc.
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(temp, new byte[2352 * 20]);

            var listing = ImageBrowser.List(temp);

            Assert.NotNull(listing.Error);
            Assert.Contains("No ISO 9660 filesystem", listing.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_file_that_is_not_an_image_is_refused()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".iso");
        try
        {
            File.WriteAllText(temp, "This is not an ISO, it is a sentence.");

            var listing = ImageBrowser.List(temp);

            Assert.NotNull(listing.Error);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    [Fact]
    public void Extraction_refuses_paths_that_escape_the_target_directory()
    {
        // Paths come from the image and are untrusted. A crafted one containing
        // "../" would otherwise write wherever it liked — the classic archive
        // traversal, and no less a problem for arriving on an optical disc.
        var iso = Fixture("source.iso");
        if (iso is null) return;

        var listing = ImageBrowser.List(iso);
        if (listing.Files.Count == 0) return;

        var evil = listing.Files[0] with { Path = "/../../escaped.txt" };

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = ImageBrowser.Extract(iso, new[] { evil }, dir.FullName);

            Assert.Equal(0, result.Extracted);
            Assert.Equal(1, result.Failed);
            Assert.False(File.Exists(Path.Combine(dir.FullName, "..", "..", "escaped.txt")));
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_file_not_present_in_the_image_fails_that_one_alone()
    {
        var iso = Fixture("source.iso");
        if (iso is null) return;

        var listing = ImageBrowser.List(iso);
        if (listing.Files.Count == 0) return;

        var missing = new ImageBrowser.FileEntry("/does/not/exist.txt", 100);
        var wanted = new[] { listing.Files[0], missing };

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = ImageBrowser.Extract(iso, wanted, dir.FullName);

            Assert.Equal(1, result.Extracted);
            Assert.Equal(1, result.Failed);
            Assert.Single(result.Problems);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Extracting_nothing_succeeds_trivially()
    {
        var iso = Fixture("source.iso");
        if (iso is null) return;

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = ImageBrowser.Extract(iso, Array.Empty<ImageBrowser.FileEntry>(),
                                              dir.FullName);

            Assert.Equal(0, result.Extracted);
            Assert.Equal(0, result.Failed);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }
}