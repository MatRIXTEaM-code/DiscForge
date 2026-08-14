using System;
using System.Collections.Generic;
using System.IO;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

public class PreservationPackageTests
{
    private static string TempDir()
    {
        // Deterministic-ish unique dir without Random/Guid dependence in the harness.
        string dir = Path.Combine(Path.GetTempPath(), "dfpres_" + System.Threading.Interlocked.Increment(ref _counter));
        Directory.CreateDirectory(dir);
        return dir;
    }
    private static int _counter;

    private static void Write(string dir, string name, byte[] data) => File.WriteAllBytes(Path.Combine(dir, name), data);

    [Fact]
    public void Build_records_every_file_and_a_valid_digest()
    {
        string dir = TempDir();
        try
        {
            Write(dir, "game.bin", new byte[] { 1, 2, 3, 4, 5 });
            Write(dir, "game.cue", System.Text.Encoding.ASCII.GetBytes("FILE \"game.bin\" BINARY"));

            var m = PreservationPackage.Build(new[] { Path.Combine(dir, "game.bin"), Path.Combine(dir, "game.cue") },
                                              "DiscForge test", title: "Test Game", platform: "PlayStation");
            Assert.Equal(2, m.Entries.Count);
            Assert.Equal("Test Game", m.Title);
            Assert.Equal("PlayStation", m.Platform);
            Assert.False(string.IsNullOrEmpty(m.Digest));
            Assert.True(PreservationPackage.DigestValid(m));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Manifest_survives_a_json_round_trip_with_its_digest_intact()
    {
        string dir = TempDir();
        try
        {
            Write(dir, "a.bin", new byte[] { 9, 8, 7 });
            var m = PreservationPackage.Build(new[] { Path.Combine(dir, "a.bin") }, "DiscForge test");
            var back = PreservationPackage.FromJson(PreservationPackage.ToJson(m));
            Assert.Equal(m.Digest, back.Digest);
            Assert.True(PreservationPackage.DigestValid(back));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Verify_passes_for_an_untouched_set()
    {
        string dir = TempDir();
        try
        {
            Write(dir, "a.bin", new byte[] { 1, 1, 2, 3, 5, 8 });
            Write(dir, "b.bin", new byte[] { 42 });
            var m = PreservationPackage.Build(new[] { Path.Combine(dir, "a.bin"), Path.Combine(dir, "b.bin") }, "DiscForge test");
            var r = PreservationPackage.Verify(m, dir);
            Assert.True(r.AllGood);
            Assert.Equal(2, r.Ok);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Verify_fails_when_a_file_is_tampered()
    {
        string dir = TempDir();
        try
        {
            Write(dir, "a.bin", new byte[] { 1, 2, 3, 4 });
            var m = PreservationPackage.Build(new[] { Path.Combine(dir, "a.bin") }, "DiscForge test");
            Write(dir, "a.bin", new byte[] { 1, 2, 3, 9 });    // change one byte
            var r = PreservationPackage.Verify(m, dir);
            Assert.False(r.AllGood);
            Assert.Equal(1, r.Failed);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Verify_reports_a_missing_file()
    {
        string dir = TempDir();
        try
        {
            Write(dir, "a.bin", new byte[] { 1, 2, 3, 4 });
            var m = PreservationPackage.Build(new[] { Path.Combine(dir, "a.bin") }, "DiscForge test");
            File.Delete(Path.Combine(dir, "a.bin"));
            var r = PreservationPackage.Verify(m, dir);
            Assert.False(r.AllGood);
            Assert.Equal(1, r.Missing);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_tampered_manifest_is_detected()
    {
        string dir = TempDir();
        try
        {
            Write(dir, "a.bin", new byte[] { 1, 2, 3, 4 });
            var m = PreservationPackage.Build(new[] { Path.Combine(dir, "a.bin") }, "DiscForge test");
            m.Entries[0].Sha256 = new string('0', 64);        // forge a hash without re-digesting
            Assert.False(PreservationPackage.DigestValid(m));

            var r = PreservationPackage.Verify(m, dir);
            Assert.False(r.ManifestIntact);
            Assert.False(r.AllGood);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_digest_changes_when_provenance_changes()
    {
        string dir = TempDir();
        try
        {
            Write(dir, "a.bin", new byte[] { 5, 5, 5 });
            var m1 = PreservationPackage.Build(new[] { Path.Combine(dir, "a.bin") }, "DiscForge test", title: "One");
            var m2 = PreservationPackage.Build(new[] { Path.Combine(dir, "a.bin") }, "DiscForge test", title: "Two");
            Assert.NotEqual(m1.Digest, m2.Digest);
        }
        finally { Directory.Delete(dir, true); }
    }
}
