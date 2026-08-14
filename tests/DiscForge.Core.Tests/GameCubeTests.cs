// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.GameCube;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// GameCube / Wii / RVZ tests. Everything is built from hand-made synthetic fixtures
/// (the project has no external oracle here), and the core proof is a round trip:
/// build a valid GameCube image with a real FST + files, read the tree back, and
/// assert ExtractFile returns each file's exact bytes. The Wii tests confirm that
/// only the plaintext partition table is read — never partition contents. The RVZ
/// tests confirm header identification and metadata.
/// </summary>
public class GameCubeTests
{
    // ---- synthetic GameCube image builder -----------------------------------

    private const long FileAaa = 0x1000;   // "/aaa.txt"
    private const long FileCcc = 0x1100;   // "/sub/ccc.bin"
    private const long FileBbb = 0x1200;   // "/bbb.dat"

    private static readonly byte[] AaaData = Encoding.ASCII.GetBytes("Hello GameCube!");
    private static readonly byte[] CccData = { 1, 2, 3, 4, 5, 6, 7 };
    private static readonly byte[] BbbData = Encoding.ASCII.GetBytes("the second file's data");

    // FST placed just past the boot header.
    private const long FstOffset = 0x800;

    private static byte[] BuildGcm()
    {
        // FST: root, file aaa, dir sub, file ccc (inside sub), file bbb (back in root).
        // Depth-first order — indices 0..4, so total count = 5.
        var strings = new MemoryStream();
        int OffAaa = AddName(strings, "aaa.txt");
        int OffSub = AddName(strings, "sub");
        int OffCcc = AddName(strings, "ccc.bin");
        int OffBbb = AddName(strings, "bbb.dat");
        byte[] stringTable = strings.ToArray();

        const int count = 5;
        var fst = new byte[count * 12 + stringTable.Length];

        // 0: root dir — flag 1, name 0, parent 0, "length" = total count.
        WriteEntry(fst, 0, dir: true, nameOff: 0, field2: 0, field3: count);
        // 1: file aaa.txt at FileAaa.
        WriteEntry(fst, 1, dir: false, nameOff: OffAaa, field2: (uint)FileAaa, field3: (uint)AaaData.Length);
        // 2: dir sub — parent 0, next index = 4 (children: index 3 only).
        WriteEntry(fst, 2, dir: true, nameOff: OffSub, field2: 0, field3: 4);
        // 3: file ccc.bin inside sub.
        WriteEntry(fst, 3, dir: false, nameOff: OffCcc, field2: (uint)FileCcc, field3: (uint)CccData.Length);
        // 4: file bbb.dat back in root.
        WriteEntry(fst, 4, dir: false, nameOff: OffBbb, field2: (uint)FileBbb, field3: (uint)BbbData.Length);

        Array.Copy(stringTable, 0, fst, count * 12, stringTable.Length);

        long imageSize = FileBbb + BbbData.Length;
        var image = new byte[imageSize];

        // Boot header.
        Encoding.ASCII.GetBytes("GALE").CopyTo(image, 0x00);   // game code
        Encoding.ASCII.GetBytes("01").CopyTo(image, 0x04);     // maker code
        image[0x06] = 0;                                        // disc id
        image[0x07] = 2;                                        // version
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x1C, 4), GcmReader.Magic);
        Encoding.ASCII.GetBytes("SUPER SMASH BROS").CopyTo(image, 0x20); // game name
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x420, 4), 0x600);       // DOL offset
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x424, 4), (uint)FstOffset);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x428, 4), (uint)fst.Length);

        fst.CopyTo(image, FstOffset);
        AaaData.CopyTo(image, FileAaa);
        CccData.CopyTo(image, FileCcc);
        BbbData.CopyTo(image, FileBbb);

        return image;
    }

    private static int AddName(MemoryStream table, string name)
    {
        int offset = (int)table.Length;
        var bytes = Encoding.ASCII.GetBytes(name);
        table.Write(bytes, 0, bytes.Length);
        table.WriteByte(0);
        return offset;
    }

    private static void WriteEntry(byte[] fst, int index, bool dir, int nameOff, uint field2, uint field3)
    {
        int at = index * 12;
        fst[at] = (byte)(dir ? 1 : 0);
        fst[at + 1] = (byte)((nameOff >> 16) & 0xFF);
        fst[at + 2] = (byte)((nameOff >> 8) & 0xFF);
        fst[at + 3] = (byte)(nameOff & 0xFF);
        BinaryPrimitives.WriteUInt32BigEndian(fst.AsSpan(at + 4, 4), field2);
        BinaryPrimitives.WriteUInt32BigEndian(fst.AsSpan(at + 8, 4), field3);
    }

    // ---- GCM tests ----------------------------------------------------------

    [Fact]
    public void Gcm_header_fields_parse()
    {
        var disc = GcmReader.Read(new MemoryStream(BuildGcm()));
        Assert.Equal("GALE", disc.GameCode);
        Assert.Equal("01", disc.MakerCode);
        Assert.Equal("SUPER SMASH BROS", disc.GameName);
        Assert.Equal(0, disc.DiscId);
        Assert.Equal(2, disc.Version);
    }

    [Fact]
    public void Gcm_is_recognised_and_garbage_is_not()
    {
        Assert.True(GcmReader.IsGcm(new MemoryStream(BuildGcm())));
        Assert.False(GcmReader.IsGcm(new MemoryStream(new byte[0x40])));
    }

    [Fact]
    public void Gcm_tree_has_the_expected_paths()
    {
        var disc = GcmReader.Read(new MemoryStream(BuildGcm()));
        var paths = disc.Entries.Select(e => e.Path).ToArray();
        Assert.Equal(new[] { "/aaa.txt", "/sub", "/sub/ccc.bin", "/bbb.dat" }, paths);
    }

    [Fact]
    public void Gcm_directory_is_flagged_and_sized_zero()
    {
        var disc = GcmReader.Read(new MemoryStream(BuildGcm()));
        var sub = Assert.Single(disc.Entries, e => e.Path == "/sub");
        Assert.True(sub.IsDirectory);
        Assert.Equal(0, sub.Size);
    }

    [Fact]
    public void Gcm_file_sizes_and_offsets_are_correct()
    {
        var disc = GcmReader.Read(new MemoryStream(BuildGcm()));
        var aaa = Assert.Single(disc.Entries, e => e.Path == "/aaa.txt");
        Assert.False(aaa.IsDirectory);
        Assert.Equal(AaaData.Length, aaa.Size);
        Assert.Equal(FileAaa, aaa.Offset);

        var ccc = Assert.Single(disc.Entries, e => e.Path == "/sub/ccc.bin");
        Assert.Equal(CccData.Length, ccc.Size);
        Assert.Equal(FileCcc, ccc.Offset);
    }

    [Fact]
    public void Gcm_extract_returns_exact_bytes()
    {
        var image = BuildGcm();
        var stream = new MemoryStream(image);
        var disc = GcmReader.Read(stream);

        foreach (var (path, expected) in new (string, byte[])[]
        {
            ("/aaa.txt", AaaData),
            ("/sub/ccc.bin", CccData),
            ("/bbb.dat", BbbData),
        })
        {
            var entry = Assert.Single(disc.Entries, e => e.Path == path);
            var output = new MemoryStream();
            GcmReader.ExtractFile(stream, entry, output);
            Assert.Equal(expected, output.ToArray());
        }
    }

    [Fact]
    public void Gcm_extracting_a_directory_throws()
    {
        var stream = new MemoryStream(BuildGcm());
        var disc = GcmReader.Read(stream);
        var sub = Assert.Single(disc.Entries, e => e.Path == "/sub");
        Assert.Throws<GameCubeFormatException>(() => GcmReader.ExtractFile(stream, sub, new MemoryStream()));
    }

    [Fact]
    public void Gcm_wrong_magic_throws()
    {
        var image = BuildGcm();
        image[0x1C] = 0; // corrupt the magic
        Assert.Throws<GameCubeFormatException>(() => GcmReader.Read(new MemoryStream(image)));
    }

    [Fact]
    public void Gcm_short_input_throws()
    {
        Assert.Throws<GameCubeFormatException>(() => GcmReader.Read(new MemoryStream(new byte[0x100])));
    }

    // ---- synthetic Wii image builder ----------------------------------------

    // Partition data offsets point far past the (tiny) image on purpose: if the reader
    // ever tried to read partition *contents* it would fault. It must not.
    private const long DataPartOffset = 0xF800000;
    private const long UpdatePartOffset = 0x50000;

    private static byte[] BuildWii()
    {
        // Big enough to hold the volume header and the partition table region.
        var image = new byte[0x50000];

        Encoding.ASCII.GetBytes("RSBE").CopyTo(image, 0x00);            // game code
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x18, 4), WiiDisc.Magic);
        Encoding.ASCII.GetBytes("New Super Wii Bros").CopyTo(image, 0x20); // game name

        // Partition group table at 0x40000: group 0 has 2 partitions.
        long groupBase = WiiDisc.PartitionTableOffset; // 0x40000
        long entryTableOffset = 0x48000;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan((int)groupBase + 0, 4), 2);                       // count
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan((int)groupBase + 4, 4), (uint)(entryTableOffset >> 2)); // table off >> 2
        // groups 1..3 stay zero (no partitions).

        // Partition entries: [offset >> 2][type].
        int e0 = (int)entryTableOffset;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(e0 + 0, 4), (uint)(DataPartOffset >> 2));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(e0 + 4, 4), 0); // DATA
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(e0 + 8, 4), (uint)(UpdatePartOffset >> 2));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(e0 + 12, 4), 1); // UPDATE

        return image;
    }

    // ---- Wii tests ----------------------------------------------------------

    [Fact]
    public void Wii_is_recognised_and_header_parses()
    {
        var stream = new MemoryStream(BuildWii());
        Assert.True(WiiDisc.IsWii(stream));
        var vol = WiiDisc.Read(stream);
        Assert.Equal("RSBE", vol.GameCode);
        Assert.Equal("New Super Wii Bros", vol.GameName);
    }

    [Fact]
    public void Wii_partition_table_parses_types_and_offsets()
    {
        var vol = WiiDisc.Read(new MemoryStream(BuildWii()));
        Assert.Equal(2, vol.Partitions.Count);

        var data = vol.Partitions[0];
        Assert.Equal(WiiPartitionType.Data, data.Type);
        Assert.Equal(DataPartOffset, data.Offset);

        var update = vol.Partitions[1];
        Assert.Equal(WiiPartitionType.Update, update.Type);
        Assert.Equal(UpdatePartOffset, update.Offset);
    }

    [Fact]
    public void Wii_reader_never_reads_partition_contents()
    {
        // The DATA partition offset (0xF800000) is far beyond the image length. The read
        // succeeds only because the reader parses the plaintext table and stops there —
        // it never seeks into an (encrypted) partition. If it did, this would throw.
        var image = BuildWii();
        Assert.True(image.Length < DataPartOffset);
        var vol = WiiDisc.Read(new MemoryStream(image));
        Assert.Equal(DataPartOffset, vol.Partitions[0].Offset);
    }

    [Fact]
    public void Wii_short_input_throws()
    {
        Assert.Throws<GameCubeFormatException>(() => WiiDisc.Read(new MemoryStream(new byte[0x80])));
    }

    // ---- synthetic Wii partition (ticket + header + TMD) builder -------------
    //
    // Lays down ONE DATA partition with a plaintext ticket, partition header, and TMD.
    // Everything is signed-but-unencrypted metadata. The "data region" is filled with a
    // recognizable poison byte so any accidental read there would be provably wrong — and
    // its declared offset points BEYOND the image, so a read would also fault outright.

    private const long PartOffset = 0x60000;         // where the partition starts
    private const long TmdRel = 0x2C0;               // TMD offset within the partition (>>2-aligned)
    private const long DataRel = 0x100000;           // data offset within the partition (relative)
    private const long DeclaredDataSize = 0x2000000; // declared data-region size
    private const byte Poison = 0xEE;                // "encrypted"/forbidden sentinel

    // The 8-byte title id: type-word 0x00010000 then the 4-char game id "RMCE".
    private static readonly byte[] TitleId8 =
        { 0x00, 0x01, 0x00, 0x00, (byte)'R', (byte)'M', (byte)'C', (byte)'E' };

    private const int SynthTitleVersion = 0x0207;
    private const int SynthContentCount = 3;

    // Absolute data offset the reader should report (partition start + relative). Chosen
    // to sit past the end of the (small) image on purpose — see the clean-room guard test.
    private const long ExpectedDataOffset = PartOffset + DataRel;

    private static byte[] BuildWiiWithPartition()
    {
        // Image large enough to hold the volume header, partition table, and the
        // ticket/header/TMD of the partition — but NOT its (declared) data region.
        var image = new byte[PartOffset + TmdRel + 0x1000];

        Encoding.ASCII.GetBytes("RMCE").CopyTo(image, 0x00);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x18, 4), WiiDisc.Magic);
        Encoding.ASCII.GetBytes("Mario Kart Wii").CopyTo(image, 0x20);

        // Partition group table at 0x40000: group 0 has 1 partition, table right after.
        long groupBase = WiiDisc.PartitionTableOffset;
        long entryTableOffset = 0x40020;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan((int)groupBase + 0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan((int)groupBase + 4, 4), (uint)(entryTableOffset >> 2));

        int e0 = (int)entryTableOffset;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(e0 + 0, 4), (uint)(PartOffset >> 2)); // offset >> 2
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(e0 + 4, 4), 0);                        // DATA

        int p = (int)PartOffset;

        // Ticket: title id at ticket offset 0x1DC. (We never write or read a title key.)
        TitleId8.CopyTo(image, p + 0x1DC);

        // Partition header immediately after the 0x2A4-byte ticket.
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(p + 0x2A4, 4), 0x208);              // TMD size (unused by us)
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(p + 0x2A8, 4), (uint)(TmdRel >> 2));// TMD offset >> 2
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(p + 0x2AC, 4), 0x100);              // cert size
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(p + 0x2B0, 4), 0);                  // cert offset
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(p + 0x2B4, 4), 0);                  // H3 offset
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(p + 0x2B8, 4), (uint)(DataRel >> 2));// data offset >> 2
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(p + 0x2BC, 4), (uint)(DeclaredDataSize >> 2)); // data size >> 2

        // TMD at partition + TmdRel: title id at 0x18C, title version at 0x1DC, contents at 0x1DE.
        int t = p + (int)TmdRel;
        TitleId8.CopyTo(image, t + 0x18C);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(t + 0x1DC, 2), (ushort)SynthTitleVersion);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(t + 0x1DE, 2), (ushort)SynthContentCount);

        return image;
    }

    private static WiiPartition SinglePartition(byte[] image) =>
        Assert.Single(WiiDisc.Read(new MemoryStream(image)).Partitions);

    [Fact]
    public void Wii_partition_details_read_ticket_and_tmd_fields()
    {
        var image = BuildWiiWithPartition();
        var stream = new MemoryStream(image);
        var part = SinglePartition(image);

        var d = WiiDisc.ReadPartitionDetails(stream, part);
        Assert.Equal(WiiPartitionType.Data, d.Type);
        Assert.Equal(PartOffset, d.Offset);
        Assert.Equal("00010000524D4345", d.TitleId);   // 0x00010000 + "RMCE"
        Assert.Equal("RMCE", d.GameId);
        Assert.Equal(SynthTitleVersion, d.TitleVersion);
        Assert.Equal(SynthContentCount, d.ContentCount);
        Assert.Equal(ExpectedDataOffset, d.DataOffset);
        Assert.Equal(DeclaredDataSize, d.DataSize);
        Assert.Equal(PartOffset + TmdRel, d.TmdOffset);
    }

    [Fact]
    public void Wii_partition_details_via_read_all()
    {
        var image = BuildWiiWithPartition();
        var stream = new MemoryStream(image);
        var vol = WiiDisc.Read(stream);
        var all = WiiDisc.ReadAllDetails(stream, vol);
        var d = Assert.Single(all);
        Assert.Equal("RMCE", d.GameId);
        Assert.Equal(SynthContentCount, d.ContentCount);
    }

    [Fact]
    public void Wii_partition_title_id_without_ascii_game_id_is_null()
    {
        // Replace the last 4 title-id bytes with non-printable bytes: GameId must be null
        // but the hex TitleId still round-trips.
        var image = BuildWiiWithPartition();
        int p = (int)PartOffset;
        image[p + 0x1DC + 4] = 0x00;
        image[p + 0x1DC + 5] = 0x01;
        image[p + 0x1DC + 6] = 0x02;
        image[p + 0x1DC + 7] = 0x03;

        var d = WiiDisc.ReadPartitionDetails(new MemoryStream(image), SinglePartition(image));
        Assert.Null(d.GameId);
        Assert.Equal("0001000000010203", d.TitleId);
    }

    // CLEAN-ROOM GUARD: proves the reader never touches the (encrypted) data region.
    // The data region is filled with a poison pattern AND its declared offset points past
    // the end of the image — if ReadPartitionDetails ever seeked there it would fault or
    // return poisoned bytes. It succeeds, so it demonstrably reads only ticket/TMD metadata.
    [Fact]
    public void Wii_partition_details_never_read_the_encrypted_data_region()
    {
        var image = BuildWiiWithPartition();

        // Poison every byte from the partition's TMD end onward — anywhere the (encrypted)
        // data would live within the image.
        for (int i = (int)PartOffset + (int)TmdRel + 0x200; i < image.Length; i++)
            image[i] = Poison;

        var d = WiiDisc.ReadPartitionDetails(new MemoryStream(image), SinglePartition(image));

        // The reported data region begins beyond the end of the image: proof we never read it.
        Assert.True(d.DataOffset > image.Length,
            "the declared data region must lie beyond the image, so a read would fault");
        Assert.Equal(ExpectedDataOffset, d.DataOffset);
        // And the metadata still came back correctly.
        Assert.Equal("RMCE", d.GameId);
        Assert.Equal(SynthContentCount, d.ContentCount);
    }

    [Fact]
    public void Wii_partition_details_truncated_ticket_throws()
    {
        // Image ends inside the ticket/header region → format exception, not a crash.
        var full = BuildWiiWithPartition();
        var truncated = full.AsSpan(0, (int)PartOffset + 0x100).ToArray();
        var part = new WiiPartition { Type = WiiPartitionType.Data, RawType = 0, Offset = PartOffset };
        Assert.Throws<GameCubeFormatException>(
            () => WiiDisc.ReadPartitionDetails(new MemoryStream(truncated), part));
    }

    [Fact]
    public void Wii_partition_details_truncated_tmd_throws()
    {
        // Ticket/header present, but the TMD offset points past the end of the image.
        var full = BuildWiiWithPartition();
        var truncated = full.AsSpan(0, (int)PartOffset + (int)TmdRel + 0x10).ToArray();
        var part = new WiiPartition { Type = WiiPartitionType.Data, RawType = 0, Offset = PartOffset };
        Assert.Throws<GameCubeFormatException>(
            () => WiiDisc.ReadPartitionDetails(new MemoryStream(truncated), part));
    }

    [Fact]
    public void Wii_partition_details_offset_beyond_image_throws()
    {
        // A partition whose start is past EOF: the very first (ticket) read is out of range.
        var image = BuildWiiWithPartition();
        var part = new WiiPartition
        {
            Type = WiiPartitionType.Data,
            RawType = 0,
            Offset = image.Length + 0x1000,
        };
        Assert.Throws<GameCubeFormatException>(
            () => WiiDisc.ReadPartitionDetails(new MemoryStream(image), part));
    }

    // ---- synthetic RVZ/WIA header builder -----------------------------------

    private static byte[] BuildRvz(string magic, uint compression, bool withStructure = false,
                                   uint partitions = 0, uint rawData = 1, uint groups = 4)
    {
        // 0xD8 = identification header only; 0x124 = through the disc-structure directory.
        var buf = new byte[withStructure ? 0x124 : 0xD8];
        Encoding.ASCII.GetBytes(magic[..3]).CopyTo(buf, 0);
        buf[3] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x04, 4), 0x01000000);   // version
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x08, 4), 0x01000000);   // version_compatible
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x0C, 4), 0x80);         // disc-struct size
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(0x24, 8), 1_459_978_240);// iso size (GC ~1.36 GiB)

        // Disc structure at 0x48.
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x48 + 0x00, 4), 1);           // disc type = GameCube
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x48 + 0x04, 4), compression); // compression
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(0x48 + 0x08, 4), 9);            // level
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x48 + 0x0C, 4), 0x200000);    // chunk size = 2 MiB

        // Disc header (0x80 unencrypted bytes) at 0x58.
        int dh = 0x58;
        Encoding.ASCII.GetBytes("GALE01").CopyTo(buf, dh + 0x00);           // 6-char game id
        Encoding.ASCII.GetBytes("Super Smash Bros. Melee").CopyTo(buf, dh + 0x20); // game name

        if (withStructure)
        {
            int ds = 0x48;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(ds + 0x90, 4), partitions);  // partition count
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(ds + 0xB4, 4), rawData);     // raw-data count
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(ds + 0xC4, 4), groups);      // group count
            buf[ds + 0xD4] = 5;                                                            // compressor-data length
        }

        return buf;
    }

    // ---- RVZ tests ----------------------------------------------------------

    [Fact]
    public void Wia_header_identifies_as_wia()
    {
        var info = RvzReader.ReadInfo(new MemoryStream(BuildRvz("WIA", (uint)RvzCompression.Lzma)));
        Assert.Equal(RvzFormat.Wia, info.Format);
        Assert.Equal(RvzCompression.Lzma, info.Compression);
        Assert.Equal("GALE01", info.GameId);
        Assert.Equal("Super Smash Bros. Melee", info.GameName);
    }

    [Fact]
    public void Rvz_header_identifies_as_rvz_with_zstd()
    {
        var info = RvzReader.ReadInfo(new MemoryStream(BuildRvz("RVZ", (uint)RvzCompression.Zstd)));
        Assert.Equal(RvzFormat.Rvz, info.Format);
        Assert.Equal(RvzCompression.Zstd, info.Compression);
        Assert.Equal(0x200000u, info.ChunkSize);
        Assert.Equal(1_459_978_240ul, info.IsoSize);
    }

    [Fact]
    public void Rvz_is_recognised_and_garbage_is_not()
    {
        Assert.True(RvzReader.IsRvzOrWia(new MemoryStream(BuildRvz("RVZ", 5))));
        Assert.False(RvzReader.IsRvzOrWia(new MemoryStream(Encoding.ASCII.GetBytes("NOPEnope...."))));
    }

    [Fact]
    public void Rvz_decode_is_deferred_and_throws()
    {
        Assert.Throws<GameCubeFormatException>(() => RvzReader.Decode(new MemoryStream(), new MemoryStream()));
    }

    [Fact]
    public void Rvz_reads_the_disc_structure_directory_when_present()
    {
        // A Wii-shaped container: 2 partitions, 3 raw regions, 40 groups.
        var info = RvzReader.ReadInfo(new MemoryStream(
            BuildRvz("RVZ", (uint)RvzCompression.Zstd, withStructure: true,
                     partitions: 2, rawData: 3, groups: 40)));
        Assert.True(info.HasStructure);
        Assert.Equal(2u, info.PartitionCount);
        Assert.Equal(3u, info.RawDataCount);
        Assert.Equal(40u, info.GroupCount);
        Assert.Equal(5, info.CompressorDataLength);
    }

    [Fact]
    public void Rvz_identification_still_works_without_the_structure_directory()
    {
        // A minimal (0xD8) header still identifies; structure just isn't available.
        var info = RvzReader.ReadInfo(new MemoryStream(BuildRvz("WIA", (uint)RvzCompression.Lzma)));
        Assert.False(info.HasStructure);
        Assert.Equal("GALE01", info.GameId);
        Assert.Equal(0u, info.GroupCount);
    }

    [Fact]
    public void Rvz_short_input_throws()
    {
        Assert.Throws<GameCubeFormatException>(() => RvzReader.ReadInfo(new MemoryStream(new byte[0x40])));
    }
}
