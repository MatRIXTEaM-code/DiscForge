// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Gdi;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the GD-ROM browse chain: the ISO LBA rebase (the "ISO LBA Fix"),
/// the base-LBA ISO reader, and browsing a Dreamcast game track through both.
///
/// The oracle is a round trip. A tree is built into an ordinary zero-based ISO,
/// rebased to LBA 45000 the way a GD-ROM authors it, then read back with the
/// base LBA — and must come out identical, names, sizes and file bytes. That the
/// same tree survives base 0 and base 45000 is the proof the offset handling is
/// right; the base-0 path is guarded separately so the rebase cannot silently
/// have become a no-op.
/// </summary>
public class GdiBrowseTests
{
    private const long GdRomBase = 45000;

    private static byte[] BuildIso(params IsoBuilder.Node[] tree) =>
        IsoBuilder.BuildTree("DCGAME", tree, joliet: true).Image;

    private static IsoBuilder.Node[] SampleTree() => new[]
    {
        IsoBuilder.Node.File("1ST_READ.BIN", new byte[6000]),   // spans blocks
        IsoBuilder.Node.File("README.TXT", Encoding.ASCII.GetBytes("dreamcast game")),
        IsoBuilder.Node.Dir("DATA", new[]
        {
            IsoBuilder.Node.File("LEVEL1.DAT", Encoding.ASCII.GetBytes("level one")),
            IsoBuilder.Node.Dir("SUB", new[]
            {
                IsoBuilder.Node.File("deep.bin", new byte[] { 1, 2, 3, 4, 5, 6, 7 }),
            }),
        }),
    };

    // ---- the rebase + base-LBA reader --------------------------------------

    [Fact]
    public void A_rebased_iso_reads_back_to_the_same_tree()
    {
        var iso0 = BuildIso(SampleTree());
        var control = IsoReader.Read(new MemoryStream(iso0));

        var isoB = IsoRebaser.Rebase(iso0, GdRomBase);
        var rebased = IsoReader.Read(new MemoryStream(isoB), GdRomBase);

        Assert.Equal(control.Entries.Count, rebased.Entries.Count);
        Assert.Equal(
            control.Entries.Select(e => (e.Path, e.IsDirectory, e.Size)).OrderBy(x => x.Path),
            rebased.Entries.Select(e => (e.Path, e.IsDirectory, e.Size)).OrderBy(x => x.Path));
    }

    [Fact]
    public void The_rebase_actually_changes_the_bytes()
    {
        var iso0 = BuildIso(SampleTree());
        var isoB = IsoRebaser.Rebase(iso0, GdRomBase);

        Assert.Equal(iso0.Length, isoB.Length);          // physical layout unchanged
        Assert.NotEqual(iso0, isoB);                     // but addresses shifted
    }

    [Fact]
    public void Rebase_by_zero_is_a_faithful_copy()
    {
        var iso0 = BuildIso(SampleTree());
        Assert.Equal(iso0, IsoRebaser.Rebase(iso0, 0));
    }

    [Fact]
    public void A_file_extracts_correctly_through_the_base_lba()
    {
        var iso0 = BuildIso(
            IsoBuilder.Node.Dir("DATA", new[]
            {
                IsoBuilder.Node.File("MSG.TXT", Encoding.ASCII.GetBytes("the payload bytes")),
            }));
        var isoB = IsoRebaser.Rebase(iso0, GdRomBase);
        var dir = IsoReader.Read(new MemoryStream(isoB), GdRomBase);

        var entry = dir.Entries.Single(e => e.Path == "/DATA/MSG.TXT");
        using var o = new MemoryStream();
        IsoReader.ExtractFile(new MemoryStream(isoB), GdRomBase, entry, o);

        Assert.Equal("the payload bytes", Encoding.ASCII.GetString(o.ToArray()));
    }

    [Fact]
    public void A_multi_block_file_survives_the_rebase_byte_for_byte()
    {
        var content = new byte[6000];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i * 17 + 3);

        var iso0 = BuildIso(IsoBuilder.Node.File("BIG.BIN", content));
        var isoB = IsoRebaser.Rebase(iso0, GdRomBase);
        var dir = IsoReader.Read(new MemoryStream(isoB), GdRomBase);

