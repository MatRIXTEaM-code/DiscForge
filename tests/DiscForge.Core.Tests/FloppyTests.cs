// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Floppy;
using DiscForge.Core.Identify;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Floppy-image readers (D64, FAT12, ADF). There is no external tool to produce
/// fixtures, so each image is built by hand in-test with a couple of known files
/// (including a multi-block/multi-cluster file) and read back — asserting the
/// disk/volume name, the file listing, and byte-exact extraction.
/// </summary>
public class FloppyTests
{
    // ======================================================================
    // D64
    // ======================================================================

    private static byte[] BuildD64(byte[] prg, byte[] seq)
    {
        var img = new byte[D64Reader.Size35];

        int bam = D64Reader.Offset(18, 0);
        img[bam + 0] = 18;   // link to first directory sector
        img[bam + 1] = 1;
        WritePetscii(img, bam + 0x90, "TEST DISK", 16);
        img[bam + 0xA2] = (byte)'4';
        img[bam + 0xA3] = (byte)'2';

        // Directory block at track 18 sector 1.
        int dir = D64Reader.Offset(18, 1);
        img[dir + 0] = 0;    // no next dir block
        img[dir + 1] = 0xFF;

        // Entry 0: PRG "HELLO", first block track 17 sector 0, 2 blocks.
        int e0 = dir + 0 * 32;
        img[e0 + 0x02] = 0x82;   // closed | PRG
        img[e0 + 0x03] = 17;
        img[e0 + 0x04] = 0;
        WritePetscii(img, e0 + 0x05, "HELLO", 16);
        img[e0 + 0x1E] = 2; img[e0 + 0x1F] = 0;

        // Entry 1: SEQ "DATA", first block track 19 sector 0, 1 block.
        int e1 = dir + 1 * 32;
        img[e1 + 0x02] = 0x81;   // closed | SEQ
        img[e1 + 0x03] = 19;
        img[e1 + 0x04] = 0;
        WritePetscii(img, e1 + 0x05, "DATA", 16);
        img[e1 + 0x1E] = 1; img[e1 + 0x1F] = 0;

        // PRG data: 254 bytes in block (17,0) linking to (17,1), then the rest.
        WriteD64Chain(img, 17, 0, prg);
        // SEQ data: a single (partial) block at (19,0).
        WriteD64Chain(img, 19, 0, seq);

        return img;
    }

    // Lay a file down as a track/sector chain starting at (track,sector), using
    // consecutive sectors on the same track. Blocks: 2-byte link + 254 data bytes;
    // the last block's link is 0 / (bytesUsed + 1).
    private static void WriteD64Chain(byte[] img, int track, int sector, byte[] content)
    {
        int pos = 0;
        while (true)
        {
            int off = D64Reader.Offset(track, sector);
            int remaining = content.Length - pos;
            if (remaining <= 254)
            {
                img[off + 0] = 0;
                img[off + 1] = (byte)(remaining + 1);   // pointer to last used byte
                Array.Copy(content, pos, img, off + 2, remaining);
                return;
            }
            Array.Copy(content, pos, img, off + 2, 254);
            pos += 254;
            img[off + 0] = (byte)track;
            img[off + 1] = (byte)(sector + 1);
            sector++;
        }
    }

    [Fact]
    public void D64_reads_disk_name_and_id()
    {
        var disk = D64Reader.Read(BuildD64(Pattern(300), Pattern(20)));
        Assert.Equal("TEST DISK", disk.DiskName);
        Assert.Equal("42", disk.DiskId);
        Assert.Equal(35, disk.Tracks);
    }

    [Fact]
    public void D64_lists_files_with_types_and_sizes()
    {
        var disk = D64Reader.Read(BuildD64(Pattern(300), Pattern(20)));
        Assert.Equal(2, disk.Files.Count);

        var prg = Assert.Single(disk.Files, f => f.Name == "HELLO");
        Assert.Equal(D64FileType.Prg, prg.Type);
        Assert.True(prg.Closed);
        Assert.Equal(2, prg.SizeBlocks);
        Assert.Equal(17, prg.FirstTrack);

        var seq = Assert.Single(disk.Files, f => f.Name == "DATA");
        Assert.Equal(D64FileType.Seq, seq.Type);
        Assert.Equal(1, seq.SizeBlocks);
    }

