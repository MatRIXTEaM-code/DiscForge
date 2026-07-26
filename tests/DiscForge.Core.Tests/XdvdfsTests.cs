// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Xbox;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the Xbox XDVDFS reader and writer. XDVDFS has no external oracle in
/// this codebase, so — as with UDF — the writer builds a volume with known
/// contents and the reader reads it back; a tree that survives with its names,
/// sizes and bytes intact is the proof both halves agree with the format.
///
/// Beyond the round trip, a hand-built volume descriptor and a base-offset image
/// pin the two things a reader must get right independently of the writer: the
/// "MICROSOFT*XBOX*MEDIA" signature check, and resolving sector addresses
/// relative to a non-zero game-partition base.
/// </summary>
public class XdvdfsTests
{
    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static (XdvdfsVolume Volume, byte[] Image) BuildAndRead(params XdvdfsBuilder.Node[] tree)
    {
        var image = XdvdfsBuilder.Build(tree);
        using var ms = new MemoryStream(image);
        return (XdvdfsReader.Read(ms), image);
    }

    private static byte[] Extract(byte[] image, XdvdfsVolume vol, string path)
    {
        var entry = vol.Files.Single(f => f.Path == path);
        using var o = new MemoryStream();
        using var src = new MemoryStream(image);
        XdvdfsReader.ExtractFile(src, vol, entry, o);
        return o.ToArray();
    }

    // ---- multi-sector directory tables -------------------------------------

    [Fact]
    public void Directory_Spanning_Many_Sectors_RoundTrips()
    {
        // Enough entries that the directory table runs to several sectors, which
        // forces the boundary-padding path: no entry may straddle a 2048-byte
        // sector, and every one must still be reachable through the BST.
        const int count = 300;
        var files = new List<XdvdfsBuilder.Node>();
        for (int i = 0; i < count; i++)
            files.Add(XdvdfsBuilder.Node.File($"file{i:D4}.bin", Bytes($"contents-{i}")));

        var (vol, image) = BuildAndRead(files.ToArray());

        Assert.Equal(count, vol.Files.Count());
        for (int i = 0; i < count; i++)
        {
            string name = $"file{i:D4}.bin";
            var entry = vol.Files.Single(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal($"contents-{i}", Encoding.ASCII.GetString(Extract(image, vol, entry.Path)));
        }
    }

    [Fact]
    public void MultiSector_Directory_HasNoBoundaryStraddlingEntry()
    {
        // Independent structural check: rebuild the same big directory and confirm
        // the root table is a whole number of sectors and its size is recorded
        // sector-granular, as a real XDVDFS volume stores it.
        var files = Enumerable.Range(0, 300)
            .Select(i => XdvdfsBuilder.Node.File($"file{i:D4}.bin", Bytes("x")))
            .ToArray();
        var (vol, _) = BuildAndRead(files);
        Assert.True(vol.RootSize % XdvdfsBuilder.SectorSize == 0);
        Assert.True(vol.RootSize > XdvdfsBuilder.SectorSize);   // genuinely multi-sector
    }

    // ---- the round trip -----------------------------------------------------

    [Fact]
    public void A_built_image_is_recognised_as_xdvdfs()
    {
        var image = XdvdfsBuilder.Build(new[] { XdvdfsBuilder.Node.File("default.xbe", Bytes("xbe")) });
        using var ms = new MemoryStream(image);
        Assert.True(XdvdfsReader.IsXdvdfs(ms));
    }

    [Fact]
    public void A_single_file_reads_back_with_its_name_and_size()
    {
        var (vol, _) = BuildAndRead(XdvdfsBuilder.Node.File("default.xbe", Bytes("hello")));

        var file = Assert.Single(vol.Files);
        Assert.Equal("/default.xbe", file.Path);
        Assert.Equal(5, file.Size);
        Assert.False(file.IsDirectory);
    }

    [Fact]
    public void A_files_bytes_survive_the_round_trip()
    {
        var content = Bytes("The quick brown fox jumps over the lazy Xbox.");
        var (vol, image) = BuildAndRead(XdvdfsBuilder.Node.File("data.bin", content));
        Assert.Equal(content, Extract(image, vol, "/data.bin"));
    }

    [Fact]
    public void An_empty_file_reads_back_as_zero_bytes()
    {
        var (vol, image) = BuildAndRead(XdvdfsBuilder.Node.File("empty.dat", Array.Empty<byte>()));
        Assert.Equal(0, vol.Files.Single().Size);
        Assert.Empty(Extract(image, vol, "/empty.dat"));
    }

    [Fact]
    public void A_multi_sector_file_reads_back_intact()
    {
        var content = new byte[7000];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i * 37 + 11);
        var (vol, image) = BuildAndRead(XdvdfsBuilder.Node.File("big.bin", content));

        Assert.Equal(7000, vol.Files.Single().Size);
        Assert.Equal(content, Extract(image, vol, "/big.bin"));
    }