        var entry = dir.Entries.Single(e => e.Path == "/BIG.BIN");
        using var o = new MemoryStream();
        IsoReader.ExtractFile(new MemoryStream(isoB), GdRomBase, entry, o);

        Assert.Equal(content, o.ToArray());
    }

    // ---- browsing a synthesized GD-ROM -------------------------------------

    // Wrap a rebased ISO as a raw 2352 Mode 1 track: each cooked 2048 sector
    // becomes a 2352-byte sector with 16 bytes of (dummy) sync+header prefix.
    private static byte[] AsRaw2352(byte[] cookedIso)
    {
        int sectors = cookedIso.Length / 2048;
        var raw = new byte[sectors * 2352];
        for (int i = 0; i < sectors; i++)
            Array.Copy(cookedIso, i * 2048, raw, i * 2352 + 16, 2048);
        return raw;
    }

    private static string WriteGdRom(string dir, byte[] gameTrackBytes, int sectorSize)
    {
        Directory.CreateDirectory(dir);
        // Two token low-density tracks and the game track at LBA 45000.
        File.WriteAllBytes(Path.Combine(dir, "track01.bin"), new byte[2352 * 4]);
        File.WriteAllBytes(Path.Combine(dir, "track02.raw"), new byte[2352 * 4]);
        File.WriteAllBytes(Path.Combine(dir, "track03.bin"), gameTrackBytes);
        File.WriteAllText(Path.Combine(dir, "game.gdi"),
            "3\n" +
            "1 0 4 2352 track01.bin 0\n" +
            "2 600 0 2352 track02.raw 0\n" +
            $"3 45000 4 {sectorSize} track03.bin 0\n");
        return Path.Combine(dir, "game.gdi");
    }

    [Fact]
    public void A_raw_2352_game_track_browses_its_filesystem()
    {
        var isoB = IsoRebaser.Rebase(BuildIso(SampleTree()), GdRomBase);
        var raw = AsRaw2352(isoB);

        string dir = Path.Combine(Path.GetTempPath(), "gdibrowse_" + Guid.NewGuid().ToString("N"));
        var gdi = WriteGdRom(dir, raw, sectorSize: 2352);
        try
        {
            var listing = GdiBrowser.BrowseFile(gdi);

            Assert.Equal("DCGAME", listing.VolumeId);
            Assert.Contains(listing.Entries, e => e.Path == "/1ST_READ.BIN");
            Assert.Contains(listing.Entries, e => e.Path == "/DATA/LEVEL1.DAT");
            Assert.Contains(listing.Entries, e => e.Path == "/DATA/SUB/deep.bin");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_game_file_extracts_through_the_browse_path()
    {
        var iso0 = BuildIso(
            IsoBuilder.Node.File("GREETING.TXT", Encoding.ASCII.GetBytes("hello from the GD area")));
        var raw = AsRaw2352(IsoRebaser.Rebase(iso0, GdRomBase));

        string dir = Path.Combine(Path.GetTempPath(), "gdiextract_" + Guid.NewGuid().ToString("N"));
        var gdi = WriteGdRom(dir, raw, sectorSize: 2352);
        try
        {
            var disc = GdiParser.ParseFile(gdi);
            var listing = GdiBrowser.Browse(disc, dir);
            var entry = listing.Entries.Single(e => e.Path == "/GREETING.TXT");

            using var o = new MemoryStream();
            GdiBrowser.ExtractFile(disc, dir, entry, o);

            Assert.Equal("hello from the GD area", Encoding.ASCII.GetString(o.ToArray()));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_cooked_2048_game_track_browses_too()
    {
        var isoB = IsoRebaser.Rebase(BuildIso(SampleTree()), GdRomBase);

        string dir = Path.Combine(Path.GetTempPath(), "gdicooked_" + Guid.NewGuid().ToString("N"));
        var gdi = WriteGdRom(dir, isoB, sectorSize: 2048);
        try
        {
            var listing = GdiBrowser.BrowseFile(gdi);
            Assert.Contains(listing.Entries, e => e.Path == "/README.TXT");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Browsing_an_image_with_no_game_track_is_refused()
    {
        var disc = GdiParser.Parse("2\n1 0 4 2352 a.bin 0\n2 600 0 2352 b.raw 0\n");
        var ex = Assert.Throws<GdiFormatException>(() => GdiBrowser.Browse(disc, "."));
        Assert.Contains("no high-density data track", ex.Message);
    }
}