    [Fact]
    public void D64_extracts_multi_block_file_with_partial_last_block()
    {
        var prg = Pattern(300);   // spans two blocks (254 + 46)
        var disk = D64Reader.Read(BuildD64(prg, Pattern(20)));
        var entry = Assert.Single(disk.Files, f => f.Name == "HELLO");
        Assert.Equal(prg, D64Reader.ExtractFile(BuildD64(prg, Pattern(20)), entry));
    }

    [Fact]
    public void D64_extracts_single_block_file()
    {
        var seq = Pattern(20);
        var img = BuildD64(Pattern(300), seq);
        var disk = D64Reader.Read(img);
        var entry = Assert.Single(disk.Files, f => f.Name == "DATA");
        Assert.Equal(seq, D64Reader.ExtractFile(img, entry));
    }

    [Fact]
    public void D64_IsD64_accepts_valid_sizes_and_rejects_others()
    {
        Assert.True(D64Reader.IsD64(D64Reader.Size35));
        Assert.True(D64Reader.IsD64(D64Reader.Size40));
        Assert.False(D64Reader.IsD64(174847));
        Assert.False(D64Reader.IsD64(new byte[1024]));
    }

    // ======================================================================
    // FAT12
    // ======================================================================

    private static byte[] BuildFat12(byte[] readme, byte[] inner)
    {
        const int bytesPerSector = 512;
        const int totalSectors = 2880;   // 1.44 MB
        var img = new byte[totalSectors * bytesPerSector];

        // BPB.
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0B), bytesPerSector);
        img[0x0D] = 1;    // sectors/cluster
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0E), 1);   // reserved
        img[0x10] = 2;    // FATs
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x11), 224); // root entries
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x13), totalSectors);
        img[0x15] = 0xF0; // media
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x16), 9);   // sectors/FAT
        img[0x1FE] = 0x55; img[0x1FF] = 0xAA;

        int reserved = 1, numFats = 2, sectorsPerFat = 9, rootEntries = 224;
        int fatStart = reserved;
        int rootStart = reserved + numFats * sectorsPerFat;               // 19
        int rootSectors = (rootEntries * 32 + bytesPerSector - 1) / bytesPerSector; // 14
        int dataStart = rootStart + rootSectors;                          // 33

        int Cluster(int n) => (dataStart + (n - 2)) * bytesPerSector;
        int fatBase = fatStart * bytesPerSector;

        void SetFat(int cluster, int value)
        {
            int k = fatBase + cluster * 3 / 2;
            if ((cluster & 1) == 0)
            {
                img[k] = (byte)(value & 0xFF);
                img[k + 1] = (byte)((img[k + 1] & 0xF0) | ((value >> 8) & 0x0F));
            }
            else
            {
                img[k] = (byte)((img[k] & 0x0F) | ((value << 4) & 0xF0));
                img[k + 1] = (byte)((value >> 4) & 0xFF);
            }
        }

        // Reserved FAT entries + chains. README = clusters 2,3; SUBDIR = 4; INNER = 5.
        SetFat(0, 0xFF0); SetFat(1, 0xFFF);
        SetFat(2, 3); SetFat(3, 0xFFF);
        SetFat(4, 0xFFF);
        SetFat(5, 0xFFF);

        // Root directory entries.
        int root = rootStart * bytesPerSector;
        WriteDirEntry(img, root + 0 * 32, "MYVOLUME", "", 0x08, 0, 0);
        WriteDirEntry(img, root + 1 * 32, "README", "TXT", 0x20, 2, (uint)readme.Length);
        WriteDirEntry(img, root + 2 * 32, "SUBDIR", "", 0x10, 4, 0);

        // README data across clusters 2 and 3.
        Array.Copy(readme, 0, img, Cluster(2), Math.Min(readme.Length, 512));
        if (readme.Length > 512)
            Array.Copy(readme, 512, img, Cluster(3), readme.Length - 512);

        // SUBDIR cluster (cluster 4): '.', '..', INNER.TXT.
        int sub = Cluster(4);
        WriteDirEntry(img, sub + 0 * 32, ".", "", 0x10, 4, 0);
        WriteDirEntry(img, sub + 1 * 32, "..", "", 0x10, 0, 0);
        WriteDirEntry(img, sub + 2 * 32, "INNER", "TXT", 0x20, 5, (uint)inner.Length);

        // INNER data (cluster 5).
        Array.Copy(inner, 0, img, Cluster(5), inner.Length);

        return img;
    }

    private static void WriteDirEntry(byte[] img, int off, string name, string ext, byte attr, int firstCluster, uint size)
    {
        for (int i = 0; i < 11; i++) img[off + i] = (byte)' ';
        var nb = Encoding.ASCII.GetBytes(name);
        Array.Copy(nb, 0, img, off, Math.Min(nb.Length, 8));
        var eb = Encoding.ASCII.GetBytes(ext);
        Array.Copy(eb, 0, img, off + 8, Math.Min(eb.Length, 3));
        img[off + 0x0B] = attr;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(off + 0x1A), (ushort)firstCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 0x1C), size);
    }

    [Fact]
    public void Fat12_reads_volume_label()
    {
        var disk = Fat12Reader.Read(BuildFat12(Pattern(600), Pattern(100)));
        Assert.Equal("MYVOLUME", disk.VolumeLabel);
    }

    [Fact]
    public void Fat12_walks_tree_including_subdirectory()
    {
        var disk = Fat12Reader.Read(BuildFat12(Pattern(600), Pattern(100)));
        Assert.Contains(disk.Entries, e => e.Path == "/README.TXT" && !e.IsDirectory && e.Size == 600);
        Assert.Contains(disk.Entries, e => e.Path == "/SUBDIR" && e.IsDirectory);
        Assert.Contains(disk.Entries, e => e.Path == "/SUBDIR/INNER.TXT" && !e.IsDirectory && e.Size == 100);
    }

    [Fact]
    public void Fat12_extracts_multi_cluster_file()
    {
        var readme = Pattern(600);   // spans two 512-byte clusters
        var img = BuildFat12(readme, Pattern(100));
        var disk = Fat12Reader.Read(img);
        var entry = Assert.Single(disk.Entries, e => e.Path == "/README.TXT");
        Assert.Equal(readme, Fat12Reader.ExtractFile(img, entry));
    }

    [Fact]
    public void Fat12_extracts_file_inside_subdirectory()
    {
        var inner = Pattern(100);
        var img = BuildFat12(Pattern(600), inner);
        var disk = Fat12Reader.Read(img);
        var entry = Assert.Single(disk.Entries, e => e.Path == "/SUBDIR/INNER.TXT");
        Assert.Equal(inner, Fat12Reader.ExtractFile(img, entry));
    }

    [Fact]
    public void Fat12_IsFat12_positive_and_negative()
    {
        Assert.True(Fat12Reader.IsFat12(BuildFat12(Pattern(600), Pattern(100))));
        Assert.False(Fat12Reader.IsFat12(new byte[512]));            // no 0x55AA
        var bad = BuildFat12(Pattern(600), Pattern(100));
        bad[0x0C] = 0x00;                                            // bytes/sector no longer 512
        Assert.False(Fat12Reader.IsFat12(bad));
    }

    // ======================================================================
    // ADF (OFS + FFS)
    // ======================================================================

    private static byte[] BuildAdf(bool ffs, byte[] hello, byte[] inner)
    {
        var b = new AdfBuild(ffs);
        const int root = 880;
        b.InitRoot(root, "TESTDISK");

        // File HELLO in root, spanning two data blocks (884, 885).
        int[] helloData = { 884, 885 };
        b.WriteFileHeader(882, root, "HELLO", helloData, hello.Length);
        b.WriteData(884, 882, 1, Slice(hello, 0, b.PayloadCap), helloData.Length > 1 ? 885 : 0);
        if (hello.Length > b.PayloadCap)
            b.WriteData(885, 882, 2, Slice(hello, b.PayloadCap, hello.Length - b.PayloadCap), 0);

        // Directory MYDIR in root, containing file INNER (one data block, 894).
        b.InitDirHeader(890, root, "MYDIR");
        b.WriteFileHeader(892, 890, "INNER", new[] { 894 }, inner.Length);
        b.WriteData(894, 892, 1, inner, 0);

        b.LinkIntoDir(root, 882, "HELLO");
        b.LinkIntoDir(root, 890, "MYDIR");
        b.LinkIntoDir(890, 892, "INNER");

        return b.Data;
    }

    // Small in-test AmigaDOS block writer (canonical big-endian offsets).
    private sealed class AdfBuild
    {
        public byte[] Data { get; } = new byte[AdfReader.DdSize];
        private readonly bool _ffs;
        public int PayloadCap => _ffs ? 512 : 488;

        public AdfBuild(bool ffs)
        {
            _ffs = ffs;
            Data[0] = (byte)'D'; Data[1] = (byte)'O'; Data[2] = (byte)'S';
            Data[3] = (byte)(ffs ? 1 : 0);
        }

        private void Int(int block, int off, int val) =>
            BinaryPrimitives.WriteInt32BigEndian(Data.AsSpan(block * 512 + off), val);

        private void Name(int block, int off, string name)
        {
            int a = block * 512 + off;
            Data[a] = (byte)name.Length;
            for (int i = 0; i < name.Length; i++) Data[a + 1 + i] = (byte)name[i];
        }

        public void InitRoot(int block, string diskName)
        {
            Int(block, 0x000, 2);       // T_HEADER
            Name(block, 0x1B0, diskName);
            Int(block, 0x1FC, 1);       // ST_ROOT
        }

        public void InitDirHeader(int block, int parent, string name)
        {
            Int(block, 0x000, 2);
            Int(block, 0x004, block);
            Name(block, 0x1B0, name);
            Int(block, 0x1F8, parent);
            Int(block, 0x1FC, 2);       // ST_USERDIR
        }

        public void WriteFileHeader(int block, int parent, string name, int[] dataBlocks, int byteSize)
        {
            Int(block, 0x000, 2);
            Int(block, 0x004, block);
            Int(block, 0x008, dataBlocks.Length);            // high_seq
            Int(block, 0x010, dataBlocks.Length > 0 ? dataBlocks[0] : 0);  // first_data
            for (int i = 0; i < dataBlocks.Length; i++)
                Int(block, 0x134 - i * 4, dataBlocks[i]);    // pointers high → low
            Int(block, 0x144, byteSize);                     // byte_size
            Name(block, 0x1B0, name);
            Int(block, 0x1F8, parent);
            Int(block, 0x1FC, -3);      // ST_FILE
        }

        public void WriteData(int block, int headerKey, int seq, byte[] chunk, int nextData)
        {
            if (_ffs)
            {
                Array.Copy(chunk, 0, Data, block * 512, chunk.Length);
            }
            else
            {
                Int(block, 0x000, 8);           // T_DATA
                Int(block, 0x004, headerKey);
                Int(block, 0x008, seq);
                Int(block, 0x00C, chunk.Length);
                Int(block, 0x010, nextData);
                Array.Copy(chunk, 0, Data, block * 512 + 24, chunk.Length);
            }
        }

        public void LinkIntoDir(int dirBlock, int childBlock, string childName)
        {
            int slot = AdfHash(childName);
            int at = dirBlock * 512 + 0x18 + slot * 4;
            int existing = BinaryPrimitives.ReadInt32BigEndian(Data.AsSpan(at));
            if (existing == 0)
            {
                BinaryPrimitives.WriteInt32BigEndian(Data.AsSpan(at), childBlock);
            }
            else
            {
                int cur = existing;
                while (true)
                {
                    int nxt = BinaryPrimitives.ReadInt32BigEndian(Data.AsSpan(cur * 512 + 0x1F0));
                    if (nxt == 0) break;
                    cur = nxt;
                }
                BinaryPrimitives.WriteInt32BigEndian(Data.AsSpan(cur * 512 + 0x1F0), childBlock);
            }
        }
    }

    private static int AdfHash(string name)
    {
        uint hash = (uint)name.Length;
        foreach (char c in name)
        {
            char u = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
            hash = (hash * 13 + (byte)u) & 0x7FF;
        }
        return (int)(hash % 72);
    }

    [Fact]
    public void Adf_ofs_reads_disk_name_and_flag()
    {
        var disk = AdfReader.Read(BuildAdf(false, Pattern(600), Pattern(200)));
        Assert.Equal("TESTDISK", disk.DiskName);
        Assert.False(disk.Ffs);
    }

    [Fact]
    public void Adf_ffs_reads_disk_name_and_flag()
    {
        var disk = AdfReader.Read(BuildAdf(true, Pattern(600), Pattern(200)));
        Assert.Equal("TESTDISK", disk.DiskName);
        Assert.True(disk.Ffs);
    }

    [Fact]
    public void Adf_ofs_lists_tree_including_subdirectory()
    {
        var disk = AdfReader.Read(BuildAdf(false, Pattern(600), Pattern(200)));
        Assert.Contains(disk.Entries, e => e.Path == "/HELLO" && !e.IsDirectory && e.Size == 600);
        Assert.Contains(disk.Entries, e => e.Path == "/MYDIR" && e.IsDirectory);
        Assert.Contains(disk.Entries, e => e.Path == "/MYDIR/INNER" && !e.IsDirectory && e.Size == 200);
    }

    [Fact]
    public void Adf_ofs_extracts_multi_block_file()
    {
        var hello = Pattern(600);   // spans two OFS data blocks (488 + 112)
        var img = BuildAdf(false, hello, Pattern(200));
        var disk = AdfReader.Read(img);
        var entry = Assert.Single(disk.Entries, e => e.Path == "/HELLO");
        Assert.Equal(hello, AdfReader.ExtractFile(img, entry));
    }

    [Fact]
    public void Adf_ffs_extracts_multi_block_file()
    {
        var hello = Pattern(600);   // spans two FFS data blocks (512 + 88)
        var img = BuildAdf(true, hello, Pattern(200));
        var disk = AdfReader.Read(img);
        var entry = Assert.Single(disk.Entries, e => e.Path == "/HELLO");
        Assert.Equal(hello, AdfReader.ExtractFile(img, entry));
    }

    [Fact]
    public void Adf_extracts_file_inside_subdirectory()
    {
        var inner = Pattern(200);
        var img = BuildAdf(false, Pattern(600), inner);
        var disk = AdfReader.Read(img);
        var entry = Assert.Single(disk.Entries, e => e.Path == "/MYDIR/INNER");
        Assert.Equal(inner, AdfReader.ExtractFile(img, entry));
    }

    [Fact]
    public void Adf_IsAdf_positive_and_negative()
    {
        Assert.True(AdfReader.IsAdf(BuildAdf(false, Pattern(600), Pattern(200))));
        Assert.False(AdfReader.IsAdf(new byte[AdfReader.DdSize]));   // right size, no "DOS"
        var wrongSize = new byte[1024];
        wrongSize[0] = (byte)'D'; wrongSize[1] = (byte)'O'; wrongSize[2] = (byte)'S';
        Assert.False(AdfReader.IsAdf(wrongSize));
    }

    // ======================================================================
    // FormatIdentifier hook
    // ======================================================================

    [Fact]
    public void FormatIdentifier_names_d64()
    {
        Assert.Equal("D64", FormatIdentifier.Identify(BuildD64(Pattern(300), Pattern(20))).Name);
    }

    [Fact]
    public void FormatIdentifier_names_fat12()
    {
        Assert.Equal("FAT12", FormatIdentifier.Identify(BuildFat12(Pattern(600), Pattern(100))).Name);
    }

    [Fact]
    public void FormatIdentifier_names_adf()
    {
        Assert.Equal("ADF", FormatIdentifier.Identify(BuildAdf(false, Pattern(600), Pattern(200))).Name);
    }

    [Fact]
    public void FormatIdentifier_does_not_misname_blank_d64_sized_buffer()
    {
        // Right size but no valid BAM link — must not be claimed as D64.
        Assert.NotEqual("D64", FormatIdentifier.Identify(new byte[D64Reader.Size35]).Name);
    }

    // ======================================================================
    // helpers
    // ======================================================================

    private static byte[] Pattern(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((i * 7 + 3) & 0xFF);
        return b;
    }

    private static byte[] Slice(byte[] src, int start, int len)
    {
        var b = new byte[len];
        Array.Copy(src, start, b, 0, len);
        return b;
    }

    private static void WritePetscii(byte[] img, int at, string s, int len)
    {
        for (int i = 0; i < len; i++)
            img[at + i] = i < s.Length ? (byte)s[i] : (byte)0xA0;
    }
}