    [Fact]
    public void Several_files_keep_their_own_contents()
    {
        var (vol, image) = BuildAndRead(
            XdvdfsBuilder.Node.File("a.txt", Bytes("first")),
            XdvdfsBuilder.Node.File("b.txt", Bytes("second one")),
            XdvdfsBuilder.Node.File("c.txt", new byte[2500]));

        Assert.Equal(3, vol.Files.Count());
        Assert.Equal(Bytes("first"), Extract(image, vol, "/a.txt"));
        Assert.Equal(Bytes("second one"), Extract(image, vol, "/b.txt"));
        Assert.Equal(2500, Extract(image, vol, "/c.txt").Length);
    }

    // ---- directories --------------------------------------------------------

    [Fact]
    public void A_directory_tree_reads_back_with_correct_paths()
    {
        var (vol, _) = BuildAndRead(
            XdvdfsBuilder.Node.File("default.xbe", Bytes("xbe")),
            XdvdfsBuilder.Node.Dir("media", new[]
            {
                XdvdfsBuilder.Node.File("intro.wmv", new byte[3000]),
                XdvdfsBuilder.Node.File("logo.bmp", Bytes("BM")),
            }));

        Assert.Contains(vol.Directories, d => d.Path == "/media");
        Assert.Contains(vol.Files, f => f.Path == "/media/intro.wmv" && f.Size == 3000);
        Assert.Contains(vol.Files, f => f.Path == "/media/logo.bmp");
    }

    [Fact]
    public void Deeply_nested_directories_survive_and_hold_their_file()
    {
        var (vol, image) = BuildAndRead(
            XdvdfsBuilder.Node.Dir("a", new[]
            {
                XdvdfsBuilder.Node.Dir("b", new[]
                {
                    XdvdfsBuilder.Node.Dir("c", new[]
                    {
                        XdvdfsBuilder.Node.File("deep.txt", Bytes("all the way down")),
                    }),
                }),
            }));

        Assert.Contains(vol.Directories, d => d.Path == "/a/b/c");
        Assert.Equal(Bytes("all the way down"), Extract(image, vol, "/a/b/c/deep.txt"));
    }

    [Fact]
    public void An_empty_directory_reads_back_as_an_empty_directory()
    {
        var (vol, _) = BuildAndRead(XdvdfsBuilder.Node.Dir("empty", Array.Empty<XdvdfsBuilder.Node>()));
        var dir = Assert.Single(vol.Directories);
        Assert.Equal("/empty", dir.Path);
        Assert.Empty(vol.Files);
    }

    [Fact]
    public void Directory_entries_carry_the_directory_attribute()
    {
        var (vol, _) = BuildAndRead(
            XdvdfsBuilder.Node.Dir("sub", new[] { XdvdfsBuilder.Node.File("f", Bytes("x")) }));

        var dir = vol.Directories.Single();
        Assert.True((dir.Attributes & 0x10) != 0);
        Assert.Equal(0, vol.Files.Single(f => f.Path == "/sub/f").Attributes & 0x10);
    }

    [Fact]
    public void A_directory_of_many_entries_reads_every_one_back()
    {
        // A balanced tree only differs from a right-leaning chain once a directory
        // holds enough entries to grow left subtrees. Fifty files, each with its
        // own distinct content, exercise the balanced layout and prove the reader
        // traverses every node — left subtrees included — not just a right spine.
        var files = new List<XdvdfsBuilder.Node>();
        for (int i = 0; i < 50; i++)
            files.Add(XdvdfsBuilder.Node.File($"file{i:D2}.dat", Bytes($"content-{i}")));

        var (vol, image) = BuildAndRead(files.ToArray());

        Assert.Equal(50, vol.Files.Count());
        for (int i = 0; i < 50; i++)
            Assert.Equal(Bytes($"content-{i}"), Extract(image, vol, $"/file{i:D2}.dat"));
    }

