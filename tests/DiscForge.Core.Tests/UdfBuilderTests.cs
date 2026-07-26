// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Udf;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the UDF 1.02 writer. UDF has no isoinfo-style external oracle, so —
/// exactly as the reader's own reference does — the writer is validated by
/// building a volume with known contents and reading it back with
/// <see cref="UdfReader"/>. A tree that survives write→read with its names,
/// sizes and bytes intact is the honest proof the descriptors are right.
///
/// The interesting cases are the ones where the byte layout is easy to get
/// wrong: empty files, files that span several logical blocks, files whose size
/// is not a whole number of blocks, and directory nesting deep enough to force
/// separate directory-data extents.
/// </summary>
public class UdfBuilderTests
{
    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static (UdfVolume Volume, MemoryStream Image) BuildAndRead(
        string volumeId, params UdfBuilder.Node[] tree)
    {
        var image = UdfBuilder.Build(volumeId, tree);
        var ms = new MemoryStream(image);
        return (UdfReader.Read(ms), ms);
    }

    private static byte[] Extract(MemoryStream image, UdfVolume vol, string path)
    {
        var entry = vol.Files.Single(f => f.Path == path);
        using var o = new MemoryStream();
        image.Position = 0;
        UdfReader.ExtractFile(image, vol, entry, o);
        return o.ToArray();
    }

    // ---- the shape of a written volume -------------------------------------

    [Fact]
    public void A_built_image_is_recognised_as_udf()
    {
        var image = UdfBuilder.Build("DISC", new[] { UdfBuilder.Node.File("A.TXT", Bytes("hi")) });
        using var ms = new MemoryStream(image);
        Assert.True(UdfReader.IsUdf(ms));
    }

    [Fact]
    public void The_volume_identifier_round_trips()
    {
        var (vol, _) = BuildAndRead("MY_DVD_VOLUME", UdfBuilder.Node.File("X", Bytes("x")));
        Assert.Equal("MY_DVD_VOLUME", vol.VolumeId);
    }

    [Fact]
    public void A_single_file_reads_back_with_its_name_and_size()
    {
        var (vol, _) = BuildAndRead("DISC", UdfBuilder.Node.File("README.TXT", Bytes("hello")));

        var file = Assert.Single(vol.Files);
        Assert.Equal("/README.TXT", file.Path);
        Assert.Equal("README.TXT", file.Name);
        Assert.Equal(5, file.Size);
        Assert.False(file.IsDirectory);
    }

    [Fact]
    public void A_files_bytes_survive_the_round_trip()
    {
        var content = Bytes("The quick brown fox jumps over the lazy dog.");
        var (vol, image) = BuildAndRead("DISC", UdfBuilder.Node.File("FOX.TXT", content));

        Assert.Equal(content, Extract(image, vol, "/FOX.TXT"));
    }

    [Fact]
    public void An_empty_file_reads_back_as_zero_bytes()
    {
        var (vol, image) = BuildAndRead("DISC", UdfBuilder.Node.File("EMPTY.DAT", Array.Empty<byte>()));

        var file = Assert.Single(vol.Files);
        Assert.Equal(0, file.Size);
        Assert.Empty(Extract(image, vol, "/EMPTY.DAT"));
    }

    // ---- multi-block and non-aligned files ---------------------------------

    [Fact]
    public void A_file_spanning_several_blocks_reads_back_intact()
    {
        // 5000 bytes = three 2048-byte blocks, the last partly filled.
        var content = new byte[5000];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i * 31 + 7);

        var (vol, image) = BuildAndRead("DISC", UdfBuilder.Node.File("BIG.BIN", content));

