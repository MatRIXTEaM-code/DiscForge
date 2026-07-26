using DiscForge.Core.Cdi;
using DiscForge.Core.Create;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class IsoBuilderTests
{
    private static byte[] Pattern(int len, byte seed)
    {
        var d = new byte[len];
        for (int i = 0; i < len; i++) d[i] = (byte)((i + seed) & 0xFF);
        return d;
    }

    [Fact]
    public void Builds_valid_pvd_svd_and_terminator()
    {
        var files = new[]
        {
            new IsoBuilder.FileEntry("README.TXT", Pattern(870, 1)),
            new IsoBuilder.FileEntry("game.dat", Pattern(10240, 2)),
        };
        var result = IsoBuilder.Build("OJISO", files); // Joliet on by default

        // PVD (type 1) at sector 16.
        var pvd = result.Image.AsSpan(16 * 2048, 2048);
        Assert.Equal(1, pvd[0]);
        Assert.Equal("CD001", System.Text.Encoding.ASCII.GetString(pvd.Slice(1, 5)));

        // SVD (type 2, Joliet) at sector 17, with the %/E escape sequence.
        var svd = result.Image.AsSpan(17 * 2048, 2048);
        Assert.Equal(2, svd[0]);
        Assert.Equal((byte)'%', svd[88]);
        Assert.Equal((byte)'/', svd[89]);
        Assert.Equal((byte)'E', svd[90]);

        // Terminator (type 0xFF) at sector 18 when Joliet is present.
        Assert.Equal(0xFF, result.Image[18 * 2048]);

        int volSectors = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(80, 4));
        Assert.Equal(result.Image.Length / 2048, volSectors);
    }

    [Fact]
    public void No_joliet_puts_terminator_at_sector_17()
    {
        var files = new[] { new IsoBuilder.FileEntry("F.BIN", Pattern(100, 1)) };
        var result = IsoBuilder.Build("VOL", files, joliet: false);
        Assert.Equal(1, result.Image[16 * 2048]);      // PVD
        Assert.Equal(0xFF, result.Image[17 * 2048]);   // terminator right after
    }

    [Fact]
    public void Builds_are_deterministic()
    {
        var files = new[] { new IsoBuilder.FileEntry("A.BIN", Pattern(4096, 7)) };
        var a = IsoBuilder.Build("VOL", files).Image;
        var b = IsoBuilder.Build("VOL", files).Image;
        Assert.True(a.AsSpan().SequenceEqual(b), "identical input must yield identical bytes");
    }

    [Fact]
    public void File_records_carry_the_real_data_length()
    {
        // Regression: Entry.Size was once never assigned, so every file's
        // recorded length was 0 (an ISO where all files appear empty).
        const int len = 1200;
        var files = new[] { new IsoBuilder.FileEntry("TEST.TXT", Pattern(len, 3)) };
        var img = IsoBuilder.Build("VOL", files, joliet: false).Image;

        var pvd = img.AsSpan(16 * 2048, 2048);
        int rootExtent = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            pvd.Slice(156 + 2, 4));
        var root = img.AsSpan(rootExtent * 2048, 2048).ToArray();

        // Walk past '.' and '..' to the first real entry.
        int p = root[0];
        p += root[p];
        var rec = root.AsSpan(p, root[p]);

        int nameLen = rec[32];
        string name = System.Text.Encoding.ASCII.GetString(rec.Slice(33, nameLen));
        int sizeField = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(10, 4));

        Assert.Equal("TEST.TXT;1", name);
        Assert.Equal(len, sizeField);
    }

    [Fact]
    public void Streaming_layout_matches_the_in_memory_build_byte_for_byte()
    {
        // The streaming writer and the in-memory builder must agree exactly —
        // deterministic builds make this an exact comparison rather than a
        // structural one.
        var tree = new[]
        {
            IsoBuilder.Node.File("readme.txt", Pattern(1200, 1)),
            IsoBuilder.Node.Dir("games", new[]
            {
                IsoBuilder.Node.File("sonic.bin", Pattern(5120, 2)),
                IsoBuilder.Node.Dir("saves", new[] { IsoBuilder.Node.File("slot1.sav", Pattern(256, 3)) }),
            }),
            IsoBuilder.Node.File("empty.dat", Array.Empty<byte>()),
        };

        var inMemory = IsoBuilder.BuildTree("OJTREE", tree, joliet: true, rockRidge: true).Image;

        var layout = IsoBuilder.Plan("OJTREE", tree, joliet: true, boot: null, rockRidge: true);
        using var ms = new MemoryStream();
        layout.WriteTo(ms);
        var streamed = ms.ToArray();

        Assert.Equal(inMemory.Length, streamed.Length);
        Assert.Equal((long)layout.VolumeSectors * 2048, streamed.Length);
        Assert.True(inMemory.AsSpan().SequenceEqual(streamed),
            "streaming output must be byte-identical to the in-memory build");
    }

    [Fact]
    public void Streaming_matches_in_memory_for_bootable_joliet_images_too()
    {
        var bootSector = new byte[2048];
        bootSector[510] = 0x55; bootSector[511] = 0xAA;
        var boot = new IsoBuilder.BootImage(bootSector);
        var tree = new[] { IsoBuilder.Node.File("setup.exe", Pattern(3000, 9)) };

        var inMemory = IsoBuilder.BuildTree("BOOTCD", tree, joliet: true, boot: boot).Image;

        var layout = IsoBuilder.Plan("BOOTCD", tree, joliet: true, boot: boot);
        using var ms = new MemoryStream();
        layout.WriteTo(ms);

        Assert.True(inMemory.AsSpan().SequenceEqual(ms.ToArray()));
    }

    [Fact]
    public void Plan_reads_lengths_only_and_streams_file_contents_from_disk()
    {
        // A path-backed source must produce the same image as its bytes would.
        var data = Pattern(9000, 4);
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(tmp, data);

            var fromDisk = IsoBuilder.Plan("VOL", new[] { IsoBuilder.Node.FromPath(tmp) });
            using var a = new MemoryStream();
            fromDisk.WriteTo(a);

            var fromBytes = IsoBuilder.Build("VOL",
                new[] { new IsoBuilder.FileEntry(Path.GetFileName(tmp), data) }).Image;

            Assert.True(fromBytes.AsSpan().SequenceEqual(a.ToArray()));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void A_file_beyond_the_iso_32bit_limit_is_refused_clearly()
    {
        // ISO 9660 stores a file's length as a u32. Refuse rather than truncate.
        var huge = new OversizeSource(4L * 1024 * 1024 * 1024);   // exactly 4 GiB
        var node = IsoBuilder.Node.File("huge.bin", huge);

        var ex = Assert.Throws<NotSupportedException>(() => IsoBuilder.Plan("VOL", new[] { node }));
        Assert.Contains("32-bit", ex.Message);
    }

    /// <summary>Reports a huge Length without allocating anything — lets us test
    /// the size guard without a real multi-gigabyte file.</summary>
    private sealed class OversizeSource(long length) : IsoBuilder.FileSource
    {
        public override long Length => length;
        public override Stream OpenRead() => throw new NotSupportedException("never read");
    }

    [Fact]
    public void Joliet_adds_a_second_hierarchy_and_grows_the_image()
    {
        var files = new[] { new IsoBuilder.FileEntry("My Long Name.txt", Pattern(2048, 3)) };
        var withJoliet = IsoBuilder.Build("Disc", files, joliet: true).Image;
        var without = IsoBuilder.Build("Disc", files, joliet: false).Image;
        Assert.True(withJoliet.Length > without.Length); // extra descriptor + dir/path tables
    }

    [Fact]
    public void El_torito_writes_boot_record_and_valid_catalog()
    {
        var files = new[] { new IsoBuilder.FileEntry("README.TXT", Pattern(500, 1)) };
        var bootSector = new byte[2048];
        System.Text.Encoding.ASCII.GetBytes("BOOT").CopyTo(bootSector, 0);
        bootSector[510] = 0x55; bootSector[511] = 0xAA;

        var boot = new IsoBuilder.BootImage(bootSector, IsoBuilder.BootMediaType.NoEmulation);
        var img = IsoBuilder.Build("BOOTDISC", files, joliet: true, boot: boot).Image;

        // Boot Record VD at sector 17 (PVD=16, BootRec=17, SVD=18, term=19).
        var br = img.AsSpan(17 * 2048, 2048);
        Assert.Equal(0, br[0]);
        Assert.Equal("CD001", System.Text.Encoding.ASCII.GetString(br.Slice(1, 5)));
        Assert.Equal("EL TORITO SPECIFICATION",
            System.Text.Encoding.ASCII.GetString(br.Slice(7, 23)));

        int catSector = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(br.Slice(71, 4));
        var cat = img.AsSpan(catSector * 2048, 64);

        // Validation entry: header 1, 0x55AA, and checksum sums the whole entry to 0.
        Assert.Equal(1, cat[0]);
        Assert.Equal(0x55, cat[30]);
        Assert.Equal(0xAA, cat[31]);
        ushort sum = 0;
        for (int i = 0; i < 32; i += 2)
            sum += System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(cat.Slice(i, 2));
        Assert.Equal(0, sum);

        // Default entry: bootable, load RBA points at the "BOOT" image.
        Assert.Equal(0x88, cat[32]);
        int loadRba = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cat.Slice(40, 4));
        Assert.Equal("BOOT", System.Text.Encoding.ASCII.GetString(img.AsSpan(loadRba * 2048, 4)));
    }

    [Fact]
    public void El_torito_shifts_terminator_but_stays_deterministic()
    {
        var files = new[] { new IsoBuilder.FileEntry("F.BIN", Pattern(100, 1)) };
        var boot = new IsoBuilder.BootImage(new byte[512]);
        var a = IsoBuilder.Build("VOL", files, joliet: true, boot: boot).Image;
        var b = IsoBuilder.Build("VOL", files, joliet: true, boot: boot).Image;
        Assert.True(a.AsSpan().SequenceEqual(b));
        Assert.Equal(0xFF, a[19 * 2048]); // terminator now at 19 (PVD,BootRec,SVD,term)
    }

    [Fact]
    public void Rock_ridge_grows_iso_records_and_stays_deterministic()
    {
        var files = new[] { new IsoBuilder.FileEntry("a-long-unix-name.tar.gz", Pattern(500, 1)) };
        var without = IsoBuilder.Build("VOL", files, joliet: false, rockRidge: false).Image;
        var withRr = IsoBuilder.Build("VOL", files, joliet: false, rockRidge: true).Image;
        Assert.True(withRr.Length >= without.Length); // SU entries enlarge dir records

        var again = IsoBuilder.Build("VOL", files, joliet: false, rockRidge: true).Image;
        Assert.True(withRr.AsSpan().SequenceEqual(again)); // deterministic
    }

    [Fact]
    public void Rock_ridge_writes_sp_and_er_in_root_self_record()
    {
        var files = new[] { new IsoBuilder.FileEntry("readme.txt", Pattern(50, 1)) };
        var img = IsoBuilder.Build("VOL", files, joliet: false, rockRidge: true).Image;

        // Root directory extent from the PVD root record (offset 156, extent at +2).
        var pvd = img.AsSpan(16 * 2048, 2048);
        int rootExtent = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            pvd.Slice(156 + 2, 4));

        // The root '.' record is the first record in the root extent; its System
        // Use area must contain the SP (0x53 0x50) and ER (0x45 0x52) signatures.
        var rootDir = img.AsSpan(rootExtent * 2048, 2048).ToArray();
        int firstRecLen = rootDir[0];
        var firstRec = rootDir.AsSpan(0, firstRecLen);
        Assert.True(ContainsSig(firstRec, (byte)'S', (byte)'P'), "SP entry present in root '.'");
        Assert.True(ContainsSig(firstRec, (byte)'E', (byte)'R'), "ER entry present in root '.'");
        Assert.True(ContainsSig(firstRec, (byte)'P', (byte)'X'), "PX entry present in root '.'");
    }

    private static bool ContainsSig(ReadOnlySpan<byte> rec, byte a, byte b)
    {
        for (int i = 0; i + 1 < rec.Length; i++)
            if (rec[i] == a && rec[i + 1] == b) return true;
        return false;
    }

    [Fact]
    public void Normalizes_long_names_and_warns()
    {
        var files = new[]
        {
            new IsoBuilder.FileEntry("a-very-long-filename.jpeg", Pattern(100, 3)),
        };
        var result = IsoBuilder.Build("VOL", files);
        Assert.NotEmpty(result.Warnings); // 8.3 normalization warned
    }

    [Fact]
    public void Handles_zero_length_file()
    {
        var files = new[] { new IsoBuilder.FileEntry("EMPTY.TXT", Array.Empty<byte>()) };
        var result = IsoBuilder.Build("VOL", files);
        Assert.True(result.Image.Length >= 21 * 2048); // at least through root dir
    }

    [Fact]
    public void Builds_nested_tree_with_valid_pvd()
    {
        var tree = new[]
        {
            IsoBuilder.Node.File("readme.txt", Pattern(120, 1)),
            IsoBuilder.Node.Dir("games", new[]
            {
                IsoBuilder.Node.File("sonic.bin", Pattern(5120, 2)),
                IsoBuilder.Node.Dir("saves", new[]
                {
                    IsoBuilder.Node.File("slot1.sav", Pattern(256, 3)),
                }),
            }),
            IsoBuilder.Node.Dir("docs", new[]
            {
                IsoBuilder.Node.File("manual.txt", Pattern(330, 4)),
            }),
        };

        var result = IsoBuilder.BuildTree("OJTREE", tree);

        var pvd = result.Image.AsSpan(16 * 2048, 2048);
        Assert.Equal(1, pvd[0]);
        Assert.Equal("CD001", System.Text.Encoding.ASCII.GetString(pvd.Slice(1, 5)));

        // Path table size (both-endian u32 at offset 132) must be non-zero and
        // consistent little vs big endian.
        uint ptLe = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(132, 4));
        uint ptBe = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(pvd.Slice(136, 4));
        Assert.True(ptLe > 10);          // more than just the root entry
        Assert.Equal(ptLe, ptBe);

        // Volume size self-consistent.
        uint vol = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(80, 4));
        Assert.Equal(result.Image.Length / 2048, (int)vol);
    }
}