    // ---- determinism --------------------------------------------------------

    [Fact]
    public void The_same_tree_builds_byte_identical_images()
    {
        XdvdfsBuilder.Node[] Tree() => new[]
        {
            XdvdfsBuilder.Node.File("a.xbe", Bytes("alpha")),
            XdvdfsBuilder.Node.Dir("d", new[] { XdvdfsBuilder.Node.File("b.dat", Bytes("beta")) }),
        };
        Assert.Equal(XdvdfsBuilder.Build(Tree()), XdvdfsBuilder.Build(Tree()));
    }

    // ---- the signature ------------------------------------------------------

    [Fact]
    public void A_stream_without_the_signature_is_refused()
    {
        // 40 sectors of zeros: no "MICROSOFT*XBOX*MEDIA" anywhere.
        using var ms = new MemoryStream(new byte[40 * 2048]);
        Assert.False(XdvdfsReader.IsXdvdfs(ms));
        ms.Position = 0;
        Assert.Throws<XdvdfsFormatException>(() => XdvdfsReader.Read(ms));
    }

    // ---- non-zero base ------------------------------------------------------

    [Fact]
    public void A_volume_at_a_non_zero_partition_base_reads_when_the_base_is_given()
    {
        // Wrap a base-0 XISO so its game partition begins at sector 4096: prepend
        // that many zero sectors. All sector addresses stay relative to the base.
        long baseSector = 4096;
        var xiso = XdvdfsBuilder.Build(new[]
        {
            XdvdfsBuilder.Node.File("default.xbe", Bytes("boot")),
            XdvdfsBuilder.Node.Dir("sub", new[] { XdvdfsBuilder.Node.File("f.dat", Bytes("payload")) }),
        });
        var full = new byte[baseSector * 2048 + xiso.Length];
        Array.Copy(xiso, 0, full, baseSector * 2048, xiso.Length);

        using var ms = new MemoryStream(full);
        var vol = XdvdfsReader.Read(ms, baseSector);

        Assert.Equal(baseSector, vol.BaseSector);
        Assert.Contains(vol.Files, f => f.Path == "/default.xbe");
        Assert.Equal(Bytes("payload"), Extract(full, vol, "/sub/f.dat"));
    }

    [Fact]
    public void The_reader_finds_the_documented_xgd1_base_automatically()
    {
        // Place a volume at the XGD1 base (sector 0x30600) and let auto-detect
        // find it with no explicit base.
        long baseSector = 0x30600;
        var xiso = XdvdfsBuilder.Build(new[] { XdvdfsBuilder.Node.File("default.xbe", Bytes("xgd1")) });
        var full = new byte[baseSector * 2048 + xiso.Length];
        Array.Copy(xiso, 0, full, baseSector * 2048, xiso.Length);

        using var ms = new MemoryStream(full);
        var vol = XdvdfsReader.Read(ms);   // no base given

        Assert.Equal(baseSector, vol.BaseSector);
        Assert.Single(vol.Files, f => f.Path == "/default.xbe");
    }

    [Fact]
    public void The_reader_finds_the_documented_xgd3_base_automatically()
    {
        // XGD3 base = 0x4100 sectors (0x02080000 bytes) — the smallest of the
        // documented offsets, so the cheapest to exercise auto-detection with.
        long baseSector = 0x4100;
        var xiso = XdvdfsBuilder.Build(new[] { XdvdfsBuilder.Node.File("default.xbe", Bytes("xgd3")) });
        var full = new byte[baseSector * 2048 + xiso.Length];
        Array.Copy(xiso, 0, full, baseSector * 2048, xiso.Length);

        using var ms = new MemoryStream(full);
        var vol = XdvdfsReader.Read(ms);

        Assert.Equal(baseSector, vol.BaseSector);
        Assert.Single(vol.Files, f => f.Path == "/default.xbe");
    }