        Assert.Equal(5000, vol.Files.Single().Size);
        Assert.Equal(content, Extract(image, vol, "/BIG.BIN"));
    }

    [Fact]
    public void A_file_exactly_one_block_long_reads_back_intact()
    {
        var content = new byte[2048];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)i;

        var (vol, image) = BuildAndRead("DISC", UdfBuilder.Node.File("BLOCK.BIN", content));

        Assert.Equal(2048, vol.Files.Single().Size);
        Assert.Equal(content, Extract(image, vol, "/BLOCK.BIN"));
    }

    [Fact]
    public void Several_files_keep_their_own_distinct_contents()
    {
        var (vol, image) = BuildAndRead("DISC",
            UdfBuilder.Node.File("ONE.TXT", Bytes("first")),
            UdfBuilder.Node.File("TWO.TXT", Bytes("second file")),
            UdfBuilder.Node.File("THREE.TXT", new byte[3000]));

        Assert.Equal(3, vol.Files.Count());
        Assert.Equal(Bytes("first"), Extract(image, vol, "/ONE.TXT"));
        Assert.Equal(Bytes("second file"), Extract(image, vol, "/TWO.TXT"));
        Assert.Equal(3000, Extract(image, vol, "/THREE.TXT").Length);
    }

    // ---- directories --------------------------------------------------------

    [Fact]
    public void A_directory_tree_reads_back_with_correct_paths()
    {
        var (vol, _) = BuildAndRead("DVD",
            UdfBuilder.Node.Dir("VIDEO_TS", new[]
            {
                UdfBuilder.Node.File("VIDEO_TS.IFO", Bytes("ifo")),
                UdfBuilder.Node.File("VTS_01_1.VOB", new byte[4096]),
            }));

        Assert.Contains(vol.Directories, d => d.Path == "/VIDEO_TS");
        Assert.Contains(vol.Files, f => f.Path == "/VIDEO_TS/VIDEO_TS.IFO");
        Assert.Contains(vol.Files, f => f.Path == "/VIDEO_TS/VTS_01_1.VOB" && f.Size == 4096);
    }

    [Fact]
    public void Deeply_nested_directories_survive_and_hold_their_file()
    {
        var (vol, image) = BuildAndRead("DISC",
            UdfBuilder.Node.Dir("A", new[]
            {
                UdfBuilder.Node.Dir("B", new[]
                {
                    UdfBuilder.Node.Dir("C", new[]
                    {
                        UdfBuilder.Node.File("deep.txt", Bytes("all the way down")),
                    }),
                }),
            }));

        Assert.Contains(vol.Directories, d => d.Path == "/A/B/C");
        Assert.Equal(Bytes("all the way down"), Extract(image, vol, "/A/B/C/deep.txt"));
    }

    [Fact]
    public void An_empty_directory_reads_back_as_an_empty_directory()
    {
        var (vol, _) = BuildAndRead("DISC",
            UdfBuilder.Node.Dir("EMPTY", Array.Empty<UdfBuilder.Node>()));

        var dir = Assert.Single(vol.Directories);
        Assert.Equal("/EMPTY", dir.Path);
        Assert.Empty(vol.Files);
    }

    [Fact]
    public void A_mixed_tree_reads_back_completely()
    {
        var (vol, image) = BuildAndRead("MIXED",
            UdfBuilder.Node.File("root.txt", Bytes("at the top")),
            UdfBuilder.Node.Dir("docs", new[]
            {
                UdfBuilder.Node.File("a.md", Bytes("aaa")),
                UdfBuilder.Node.Dir("img", new[]
                {
                    UdfBuilder.Node.File("pic.dat", new byte[1500]),
                }),
            }),
            UdfBuilder.Node.File("last.bin", new byte[100]));

        Assert.Equal(4, vol.Files.Count());
        Assert.Equal(2, vol.Directories.Count());           // docs, docs/img
        Assert.Equal(Bytes("at the top"), Extract(image, vol, "/root.txt"));
        Assert.Equal(Bytes("aaa"), Extract(image, vol, "/docs/a.md"));
        Assert.Equal(1500, Extract(image, vol, "/docs/img/pic.dat").Length);
    }

    // ---- determinism --------------------------------------------------------

    [Fact]
    public void The_same_tree_builds_byte_identical_images()
    {
        UdfBuilder.Node[] Tree() => new[]
        {
            UdfBuilder.Node.File("A.TXT", Bytes("alpha")),
            UdfBuilder.Node.Dir("D", new[] { UdfBuilder.Node.File("B.TXT", Bytes("beta")) }),
        };

        var first = UdfBuilder.Build("REPEATABLE", Tree());
        var second = UdfBuilder.Build("REPEATABLE", Tree());

        Assert.Equal(first, second);
    }

    // ---- warnings -----------------------------------------------------------

    [Fact]
    public void A_duplicate_name_in_a_directory_is_warned_about()
    {
        var result = UdfBuilder.BuildResultOf("DISC", new[]
        {
            UdfBuilder.Node.File("SAME.TXT", Bytes("one")),
            UdfBuilder.Node.File("same.txt", Bytes("two")),
        });

        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Duplicate name"));
    }
}

