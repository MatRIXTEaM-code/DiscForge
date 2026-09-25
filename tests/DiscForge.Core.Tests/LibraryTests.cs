// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;
using DiscForge.Core.Dat;
using DiscForge.Core.Library;
using DiscForge.Core.Patch;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The collection/library manager, validated against a synthetic folder and a hand-built
/// Logiqx DAT whose entries are the actual hashes of the test files: a correctly-named
/// good file verifies, a good file under the wrong name is flagged mis-named with the
/// canonical name, a byte-identical second copy is a duplicate, an unrelated file is
/// unknown, a DAT entry with no matching file is reported missing, and the rename plan
/// applied on disk moves the mis-named file to its canonical name.
/// </summary>
public class LibraryTests
{
    private static (string Crc, string Md5, string Sha1) Hashes(byte[] data) =>
    (
        BpsPatch.Crc32(data).ToString("x8"),
        System.Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant(),
        System.Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant()
    );

    private static string Rom(string game, string name, byte[] data)
    {
        var (crc, md5, sha1) = Hashes(data);
        return $"<game name=\"{game}\"><rom name=\"{name}\" size=\"{data.Length}\" " +
               $"crc=\"{crc}\" md5=\"{md5}\" sha1=\"{sha1}\"/></game>";
    }

    private static byte[] Content(int seed, int len = 4096)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void Scan_verifies_flags_misnamed_duplicate_unknown_and_missing()
    {
        var dir = Directory.CreateTempSubdirectory("dforge_lib_").FullName;
        try
        {
            var a = Content(1);
            var b = Content(2);
            var c = Content(3);           // catalogued but not on disk (missing)
            var junk = Content(99);       // on disk but not catalogued (unknown)

            File.WriteAllBytes(Path.Combine(dir, "game_a.bin"), a);       // correctly named
            File.WriteAllBytes(Path.Combine(dir, "game_a_copy.bin"), a);  // duplicate of A
            File.WriteAllBytes(Path.Combine(dir, "wrongname.bin"), b);    // B under the wrong name
            File.WriteAllBytes(Path.Combine(dir, "random.bin"), junk);    // unknown

            string xml = "<datafile><header><name>Test Set</name></header>" +
                         Rom("Game A", "game_a.bin", a) +
                         Rom("Game B", "game_b.bin", b) +
                         Rom("Game C", "game_c.bin", c) +
                         "</datafile>";
            var dat = DatFile.ParseText(xml);

            var report = LibraryScanner.Scan(dir, dat);

            Assert.Equal("Test Set", report.DatName);
            Assert.Equal(4, report.Total);

            var byName = report.Entries.ToDictionary(e => e.FileName);
            Assert.Equal(LibraryStatus.Verified, byName["game_a.bin"].Status);
            Assert.Equal(LibraryStatus.Duplicate, byName["game_a_copy.bin"].Status);
            Assert.Equal(LibraryStatus.Misnamed, byName["wrongname.bin"].Status);
            Assert.Equal("game_b.bin", byName["wrongname.bin"].SuggestedName);
            Assert.Equal(LibraryStatus.Unknown, byName["random.bin"].Status);

            // Game C is the only catalogued entry with no matching file.
            Assert.Single(report.Missing);
            Assert.Equal("game_c.bin", report.Missing[0].Name);

            // Aggregates.
            Assert.Equal(2, report.Verified);   // game_a (verified) + wrongname (misnamed)
            Assert.Equal(1, report.Misnamed);
            Assert.Equal(1, report.Duplicates);
            Assert.Equal(1, report.Unknown);

            // The rename plan targets exactly the mis-named file.
            var plan = report.RenamePlan();
            Assert.Single(plan);
            Assert.EndsWith("wrongname.bin", plan[0].From);
            Assert.EndsWith("game_b.bin", plan[0].To);

            // Applying it moves the file to its canonical name.
            Assert.Equal(1, LibraryScanner.ApplyRenames(plan));
            Assert.True(File.Exists(Path.Combine(dir, "game_b.bin")));
            Assert.False(File.Exists(Path.Combine(dir, "wrongname.bin")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Without_a_dat_files_are_identified_and_hashed_but_unchecked()
    {
        var dir = Directory.CreateTempSubdirectory("dforge_lib2_").FullName;
        try
        {
            var data = Content(7);
            File.WriteAllBytes(Path.Combine(dir, "thing.bin"), data);
            var (crc, _, sha1) = Hashes(data);

            var report = LibraryScanner.Scan(dir, dat: null);
            var e = Assert.Single(report.Entries);
            Assert.Equal(LibraryStatus.Unchecked, e.Status);
            Assert.Equal(crc, e.Crc32Hex);
            Assert.Equal(sha1, e.Sha1);
            Assert.Empty(report.Missing);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Apply_renames_does_not_clobber_a_different_existing_file()
    {
        var dir = Directory.CreateTempSubdirectory("dforge_lib3_").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "from.bin"), Content(1));
            File.WriteAllBytes(Path.Combine(dir, "to.bin"), Content(2));   // occupied by a different file
            var plan = new[] { new RenamePlanItem(Path.Combine(dir, "from.bin"), Path.Combine(dir, "to.bin")) };

            Assert.Equal(0, LibraryScanner.ApplyRenames(plan));   // skipped, not overwritten
            Assert.True(File.Exists(Path.Combine(dir, "from.bin")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_missing_folder_is_declined()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            LibraryScanner.Scan(Path.Combine(Path.GetTempPath(), "dforge_no_such_dir_" + Guid.NewGuid())));
    }

    [Fact]
    public void Nested_folders_are_scanned_recursively()
    {
        var dir = Directory.CreateTempSubdirectory("dforge_lib4_").FullName;
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(dir, "sub", "deeper")).FullName;
            File.WriteAllBytes(Path.Combine(dir, "top.bin"), Content(1));
            File.WriteAllBytes(Path.Combine(sub, "nested.bin"), Content(2));

            var report = LibraryScanner.Scan(dir);
            Assert.Equal(2, report.Total);
            Assert.Contains(report.Entries, e => e.FileName == "nested.bin");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