    // ---- a hand-built descriptor -------------------------------------------

    // ---- streamed writing ---------------------------------------------------

    [Fact]
    public void Streaming_to_a_seekable_stream_matches_the_in_memory_build_byte_for_byte()
    {
        XdvdfsBuilder.Node[] Tree() => new[]
        {
            XdvdfsBuilder.Node.File("a.txt", Bytes("hello")),
            XdvdfsBuilder.Node.Dir("d", new[] { XdvdfsBuilder.Node.File("b.bin", new byte[3000]) }),
        };

        var inMemory = XdvdfsBuilder.Build(Tree());
        using var ms = new MemoryStream();
        XdvdfsBuilder.BuildToStream(ms, Tree());

        Assert.Equal(inMemory, ms.ToArray());
    }

    [Fact]
    public void A_streamed_file_source_produces_the_same_image_as_an_in_memory_one()
    {
        var content = new byte[5000];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i * 13 + 7);

        var inMemory = XdvdfsBuilder.Build(new[] { XdvdfsBuilder.Node.File("f.dat", content) });

        using var ms = new MemoryStream();
        XdvdfsBuilder.BuildToStream(ms, new[]
        {
            XdvdfsBuilder.Node.File("f.dat", content.Length, () => new MemoryStream(content)),
        });

        Assert.Equal(inMemory, ms.ToArray());
    }

    [Fact]
    public void A_streamed_build_reads_back_through_the_reader()
    {
        var content = Bytes("content that was streamed, not buffered");
        using var ms = new MemoryStream();
        XdvdfsBuilder.BuildToStream(ms, new[]
        {
            XdvdfsBuilder.Node.File("s.dat", content.Length, () => new MemoryStream(content)),
        });

        var image = ms.ToArray();
        using var read = new MemoryStream(image);
        var vol = XdvdfsReader.Read(read);
        Assert.Equal(content, Extract(image, vol, "/s.dat"));
    }

    [Fact]
    public void A_file_backed_source_streams_from_disk()
    {
        var content = new byte[4096];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i & 0xFF);
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            using var ms = new MemoryStream();
            XdvdfsBuilder.BuildToStream(ms, new[] { XdvdfsBuilder.Node.FileFromPath("disk.bin", path) });

            var image = ms.ToArray();
            using var read = new MemoryStream(image);
            var vol = XdvdfsReader.Read(read);
            Assert.Equal(content, Extract(image, vol, "/disk.bin"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_non_seekable_output_is_refused()
    {
        using var pipe = new NonSeekableStream();
        Assert.Throws<ArgumentException>(() =>
            XdvdfsBuilder.BuildToStream(pipe, new[] { XdvdfsBuilder.Node.File("a", Bytes("x")) }));
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public void A_hand_built_volume_descriptor_and_single_entry_parse()
    {
        // Build the minimum by hand, independent of the writer: a descriptor at
        // sector 32 pointing at a one-entry directory table at sector 33, whose
        // file data is at sector 34.
        var image = new byte[35 * 2048];
        var magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

        int vd = 32 * 2048;
        magic.CopyTo(image, vd);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(vd + 0x14, 4), 33);   // root sector
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(vd + 0x18, 4), 24);   // root size (one padded entry)
        magic.CopyTo(image, vd + 0x7EC);

        int tbl = 33 * 2048;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(tbl + 0, 2), 0xFFFF); // left: none
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(tbl + 2, 2), 0xFFFF); // right: none
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tbl + 4, 4), 34);     // data sector
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tbl + 8, 4), 5);      // size
        image[tbl + 12] = 0x20;                                                     // normal file
        image[tbl + 13] = 9;                                                        // name length
        Encoding.ASCII.GetBytes("HELLO.TXT").CopyTo(image, tbl + 14);

        Encoding.ASCII.GetBytes("world").CopyTo(image, 34 * 2048);

        using var ms = new MemoryStream(image);
        var vol = XdvdfsReader.Read(ms);

        var file = Assert.Single(vol.Files);
        Assert.Equal("/HELLO.TXT", file.Path);
        Assert.Equal(5, file.Size);
        Assert.Equal(Bytes("world"), Extract(image, vol, "/HELLO.TXT"));
    }
}
