// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Identify;
using DiscForge.Core.Partition;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Partition-table parsing (MBR, GPT, best-effort PS2 APA) and the composed
/// whole-disk reader. Every fixture is built by hand in-test at documented
/// offsets and read back — primaries, an extended/logical chain, a GPT with named
/// entries, an APA chain, and an MBR disk carrying a real FAT12 partition so the
/// composed reader's per-partition filesystem detection can be asserted.
/// </summary>
public class PartitionTests
{
    // ======================================================================
    // builders
    // ======================================================================

    private static void WriteMbrEntry(byte[] disk, int off, bool boot, byte type, uint startLba, uint count)
    {
        disk[off] = (byte)(boot ? 0x80 : 0x00);
        disk[off + 4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(off + 8), startLba);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(off + 12), count);
    }

    private static void WriteSignature(byte[] disk, int sectorBase)
    {
        disk[sectorBase + 0x1FE] = 0x55;
        disk[sectorBase + 0x1FF] = 0xAA;
    }

    // A 512-byte MBR with two primaries: FAT16 (0x06) and Linux (0x83).
    private static byte[] BuildMbrTwoPrimaries()
    {
        var disk = new byte[512];
        WriteMbrEntry(disk, 0x1BE + 0 * 16, boot: true, type: 0x06, startLba: 2048, count: 200_000);
        WriteMbrEntry(disk, 0x1BE + 1 * 16, boot: false, type: 0x83, startLba: 204_800, count: 800_000);
        WriteSignature(disk, 0);
        return disk;
    }

    // A whole-disk image with one extended container (0x0F) holding two logical
    // partitions via an EBR chain. Sector layout (512-byte sectors):
    //   LBA 0        primary MBR (one extended entry, base = extBase)
    //   LBA extBase  EBR #1: logical A + link to EBR #2
    //   LBA extBase+ link  EBR #2: logical B, no further link
    private static byte[] BuildMbrExtended(long extBase, long link, out long logicalAStart, out long logicalBStart)
    {
        const int sectors = 4096;
        var disk = new byte[sectors * 512];

        // Primary MBR: a single extended (LBA) container covering the whole area.
        WriteMbrEntry(disk, 0x1BE + 0 * 16, boot: false, type: 0x0F, startLba: (uint)extBase, count: 3000);
        WriteSignature(disk, 0);

        // EBR #1 at extBase: logical A starts 63 sectors into this EBR; link points
        // to EBR #2 relative to the container base.
        int ebr1 = (int)(extBase * 512);
        WriteMbrEntry(disk, ebr1 + 0x1BE + 0 * 16, boot: false, type: 0x83, startLba: 63, count: 500);
        WriteMbrEntry(disk, ebr1 + 0x1BE + 1 * 16, boot: false, type: 0x0F, startLba: (uint)link, count: 700);
        WriteSignature(disk, ebr1);
        logicalAStart = extBase + 63;

        // EBR #2 at extBase+link: logical B, no next link.
        int ebr2 = (int)((extBase + link) * 512);
        WriteMbrEntry(disk, ebr2 + 0x1BE + 0 * 16, boot: false, type: 0x06, startLba: 63, count: 600);
        WriteSignature(disk, ebr2);
        logicalBStart = extBase + link + 63;

        return disk;
    }

    // A minimal FAT12 boot sector recognised by FormatIdentifier (BPB + 0x55AA).
    private static void WriteFat12Boot(byte[] disk, int at)
    {
        disk[at + 0x0B] = 0x00; disk[at + 0x0C] = 0x02;   // bytes/sector = 512 (LE)
        disk[at + 0x0D] = 1;                              // sectors/cluster
        disk[at + 0x10] = 2;                              // number of FATs
        disk[at + 0x15] = 0xF0;                           // media descriptor
        disk[at + 0x1FE] = 0x55; disk[at + 0x1FF] = 0xAA;
    }

    // An MBR disk whose first partition actually contains a FAT12 boot sector.
    private static byte[] BuildMbrWithFat12Partition(long partStartLba)
    {
        const int sectors = 4096;
        var disk = new byte[sectors * 512];
        WriteMbrEntry(disk, 0x1BE + 0 * 16, boot: true, type: 0x01, startLba: (uint)partStartLba, count: 1000);
        WriteSignature(disk, 0);
        WriteFat12Boot(disk, (int)(partStartLba * 512));
        return disk;
    }

    private static readonly byte[] EfiSystemGuid =
        Guid.Parse("C12A7328-F81F-11D2-BA4B-00A0C93EC93B").ToByteArray();
    private static readonly byte[] MsBasicDataGuid =
        Guid.Parse("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7").ToByteArray();
    private static readonly byte[] DiskGuidBytes =
        Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray();
    private static readonly byte[] UniqueGuid1 =
        Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE").ToByteArray();
    private static readonly byte[] UniqueGuid2 =
        Guid.Parse("99999999-8888-7777-6666-555555555555").ToByteArray();

    // A protective MBR + GPT header + two entries (EFI System, MS Basic Data).
    private static byte[] BuildGpt()
    {
        const int entrySize = 128;
        const int entryCount = 4;
        const long entriesLba = 2;
        // sectors: 0 pMBR, 1 header, 2.. entries, plus payload room.
        const int sectors = 64;
        var disk = new byte[sectors * 512];

        // Protective MBR with one 0xEE entry.
        WriteMbrEntry(disk, 0x1BE, boot: false, type: 0xEE, startLba: 1, count: (uint)(sectors - 1));
        WriteSignature(disk, 0);

        // GPT header at LBA 1.
        int h = 0x200;
        Encoding.ASCII.GetBytes("EFI PART").CopyTo(disk, h);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(h + 0x08), 0x00010000); // revision
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(h + 0x0C), 92);         // header size
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(h + 0x18), 1);          // current LBA
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(h + 0x20), sectors - 1);// backup LBA
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(h + 0x28), 6);          // first usable
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(h + 0x30), sectors - 2);// last usable
        DiskGuidBytes.CopyTo(disk, h + 0x38);
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(h + 0x48), entriesLba); // entries LBA
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(h + 0x50), entryCount); // count
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(h + 0x54), entrySize);  // entry size

        int e = (int)(entriesLba * 512);
        WriteGptEntry(disk, e + 0 * entrySize, EfiSystemGuid, UniqueGuid1, 8, 15, "EFI");
        WriteGptEntry(disk, e + 1 * entrySize, MsBasicDataGuid, UniqueGuid2, 16, 40, "Windows");
        return disk;
    }

    private static void WriteGptEntry(byte[] disk, int off, byte[] typeGuid, byte[] uniqueGuid,
                                      ulong firstLba, ulong lastLba, string name)
    {
        typeGuid.CopyTo(disk, off + 0x00);
        uniqueGuid.CopyTo(disk, off + 0x10);
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(off + 0x20), firstLba);
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(off + 0x28), lastLba);
        var nb = Encoding.Unicode.GetBytes(name);
        Array.Copy(nb, 0, disk, off + 0x38, Math.Min(nb.Length, 72));
    }

    // A PS2 APA disk with a head + two more partitions in a next-linked chain.
    private static byte[] BuildApa()
    {
        const int sectors = 64;
        var disk = new byte[sectors * 512];
        // head at sector 0 -> next 4 -> next 8 -> 0 (end)
        WriteApaHeader(disk, sector: 0, next: 4, nsector: 2048, type: 0x0000, id: "__mbr");
        WriteApaHeader(disk, sector: 4, next: 8, nsector: 4096, type: 0x0001, id: "__system");
        WriteApaHeader(disk, sector: 8, next: 0, nsector: 8192, type: 0x0100, id: "PP.HDL.GAME");
        return disk;
    }

    private static void WriteApaHeader(byte[] disk, long sector, uint next, uint nsector, uint type, string id)
    {
        int at = (int)(sector * 512);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(at + 0x004), ApaReader.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(at + 0x008), next);
        var idb = Encoding.ASCII.GetBytes(id);
        Array.Copy(idb, 0, disk, at + 0x010, Math.Min(idb.Length, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(at + 0x040), (uint)sector);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(at + 0x044), nsector);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(at + 0x048), type);
    }

    // ======================================================================
    // MBR
    // ======================================================================

    [Fact]
    public void Mbr_parses_two_primaries_with_types_offsets_sizes()
    {
        var mbr = MbrReader.Read(BuildMbrTwoPrimaries());
        Assert.Equal(2, mbr.Partitions.Count);

        var p1 = mbr.Partitions[0];
        Assert.Equal("FAT16", p1.TypeName);
        Assert.True(p1.Bootable);
        Assert.Equal(2048L, p1.StartLba);
        Assert.Equal(2048L * 512, p1.StartByte);
        Assert.Equal(200_000L, p1.SectorCount);
        Assert.Equal(200_000L * 512, p1.SizeBytes);
        Assert.False(p1.IsExtended);
        Assert.False(p1.IsLogical);

        var p2 = mbr.Partitions[1];
        Assert.Equal("Linux", p2.TypeName);
        Assert.False(p2.Bootable);
        Assert.Equal(204_800L, p2.StartLba);
    }

    [Fact]
    public void Mbr_skips_empty_slots()
    {
        // Only one non-empty entry; the other three are zero.
        var disk = new byte[512];
        WriteMbrEntry(disk, 0x1BE, boot: false, type: 0x83, startLba: 100, count: 100);
        WriteSignature(disk, 0);
        var mbr = MbrReader.Read(disk);
        Assert.Single(mbr.Partitions);
    }

    [Fact]
    public void Mbr_missing_signature_throws()
    {
        Assert.Throws<PartitionFormatException>(() => MbrReader.Read(new byte[512]));
    }

    [Fact]
    public void Mbr_enumerates_extended_logical_chain()
    {
        var disk = BuildMbrExtended(2048, 1024, out long aStart, out long bStart);
        var mbr = MbrReader.Read(disk);

        // The container + two logical partitions.
        var container = Assert.Single(mbr.Partitions, p => p.IsExtended);
        Assert.Equal(2048L, container.StartLba);

        var logicals = mbr.Partitions.Where(p => p.IsLogical).ToList();
        Assert.Equal(2, logicals.Count);
        Assert.Equal(aStart, logicals[0].StartLba);
        Assert.Equal("Linux", logicals[0].TypeName);
        Assert.Equal(bStart, logicals[1].StartLba);
        Assert.Equal("FAT16", logicals[1].TypeName);
    }

    [Fact]
    public void Mbr_extended_chain_guards_against_loops()
    {
        // Make EBR #2 link back to EBR #1 (a cycle); the reader must still stop.
        const int sectors = 4096;
        var disk = new byte[sectors * 512];
        WriteMbrEntry(disk, 0x1BE, boot: false, type: 0x0F, startLba: 2048, count: 3000);
        WriteSignature(disk, 0);
        int ebr1 = 2048 * 512;
        WriteMbrEntry(disk, ebr1 + 0x1BE + 0 * 16, false, 0x83, 63, 500);
        WriteMbrEntry(disk, ebr1 + 0x1BE + 1 * 16, false, 0x0F, 1024, 700);
        WriteSignature(disk, ebr1);
        int ebr2 = (2048 + 1024) * 512;
        WriteMbrEntry(disk, ebr2 + 0x1BE + 0 * 16, false, 0x06, 63, 600);
        WriteMbrEntry(disk, ebr2 + 0x1BE + 1 * 16, false, 0x0F, 1024, 700); // points back at EBR #1
        WriteSignature(disk, ebr2);

        var mbr = MbrReader.Read(disk);   // must terminate
        Assert.Equal(2, mbr.Partitions.Count(p => p.IsLogical));
    }

    // ======================================================================
    // GPT
    // ======================================================================

    [Fact]
    public void Gpt_reads_disk_guid_and_two_entries()
    {
        var gpt = GptReader.Read(BuildGpt());
        Assert.Equal("11111111-2222-3333-4444-555555555555", gpt.DiskGuid);
        Assert.Equal(2, gpt.Partitions.Count);
    }

    [Fact]
    public void Gpt_maps_type_names_and_guids()
    {
        var gpt = GptReader.Read(BuildGpt());
        var efi = gpt.Partitions[0];
        Assert.Equal("EFI System", efi.TypeName);
        Assert.Equal("C12A7328-F81F-11D2-BA4B-00A0C93EC93B", efi.TypeGuid);
        Assert.Equal("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", efi.UniqueGuid);

        var data = gpt.Partitions[1];
        Assert.Equal("Microsoft Basic Data", data.TypeName);
        Assert.Equal("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7", data.TypeGuid);
    }

    [Fact]
    public void Gpt_reads_lbas_sizes_and_utf16_names()
    {
        var gpt = GptReader.Read(BuildGpt());
        var efi = gpt.Partitions[0];
        Assert.Equal(8L, efi.FirstLba);
        Assert.Equal(15L, efi.LastLba);
        Assert.Equal(8L * 512, efi.StartByte);
        Assert.Equal((15L - 8 + 1) * 512, efi.SizeBytes);
        Assert.Equal("EFI", efi.Name);
        Assert.Equal("Windows", gpt.Partitions[1].Name);
    }

    [Fact]
    public void Gpt_missing_signature_throws()
    {
        Assert.Throws<PartitionFormatException>(() => GptReader.Read(new byte[4096]));
    }

    // ======================================================================
    // APA (best-effort)
    // ======================================================================

    [Fact]
    public void Apa_walks_next_chain_from_head()
    {
        var apa = ApaReader.Read(BuildApa());
        Assert.Equal(3, apa.Partitions.Count);
        Assert.Equal("__mbr", apa.Partitions[0].Id);
        Assert.Equal("__system", apa.Partitions[1].Id);
        Assert.Equal("PP.HDL.GAME", apa.Partitions[2].Id);
        Assert.Equal(4096L * 512, apa.Partitions[1].SizeBytes);
        Assert.Equal(4L, apa.Partitions[1].StartSector);
    }

    [Fact]
    public void Apa_missing_magic_throws()
    {
        Assert.Throws<PartitionFormatException>(() => ApaReader.Read(new byte[2048]));
    }

    [Fact]
    public void Apa_detects_magic()
    {
        Assert.True(ApaReader.IsApa(BuildApa()));
        Assert.False(ApaReader.IsApa(new byte[2048]));
    }

    // ======================================================================
    // Composed whole-disk reader
    // ======================================================================

    [Fact]
    public void Compose_mbr_reports_fat_filesystem_for_partition()
    {
        using var ms = new MemoryStream(BuildMbrWithFat12Partition(64));
        var disk = PartitionTable.Read(ms);
        Assert.Equal("MBR", disk.Scheme);
        var p1 = Assert.Single(disk.Partitions);
        Assert.StartsWith("FAT", p1.FileSystem);
        Assert.True(p1.Bootable);
        Assert.Equal(64L * 512, p1.StartByte);
    }

    [Fact]
    public void Compose_gpt_detects_scheme_and_guid()
    {
        using var ms = new MemoryStream(BuildGpt());
        var disk = PartitionTable.Read(ms);
        Assert.Equal("GPT", disk.Scheme);
        Assert.Equal("11111111-2222-3333-4444-555555555555", disk.DiskGuid);
        Assert.Equal(2, disk.Partitions.Count);
        Assert.Contains(disk.Partitions, p => p.TypeName.Contains("EFI System"));
    }

    [Fact]
    public void Compose_apa_detects_scheme()
    {
        using var ms = new MemoryStream(BuildApa());
        var disk = PartitionTable.Read(ms);
        Assert.Equal("APA", disk.Scheme);
        Assert.Equal(3, disk.Partitions.Count);
    }

    [Fact]
    public void Compose_unpartitioned_throws()
    {
        Assert.Throws<PartitionFormatException>(() => PartitionTable.Read(new MemoryStream(new byte[4096])));
    }

    [Fact]
    public void Compose_is_safe_on_short_stream_claiming_large_partition()
    {
        // Partition claims a huge size but the image is tiny — detection must not crash.
        var disk = new byte[512];
        WriteMbrEntry(disk, 0x1BE, boot: false, type: 0x83, startLba: 1, count: 0xFFFFFFF0);
        WriteSignature(disk, 0);
        using var ms = new MemoryStream(disk);
        var image = PartitionTable.Read(ms);
        var p = Assert.Single(image.Partitions);
        Assert.Equal("unknown", p.FileSystem);   // nothing to peek beyond the MBR
    }

    // ======================================================================
    // FormatIdentifier hook
    // ======================================================================

    [Fact]
    public void FormatIdentifier_names_gpt()
    {
        Assert.Equal("GPT", FormatIdentifier.Identify(BuildGpt()).Name);
    }

    [Fact]
    public void FormatIdentifier_names_mbr()
    {
        Assert.Equal("MBR", FormatIdentifier.Identify(BuildMbrTwoPrimaries()).Name);
    }

    [Fact]
    public void FormatIdentifier_does_not_call_bare_fat12_floppy_an_mbr()
    {
        // A FAT12 boot sector has 0x55AA but no partition entries — must stay FAT12.
        var disk = new byte[512];
        WriteFat12Boot(disk, 0);
        Assert.Equal("FAT12", FormatIdentifier.Identify(disk).Name);
    }

    [Fact]
    public void FormatIdentifier_does_not_call_plain_0x55aa_buffer_an_mbr()
    {
        // 0x55AA present but no plausible partition entry — not an MBR.
        var disk = new byte[512];
        WriteSignature(disk, 0);
        Assert.NotEqual("MBR", FormatIdentifier.Identify(disk).Name);
    }
}