/// <summary>
/// Tests for the streamed UDF writer. The streamed path must produce exactly the
/// same image as the in-memory build for in-memory inputs (so all the descriptor
/// work above applies unchanged), and must round-trip through the reader when the
/// file content comes from a stream or a file on disk rather than a byte[].
/// </summary>
public class UdfStreamedTests
{
    private static byte[] Bytes(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    private static byte[] Extract(byte[] image, UdfVolume vol, string path)
    {
        var entry = vol.Files.Single(f => f.Path == path);
        using var o = new MemoryStream();
        using var img = new MemoryStream(image);
        UdfReader.ExtractFile(img, vol, entry, o);
        return o.ToArray();
    }

    [Fact]
    public void Streaming_matches_the_in_memory_build_byte_for_byte()
    {
        UdfBuilder.Node[] Tree() => new[]
        {
            UdfBuilder.Node.File("A.TXT", Bytes("hello world")),
            UdfBuilder.Node.Dir("SUB", new[] { UdfBuilder.Node.File("B.BIN", new byte[5000]) }),
        };

        var inMemory = UdfBuilder.Build("DISC", Tree());
        using var ms = new MemoryStream();
        UdfBuilder.BuildToStream("DISC", ms, Tree());

        Assert.Equal(inMemory, ms.ToArray());
    }

    [Fact]
    public void A_streamed_file_source_produces_the_same_image_as_an_in_memory_one()
    {
        var content = new byte[6000];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i * 17 + 3);

        var inMemory = UdfBuilder.Build("DISC", new[] { UdfBuilder.Node.File("F.DAT", content) });

        using var ms = new MemoryStream();
        UdfBuilder.BuildToStream("DISC", ms, new[]
        {
            UdfBuilder.Node.File("F.DAT", content.Length, () => new MemoryStream(content)),
        });

        Assert.Equal(inMemory, ms.ToArray());
    }

    [Fact]
    public void A_streamed_build_reads_back_through_the_reader()
    {
        var content = Bytes("this content was streamed into the extent, not buffered whole");
        using var ms = new MemoryStream();
        UdfBuilder.BuildToStream("STREAMED", ms, new[]
        {
            UdfBuilder.Node.Dir("D", new[]
            {
                UdfBuilder.Node.File("S.DAT", content.Length, () => new MemoryStream(content)),
            }),
        });

        var image = ms.ToArray();
        using var read = new MemoryStream(image);
        var vol = UdfReader.Read(read);
        Assert.Equal(content, Extract(image, vol, "/D/S.DAT"));
    }

    [Fact]
    public void A_file_backed_source_streams_from_disk()
    {
        var content = new byte[8192];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i & 0x7F);
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            using var ms = new MemoryStream();
            UdfBuilder.BuildToStream("DISK", ms, new[] { UdfBuilder.Node.FileFromPath("DISK.BIN", path) });

            var image = ms.ToArray();
            using var read = new MemoryStream(image);
            var vol = UdfReader.Read(read);
            Assert.Equal(content, Extract(image, vol, "/DISK.BIN"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_non_seekable_output_is_refused()
    {
        using var pipe = new NonSeekableStream();
        Assert.Throws<ArgumentException>(() =>
            UdfBuilder.BuildToStream("X", pipe, new[] { UdfBuilder.Node.File("A", Bytes("x")) }));
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
