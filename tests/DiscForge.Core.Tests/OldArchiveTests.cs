// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;
using DiscForge.Core.Iso;
using DiscForge.Core.OldArchives;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// LHA/LArc, ARJ and ZOO reading against tests/fixtures/oldarc (see make-fixtures.sh): LHA archives
/// made by the original DOS, Amiga, OS-9 and Unix tools (from the Lhasa test corpus), and ARJ and ZOO
/// archives made by ARJ 3.10 and zoo 2.10. manifest.txt holds the size and SHA-1 of every file as the
/// reference tool (lhasa, arj, zoo) extracted it.
/// </summary>
public class OldArchiveTests
{
    private static string Dir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var c = Path.Combine(dir, "tests", "fixtures", "oldarc");
            if (Directory.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("tests/fixtures/oldarc not found.");
    }

    private static string F(string name) => Path.Combine(Dir(), name);

    private static string Sha1(byte[] b) => System.Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "df-old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Theory]
    [InlineData("lharc113-lh1.lzh", "LHA")]
    [InlineData("lharc113-lh1-long.lzh", "LHA")]      // long enough for LZHUF's tree rebuilds
    [InlineData("larc333-lz4.lzs", "LHA")]
    [InlineData("larc333-lz5.lzs", "LHA")]
    [InlineData("generated-lzs.lzs", "LHA")]
    [InlineData("amiga122-lh4.lzh", "LHA")]
    [InlineData("lha213-lh5.lzh", "LHA")]
    [InlineData("unix114i-h0-lh6.lzh", "LHA")]
    [InlineData("unix114i-h1-lh7.lzh", "LHA")]
    [InlineData("unix114i-h2-lh5.lzh", "LHA")]
    [InlineData("unlha32-h2-lhx.lzh", "LHA")]
    [InlineData("lhark04d-lh7.lzh", "LHA")]            // LHARK's incompatible -lh7-
    [InlineData("osk201-h2-lh5.lzh", "LHA")]           // OS-9/68k's short level-2 header length
    [InlineData("amiga122-sfx.run", "LHA")]            // Amiga self-extractor with a decoy header
    [InlineData("amiga122-lh0-dirs.lzh", "LHA")]       // folders stored as -lh0-
    [InlineData("arj-m0.arj", "ARJ")]
    [InlineData("arj-m1.arj", "ARJ")]
    [InlineData("arj-m2.arj", "ARJ")]
    [InlineData("arj-m3.arj", "ARJ")]
    [InlineData("arj-m4.arj", "ARJ")]
    [InlineData("arj-garbled.arj", "ARJ")]
    [InlineData("arj-multi.arj", "ARJ")]
    [InlineData("zoo-stored.zoo", "ZOO")]
    [InlineData("zoo-lzw.zoo", "ZOO")]
    [InlineData("zoo-lzh.zoo", "ZOO")]
    public void Every_file_matches_the_reference_tool(string archive, string format)
    {
        var expected = File.ReadAllLines(F("manifest.txt"))
            .Where(l => l.StartsWith(archive + "|", StringComparison.Ordinal))
            .Select(l => l[(archive.Length + 1)..])
            .OrderBy(l => l, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(format, OldArchive.Detect(F(archive)));
        using var a = OldArchive.Open(F(archive));
        Assert.Equal(format, a.Format);
        var actual = new List<string>();
        foreach (var e in a.Entries.Where(e => !e.IsDirectory))
        {
            var ms = new MemoryStream();
            a.Extract(e, ms, e.IsEncrypted ? "DiscForge" : null);
            actual.Add($"{ms.Length}|{Sha1(ms.ToArray())}");
        }
        actual.Sort(StringComparer.Ordinal);
        Assert.Equal(string.Join("\n", expected), string.Join("\n", actual));
    }

    [Fact]
    public void Arj_and_zoo_keep_folders_and_names()
    {
        foreach (var f in new[] { "arj-m1.arj", "zoo-lzh.zoo" })
        {
            using var a = OldArchive.Open(F(f));
            var names = a.Entries.Select(e => e.Name).ToList();
            Assert.Contains("docs/notes.txt", names);
            Assert.Contains("readme.txt", names);
            Assert.Contains("noise.bin", names);
        }
    }

    [Fact]
    public void Extract_all_writes_the_tree()
    {
        var dir = TempDir();
        try
        {
            using var a = OldArchive.Open(F("arj-multi.arj"));
            var r = a.ExtractAll(dir);
            Assert.True(r.AllOk);
            Assert.True(File.Exists(Path.Combine(dir, "docs", "notes.txt")));
            Assert.Empty(Directory.GetFiles(dir, "*.dfpart", SearchOption.AllDirectories));
            // Existing files are kept unless overwrite is asked for.
            Assert.Equal(0, a.ExtractAll(dir).Ok);
            Assert.True(a.ExtractAll(dir, overwrite: true).AllOk);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Garbled_arj_needs_the_right_password()
    {
        using var a = OldArchive.Open(F("arj-garbled.arj"));
        Assert.True(a.AnyEncrypted);
        var e = a.Entries.First(x => x.IsEncrypted && x.Size > 100);
        Assert.Throws<OldArchivePasswordException>(() => a.Extract(e, new MemoryStream(), null));
        Assert.Throws<OldArchivePasswordException>(() => a.Extract(e, new MemoryStream(), "nope"));
        Assert.True(a.TestAll("DiscForge").AllOk);
    }

    [Fact]
    public void Arj_volume_set_opens_from_any_volume_and_reports_a_missing_one()
    {
        foreach (var v in new[] { "arj-multi.arj", "arj-multi.a01", "arj-multi.a02" })
        {
            using var a = OldArchive.Open(F(v));
            Assert.Equal(3, a.VolumePaths.Count);
            Assert.Contains(a.Entries, e => e.VolumeCount > 1);
            Assert.True(a.TestAll().AllOk);
        }
        var dir = TempDir();
        try
        {
            File.Copy(F("arj-multi.arj"), Path.Combine(dir, "set.arj"));
            File.Copy(F("arj-multi.a01"), Path.Combine(dir, "set.a01"));
            using var a = OldArchive.Open(Path.Combine(dir, "set.arj"));
            Assert.Contains(a.Warnings, w => w.Contains("set.a02"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Damaged_data_is_caught_by_the_crc()
    {
        var dir = TempDir();
        try
        {
            foreach (var f in new[] { "lha213-lh5.lzh", "arj-m1.arj", "zoo-lzw.zoo" })
            {
                var b = File.ReadAllBytes(F(f));
                b[b.Length / 2] ^= 0x40;
                var p = Path.Combine(dir, f);
                File.WriteAllBytes(p, b);
                using var a = OldArchive.Open(p);
                Assert.False(a.TestAll().AllOk);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Self_extracting_arj_is_found_behind_a_stub()
    {
        var dir = TempDir();
        try
        {
            var stub = new byte[5000];
            "MZ"u8.CopyTo(stub);
            var p = Path.Combine(dir, "setup.exe");
            File.WriteAllBytes(p, stub.Concat(File.ReadAllBytes(F("arj-m4.arj"))).ToArray());
            Assert.Equal("ARJ", OldArchive.Detect(p));
            using var a = OldArchive.Open(p);
            Assert.Equal(5000L, a.StartOffset);
            Assert.True(a.TestAll().AllOk);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Folder_sweep_counts_sets_once_and_flags_problems()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            foreach (var f in new[] { "lha213-lh5.lzh", "zoo-lzh.zoo", "arj-multi.arj", "arj-multi.a01", "arj-multi.a02", "arj-garbled.arj" })
                File.Copy(F(f), Path.Combine(dir, "sub", f));
            var trunc = File.ReadAllBytes(F("arj-m1.arj"));
            File.WriteAllBytes(Path.Combine(dir, "cut.arj"), trunc[..(trunc.Length / 2)]);
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not an archive");

            var items = ArchiveSweep.Run(dir, new SweepOptions());
            Assert.Equal(5, items.Count);   // the three-volume set counts once
            Assert.Equal(3, items.Count(i => i.Ok));
            Assert.Equal(SweepItem.StatusPassword, items.Single(i => i.Path.EndsWith("arj-garbled.arj")).Status);
            Assert.Equal(SweepItem.StatusDamaged, items.Single(i => i.Path.EndsWith("cut.arj")).Status);

            var outDir = Path.Combine(dir, "out");
            var extracted = ArchiveSweep.Run(dir, new SweepOptions { ExtractTo = outDir, Password = "DiscForge" });
            Assert.True(File.Exists(Path.Combine(outDir, "sub", "arj-multi", "docs", "notes.txt")));
            Assert.Contains("Archive,Format,Status", ArchiveSweep.ToCsv(extracted, dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Archives_on_a_disc_image_are_found_and_extracted()
    {
        var dir = TempDir();
        try
        {
            var nodes = new List<IsoBuilder.Node>
            {
                IsoBuilder.Node.Dir("DOS", new[]
                {
                    IsoBuilder.Node.File("GAME.LZH", File.ReadAllBytes(F("lha213-lh5.lzh"))),
                    IsoBuilder.Node.File("TOOLS.ARJ", File.ReadAllBytes(F("arj-multi.arj"))),
                    IsoBuilder.Node.File("TOOLS.A01", File.ReadAllBytes(F("arj-multi.a01"))),
                    IsoBuilder.Node.File("TOOLS.A02", File.ReadAllBytes(F("arj-multi.a02"))),
                }),
                IsoBuilder.Node.File("README.TXT", "hello"u8.ToArray()),
            };
            var iso = Path.Combine(dir, "disc.iso");
            File.WriteAllBytes(iso, IsoBuilder.BuildTree("TESTDISC", nodes).Image);

            var (found, error) = DiscImageArchives.Find(iso);
            Assert.Null(error);
            Assert.Equal(2, found.Count);
            Assert.Equal(3, found.Single(f => f.PathInImage.Contains("TOOLS", StringComparison.OrdinalIgnoreCase)).VolumePathsInImage.Count);

            var outDir = Path.Combine(dir, "out");
            var items = DiscImageArchives.Process(iso, outDir, null, false);
            Assert.True(items.All(i => i.Ok));
            Assert.Contains(Directory.GetFiles(outDir, "notes.txt", SearchOption.AllDirectories), p => p.Contains("TOOLS", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Lhasa_corpus_when_available()
    {
        // Set LHASA_TESTDATA to a Lhasa checkout's test/archives to run over all ~220 archives.
        var root = Environment.GetEnvironmentVariable("LHASA_TESTDATA");
        if (string.IsNullOrEmpty(root)) return;
        int ok = 0;
        var problems = new List<string>();
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            if (OldArchive.Detect(f) != "LHA") continue;
            using var a = OldArchive.Open(f);
            var r = a.TestAll();
            if (r.AllOk) ok++;
            else problems.AddRange(r.Entries.Where(e => !e.Ok && !e.Entry.Method.StartsWith("lh2") && !e.Entry.Method.StartsWith("lh3"))
                .Select(e => $"{f}: {e.Entry.Name}: {e.Error}"));
        }
        Assert.Empty(problems);
        Assert.True(ok > 200);
    }
}
