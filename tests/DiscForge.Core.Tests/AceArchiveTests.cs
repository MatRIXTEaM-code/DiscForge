// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;
using System.Text;
using DiscForge.Core.Ace;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// ACE reading against tests/fixtures/ace (see make-fixtures.py there): a solid WinAce 2.0 archive cut
/// from a real one, the same with every file encrypted, the same over three volumes, hand-built
/// streams for ACE 1.0 LZ77 and the SOUND and PIC modes, hostile file names and a DOS code-page name.
/// manifest.txt holds the SHA-1 of every member as the reference decoder (acefile) produced it.
/// </summary>
public class AceArchiveTests
{
    private const string Password = "DiscForge";

    private static string FixtureDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "ace");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("tests/fixtures/ace not found.");
    }

    private static string Fixture(string name) => Path.Combine(FixtureDir(), name);

    private static string Sha1(byte[] b) => System.Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "df-ace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Theory]
    [InlineData("winace-solid.ace")]
    [InlineData("winace-solid-password.ace")]
    [InlineData("winace-multi.ace")]
    [InlineData("synthetic-modes.ace")]
    [InlineData("hostile-names.ace")]
    [InlineData("cp437-cafe.ace")]
    public void Every_member_matches_the_reference_decoder(string archive)
    {
        var expected = File.ReadAllLines(Fixture("manifest.txt"))
            .Where(l => l.StartsWith(archive + "|", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(expected);
        using var a = AceArchive.Open(Fixture(archive));
        var actual = new List<string>();
        foreach (var m in a.Members)
        {
            string raw = Encoding.Latin1.GetString(m.RawName);
            if (m.IsDirectory) { actual.Add($"{archive}|{raw}|dir"); continue; }
            var ms = new MemoryStream();
            a.Extract(m, ms, m.IsEncrypted ? Password : null);
            actual.Add($"{archive}|{raw}|{ms.Length}|{Sha1(ms.ToArray())}");
        }
        Assert.Equal(string.Join("\n", expected), string.Join("\n", actual));
    }

    [Fact]
    public void Solid_archive_details_are_read()
    {
        using var a = AceArchive.Open(Fixture("winace-solid.ace"));
        Assert.True(a.IsSolid);
        Assert.False(a.IsMultiVolume);
        Assert.Equal(20, a.VersionNeeded);
        Assert.Equal("Win32", a.HostOs);
        Assert.Equal("*UNREGISTERED VERSION*", a.Advert);
        Assert.Equal("default", a.Comment);
        Assert.Equal(35, a.Members.Count);
        Assert.Equal(21, a.Members.Count(m => m.IsDirectory));
        Assert.Contains(a.Members, m => m.CompressionType == AceDecompressor.CompStored);
        Assert.Contains(a.Members, m => m.CompressionType == AceDecompressor.CompBlocked);
        // Stored names use backslashes; DiscForge shows and writes them with '/' and keeps them relative.
        Assert.Equal("winappdbg-winappdbg_v1.6/doc/Makefile", a.Members[21].Name);
    }

    [Fact]
    public void Test_all_passes_and_extract_all_writes_every_file()
    {
        using var a = AceArchive.Open(Fixture("winace-solid.ace"));
        Assert.True(a.TestAll().AllOk);
        var dir = TempDir();
        try
        {
            var r = a.ExtractAll(dir);
            Assert.True(r.AllOk);
            Assert.Equal(35, r.Ok);
            var readme = Path.Combine(dir, "winappdbg-winappdbg_v1.6", "README.md");
            Assert.True(File.Exists(readme));
            Assert.Equal("0c5e4421a340cd83e93e0a73cc13ad1742335ab3", Sha1(File.ReadAllBytes(readme)));
            Assert.Empty(Directory.GetFiles(dir, "*.dfpart", SearchOption.AllDirectories));

            // A second run leaves the existing files alone unless told to overwrite.
            var again = a.ExtractAll(dir);
            Assert.Equal(21, again.Ok);   // the folders
            Assert.Contains("already exists", again.Members.First(m => !m.Ok).Error);
            Assert.True(a.ExtractAll(dir, overwrite: true).AllOk);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Encrypted_members_need_the_right_password()
    {
        using var a = AceArchive.Open(Fixture("winace-solid-password.ace"));
        Assert.True(a.AnyEncrypted);
        var file = a.Members.First(m => m.IsEncrypted);
        Assert.Throws<AcePasswordException>(() => a.Extract(file, new MemoryStream(), null));
        Assert.Throws<AcePasswordException>(() => a.Extract(file, new MemoryStream(), "wrong"));
        var ok = a.TestAll(Password);
        Assert.True(ok.AllOk);
        var bad = a.TestAll("wrong");
        Assert.Equal(14, bad.Failed);
        Assert.All(bad.Members.Where(m => !m.Ok), m => Assert.Contains("password", m.Error));
    }

    [Fact]
    public void Multi_volume_set_opens_from_any_volume()
    {
        foreach (var name in new[] { "winace-multi.ace", "winace-multi.c00", "winace-multi.c01" })
        {
            using var a = AceArchive.Open(Fixture(name));
            Assert.True(a.IsMultiVolume);
            Assert.Equal(3, a.VolumePaths.Count);
            Assert.Empty(a.Warnings);
            Assert.Equal(35, a.Members.Count);
            Assert.Contains(a.Members, m => m.VolumeCount == 2);   // at least one file is split
            Assert.True(a.TestAll().AllOk);
        }
    }

    [Fact]
    public void Missing_last_volume_is_reported_not_fatal()
    {
        var dir = TempDir();
        try
        {
            File.Copy(Fixture("winace-multi.ace"), Path.Combine(dir, "set.ace"));
            File.Copy(Fixture("winace-multi.c00"), Path.Combine(dir, "set.c00"));
            using var a = AceArchive.Open(Path.Combine(dir, "set.ace"));
            Assert.Equal(2, a.VolumePaths.Count);
            Assert.NotNull(a.IncompleteMember);
            Assert.Contains("set.c01", a.Warnings[0]);
            var r = a.TestAll();
            Assert.Equal(1, r.Failed);
            Assert.Equal(r.Members.Count - 1, r.Ok);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Hostile_names_never_leave_the_output_folder()
    {
        using var a = AceArchive.Open(Fixture("hostile-names.ace"));
        var names = a.Members.Select(m => m.Name).ToArray();
        Assert.Equal(new[]
        {
            "escape-1.txt", "escape-2.txt", "C/Windows/escape-3.txt", "etc/escape-4.txt",
            "server/share/escape-5.txt", "escape-6.txt", "_CON.txt", "_aux", "dots/x.txt", "abc.txt",
            "file0010", "good/inner/file.txt",
        }, names);

        var parent = TempDir();
        try
        {
            var dest = Path.Combine(parent, "out");
            var r = a.ExtractAll(dest);
            Assert.True(r.AllOk);
            // Nothing was written beside the output folder…
            Assert.Equal(new[] { dest }, Directory.GetFileSystemEntries(parent));
            // …and every file is inside it.
            var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
            Assert.All(r.Members, m => Assert.StartsWith(root, m.OutputPath));
        }
        finally { Directory.Delete(parent, true); }
    }

    [Theory]
    [InlineData("a.exe\0b.txt", "a.exe")]
    [InlineData("\\etc\\foo/bar\\baz.txt", "etc/foo/bar/baz.txt")]
    [InlineData("a/b/../b/.//.././c/.//../d/file.txt", "a/d/file.txt")]
    [InlineData(".././.././../etc/passwd", "etc/passwd")]
    [InlineData("C:\\Windows\\foo.exe", "C/Windows/foo.exe")]
    [InlineData("\\\\?\\raw\\path", "raw/path")]
    [InlineData("c:\\c:\\CVE-2018-20250\\p.lnk", "c/c/CVE-2018-20250/p.lnk")]
    [InlineData("../etc/../", "")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("folder./name. ", "folder/name")]
    public void Names_are_cleaned(string stored, string expected)
    {
        Assert.Equal(expected, AceArchive.SanitizeRelativePath(stored));
    }

    [Fact]
    public void Contained_path_rejects_escapes()
    {
        var root = TempDir();
        try
        {
            Assert.Throws<AceFormatException>(() => AceArchive.ContainedPath(root, "../x"));
            Assert.Throws<AceFormatException>(() => AceArchive.ContainedPath(root, Path.GetFullPath("/tmp/x")));
            Assert.StartsWith(Path.GetFullPath(root), AceArchive.ContainedPath(root, "a/b.txt"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Dos_code_page_names_are_decoded()
    {
        using var a = AceArchive.Open(Fixture("cp437-cafe.ace"));
        Assert.Equal("café.txt", a.Members[0].Name);
    }

    [Fact]
    public void Blowfish_key_and_chain_match_the_reference()
    {
        // Values from acefile's own doctests.
        var key = AceBlowfish.DeriveKey("123456789"u8);
        Assert.Equal(new uint[] { 3071200156, 3325860325, 4058316933, 1308772094, 896611998 }, key);
        var data = Enumerable.Repeat((byte)'7', 16).ToArray();
        new AceBlowfish("123456789"u8).DecryptInPlace(data);
        Assert.Equal(System.Convert.FromHexString("095FD0617D1D68DD3E68E7564A2A5FEA"), data);
    }

    [Fact]
    public void Detection()
    {
        Assert.True(AceArchive.IsAceFile(Fixture("winace-solid.ace")));
        Assert.True(AceArchive.HasAceSignature(File.ReadAllBytes(Fixture("cp437-cafe.ace"))));
        Assert.False(AceArchive.IsAceFile(Fixture("manifest.txt")));
        Assert.Throws<AceFormatException>(() => AceArchive.Open(Fixture("manifest.txt")));
        var id = DiscForge.Core.Identify.FormatIdentifier.Identify(File.ReadAllBytes(Fixture("winace-solid.ace")));
        Assert.Equal("ACE", id.Name);
        Assert.Equal("archive", id.Category);
    }

    [Fact]
    public void Self_extractor_stub_in_front_is_skipped()
    {
        var dir = TempDir();
        try
        {
            var sfx = Path.Combine(dir, "setup.exe");
            var stub = new byte[3000];
            "MZ"u8.CopyTo(stub);
            File.WriteAllBytes(sfx, stub.Concat(File.ReadAllBytes(Fixture("winace-solid.ace"))).ToArray());
            using var a = AceArchive.Open(sfx);
            Assert.Equal(3000L, a.StartOffset);
            Assert.True(a.TestAll().AllOk);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Damaged_data_is_caught_by_the_crc()
    {
        var dir = TempDir();
        try
        {
            var bytes = File.ReadAllBytes(Fixture("winace-solid.ace"));
            bytes[bytes.Length - 200] ^= 0x55;   // inside the last member's packed data
            var path = Path.Combine(dir, "bad.ace");
            File.WriteAllBytes(path, bytes);
            using var a = AceArchive.Open(path);
            var r = a.TestAll();
            Assert.Equal(1, r.Failed);
            Assert.Equal("winappdbg-winappdbg_v1.6/README.md", r.Members.Single(m => !m.Ok).Member.Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The full 5.6 MB WinAce archive from acefile-testdata (265 files, LZ77 + DELTA + EXE),
    /// when the folder is available (set ACE_TESTDATA); otherwise nothing to do.</summary>
    [Fact]
    public void Full_reference_archive_when_available()
    {
        var root = Environment.GetEnvironmentVariable("ACE_TESTDATA");
        if (string.IsNullOrEmpty(root)) return;
        var path = Path.Combine(root, "blocked_unregistered", "winappdbg-winappdbg_v1.6_plain.ace");
        using var a = AceArchive.Open(path);
        var r = a.TestAll();
        Assert.True(r.AllOk);
        Assert.Equal(268, r.Ok);
    }
}