public class CdiCreatorTests
{
    private static byte[] Pattern(int len, byte seed)
    {
        var d = new byte[len];
        for (int i = 0; i < len; i++) d[i] = (byte)((i * 7 + seed) & 0xFF);
        return d;
    }

    [Fact]
    public void Folder_to_cdi_to_extract_roundtrips_the_iso()
    {
        var files = new[]
        {
            new IsoBuilder.FileEntry("DATA1.BIN", Pattern(3000, 1)),
            new IsoBuilder.FileEntry("DATA2.BIN", Pattern(5000, 2)),
        };

        // Build the expected ISO independently.
        var expectedIso = IsoBuilder.Build("OJVOL", files).Image;

        using var cdi = new MemoryStream();
        var result = CdiCreator.CreateDataImage("OJVOL", files, CdiVersion.V35, cdi);
        Assert.Equal(expectedIso.Length / 2048, result.IsoSectors);

        // Parse the CDI and extract the single data track's user data.
        cdi.Position = 0;
        var image = CdiParser.Parse(cdi);
        Assert.Equal(1, image.TrackCount);
        var track = image.AllTracks.Single();
        Assert.Equal(CdiTrackMode.Mode1, track.Mode);
        Assert.Equal(CdiSectorSize.S2048, track.SectorSize);

        using var extracted = new MemoryStream();
        CdiExtractor.ExtractUserData(cdi, track, extracted);

        Assert.True(expectedIso.AsSpan().SequenceEqual(extracted.ToArray()),
            "extracted ISO must match the independently-built ISO");
    }

    [Theory]
    [InlineData(CdiVersion.V2)]
    [InlineData(CdiVersion.V3)]
    [InlineData(CdiVersion.V35)]
    public void Creates_and_verifies_clean_across_versions(CdiVersion version)
    {
        var files = new[] { new IsoBuilder.FileEntry("F.BIN", Pattern(9000, 5)) };
        using var cdi = new MemoryStream();
        CdiCreator.CreateDataImage("VOL", files, version, cdi);

        cdi.Position = 0;
        var image = CdiParser.Parse(cdi);
        var report = CdiVerifier.Verify(cdi, image, computeUserChecksums: true);
        Assert.True(report.Passed);
        Assert.Equal(version, image.Version);
    }
}
