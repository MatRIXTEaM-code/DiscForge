// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Udf;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// End-to-end browse of a UDF 2.50 volume that uses a Blu-ray-style <b>metadata
/// partition</b> — the indirection where the File Set and the whole directory tree
/// live inside a Metadata File addressed through a Type 2 partition map. Earlier
/// tests exercise <see cref="UdfMetadataPartition"/>'s parse/translate in isolation;
/// this builds a complete, spec-correct image (anchor → volume descriptor sequence →
/// LVD carrying the metadata map → Metadata File extents → File Set → directory tree)
/// and drives it through the real <see cref="UdfReader"/>, so the whole resolver path
/// — including a file whose data extent is reached *through* the metadata mapping —
/// is validated without needing a 25 GB pressed disc.
/// </summary>
public class UdfBluRayBrowseTests
{
    private const int SS = 2048;

    // Physical layout (in 2048-byte sectors).
    private const int Anchor = 256, Vds = 272, Pvd = 272, Pd = 273, Lvd = 274, Term = 275;
    private const uint PartStart = 300, PartLen = 200;
    private const uint MetaFileBlock = 5;          // Metadata File Entry at PartStart+5 = 305
    private const uint MetaExtentPos = 10, MetaExtentBlocks = 10; // meta logical L -> 310+L

    // Metadata-partition logical blocks for the directory structure.
    private const uint FsdBlk = 0;      // -> phys 310
    private const uint RootBlk = 1;     // -> phys 311
    private const uint HelloFeBlk = 3;  // -> phys 313
    private const uint HelloDataBlk = 4;// -> phys 314
    private const uint DirFeBlk = 5;    // -> phys 315
    private const uint InsideFeBlk = 6; // -> phys 316

    private const string HelloText = "Hello, Blu-ray!\n";   // 16 bytes, reached via an extent
    private const string InsideText = "inside file\n";       // 12 bytes, embedded (ad_type 3)

    private static void U16(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
    private static void U32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
    private static void U64(byte[] b, int at, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), v);

    // Write a 16-byte descriptor tag with a correct tag checksum (bytes 0-3 and
    // 5-15 sum to byte 4). The reader validates this on the anchor and every VDS
    // descriptor, skipping any that fail.
    private static void WriteTag(byte[] img, int sector, ushort tagId)
    {
        int b = sector * SS;
        U16(img, b, tagId);
        U16(img, b + 2, 3);            // descriptor version
        U32(img, b + 12, (uint)sector);// tag location
        int sum = 0;
        for (int i = 0; i < 4; i++) sum += img[b + i];
        for (int i = 5; i < 16; i++) sum += img[b + i];
        img[b + 4] = (byte)(sum & 0xFF);
    }

    // A File Identifier Descriptor. name="" marks the parent entry.
    private static byte[] Fid(string name, byte characteristics, uint icbBlock)
    {
        int nameLen = name.Length == 0 ? 0 : 1 + name.Length; // OSTA: 1 compression byte + chars
        int total = 38 + nameLen;
        int pad = (4 - (total % 4)) % 4;
        var f = new byte[total + pad];
        U16(f, 0, 257);                // FID tag id (reader checks only this)
        f[18] = characteristics;
        f[19] = (byte)nameLen;
        U32(f, 24, icbBlock);          // ICB long_ad location (lba)
        U16(f, 36, 0);                 // length of implementation use
        if (nameLen > 0)
        {
            f[38] = 8;                 // OSTA compression id 8 (8-bit)
            for (int i = 0; i < name.Length; i++) f[39 + i] = (byte)name[i];
        }
        return f;
    }

    private static byte[] BuildImage()
    {
        var img = new byte[512 * SS];

        // --- Anchor Volume Descriptor Pointer (sector 256) ---
        WriteTag(img, Anchor, 2);
        U32(img, Anchor * SS + 16, (uint)(8 * SS)); // main VDS length (bytes)
        U32(img, Anchor * SS + 20, Vds);            // main VDS location

        // --- Primary Volume Descriptor (label) ---
        WriteTag(img, Pvd, 1);
        int pvd = Pvd * SS;
        // dstring volume id: [compression=8]["BDVOLUME"] ... [len at last byte]
        img[pvd + 24] = 8;
        Encoding.ASCII.GetBytes("BDVOLUME").CopyTo(img, pvd + 25);
        img[pvd + 24 + 31] = 9;        // dstring length = 1 + 8

        // --- Partition Descriptor ---
        WriteTag(img, Pd, 5);
        U32(img, Pd * SS + 188, PartStart);
        U32(img, Pd * SS + 192, PartLen);

        // --- Logical Volume Descriptor (block size, file-set pointer, partition maps) ---
        WriteTag(img, Lvd, 6);
        int lvd = Lvd * SS;
        U32(img, lvd + 212, SS);                 // logical block size
        // logicalVolumeContentsUse = long_ad to the File Set: lba at +252.
        U32(img, lvd + 248, SS);                 // extent length
        U32(img, lvd + 252, FsdBlk);             // File Set logical block (metadata partition)
        U32(img, lvd + 432, 2);                  // number of partition maps
        // Type 1 map @440: [1][6][volSeq(2)][partNum(2)]
        int m = lvd + 440;
        img[m] = 1; img[m + 1] = 6; U16(img, m + 4, 0);
        // Type 2 metadata partition map @446: [2][64][flags][ident...]
        int m2 = m + 6;
        img[m2] = 2; img[m2 + 1] = 64;
        img[m2 + 4] = 0;                         // identifier flags byte
        Encoding.ASCII.GetBytes("*UDF Metadata Partition").CopyTo(img, m2 + 5);
        U16(img, m2 + 36, 1);                    // volume sequence number
        U16(img, m2 + 38, 1);                    // partition number
        U32(img, m2 + 40, MetaFileBlock);        // metadata file location (partition block)
        U32(img, m2 + 44, MetaFileBlock + 1);    // mirror file location

        // --- Terminating Descriptor ---
        WriteTag(img, Term, 8);

        // --- Metadata File Entry (physical partition, block 5 -> sector 305) ---
        int mfe = (int)(PartStart + MetaFileBlock) * SS;
        U16(img, mfe, 261);                      // File Entry tag (no checksum needed here)
        U16(img, mfe + 16 + 18, 0);              // ICB flags: adType 0 (short_ad)
        U32(img, mfe + 0xA8, 0);                 // length of EA
        U32(img, mfe + 0xAC, 8);                 // length of AD (one short_ad)
        U32(img, mfe + 0xB0, MetaExtentBlocks * SS); // extent length (bytes), type 0
        U32(img, mfe + 0xB4, MetaExtentPos);     // extent position (partition block 10)

        // --- File Set Descriptor (metadata logical 0 -> sector 310) ---
        int fsd = (int)MetaPhys(FsdBlk) * SS;
        U16(img, fsd, 256);
        U32(img, fsd + 400 + 4, RootBlk);        // root directory ICB location (lba)

        // --- Root directory File Entry (metadata logical 1 -> 311), embedded FIDs ---
        var rootFids = Concat(
            Fid("", 0x0A, RootBlk),              // parent (dir+parent)
            Fid("HELLO.TXT", 0x00, HelloFeBlk),  // file
            Fid("DIR", 0x02, DirFeBlk));         // subdirectory
        WriteDir(img, MetaPhys(RootBlk), rootFids);

        // --- HELLO.TXT File Entry (metadata logical 3 -> 313), data via an extent ---
        var hello = Encoding.ASCII.GetBytes(HelloText);
        int hfe = (int)MetaPhys(HelloFeBlk) * SS;
        U16(img, hfe, 261);
        img[hfe + 16 + 11] = 5;                  // file type: regular file
        U16(img, hfe + 16 + 18, 0);              // adType 0 (short_ad)
        U64(img, hfe + 0x38, (ulong)hello.Length);
        U32(img, hfe + 0xA8, 0);
        U32(img, hfe + 0xAC, 8);
        U32(img, hfe + 0xB0, (uint)hello.Length);// extent length (type 0 recorded)
        U32(img, hfe + 0xB4, HelloDataBlk);      // extent position (metadata logical 4)
        hello.CopyTo(img, (int)MetaPhys(HelloDataBlk) * SS);

        // --- DIR File Entry (metadata logical 5 -> 315), embedded FIDs ---
        var dirFids = Concat(
            Fid("", 0x0A, DirFeBlk),
            Fid("INSIDE.TXT", 0x00, InsideFeBlk));
        WriteDir(img, MetaPhys(DirFeBlk), dirFids);

        // --- INSIDE.TXT File Entry (metadata logical 6 -> 316), embedded data ---
        var inside = Encoding.ASCII.GetBytes(InsideText);
        int ife = (int)MetaPhys(InsideFeBlk) * SS;
        U16(img, ife, 261);
        img[ife + 16 + 11] = 5;                  // file type: regular file
        U16(img, ife + 16 + 18, 3);              // adType 3 (data embedded in the FE)
        U64(img, ife + 0x38, (ulong)inside.Length);
        U32(img, ife + 0xA8, 0);
        U32(img, ife + 0xAC, (uint)inside.Length);
        inside.CopyTo(img, ife + 0xB0);

        return img;
    }

    // A directory File Entry with its FIDs embedded (ICB ad_type 3).
    private static void WriteDir(byte[] img, uint sector, byte[] fids)
    {
        int b = (int)sector * SS;
        U16(img, b, 261);
        img[b + 16 + 11] = 4;                    // file type: directory
        U16(img, b + 16 + 18, 3);                // adType 3 (embedded)
        U64(img, b + 0x38, (ulong)fids.Length);  // size = embedded data length
        U32(img, b + 0xA8, 0);                   // length of EA
        U32(img, b + 0xAC, (uint)fids.Length);   // length of AD (= embedded bytes)
        fids.CopyTo(img, b + 0xB0);
    }

    private static uint MetaPhys(uint logical) => PartStart + MetaExtentPos + logical;

    private static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    private static string Extract(byte[] image, UdfVolume vol, UdfEntry e)
    {
        using var ms = new MemoryStream();
        using var s = new MemoryStream(image, writable: false);
        UdfReader.ExtractFile(s, vol, e, ms);
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    [Fact]
    public void BrowsesMetadataPartition_EndToEnd()
    {
        var image = BuildImage();
        using var s = new MemoryStream(image, writable: false);

        Assert.True(UdfReader.IsUdf(s));
        var vol = UdfReader.Read(s);

        Assert.Equal("BDVOLUME", vol.VolumeId);
        Assert.Equal(PartStart, vol.PartitionStart);

        var hello = Assert.Single(vol.Entries, e => e.Path == "/HELLO.TXT");
        Assert.False(hello.IsDirectory);
        Assert.Equal(HelloText.Length, hello.Size);

        var dir = Assert.Single(vol.Entries, e => e.Path == "/DIR");
        Assert.True(dir.IsDirectory);

        var inside = Assert.Single(vol.Entries, e => e.Path == "/DIR/INSIDE.TXT");
        Assert.False(inside.IsDirectory);
        Assert.Equal(InsideText.Length, inside.Size);
    }

    [Fact]
    public void ExtractsFileData_ThroughMetadataMapping()
    {
        var image = BuildImage();
        using var s = new MemoryStream(image, writable: false);
        var vol = UdfReader.Read(s);

        // HELLO.TXT's bytes are reached via an allocation extent whose block is
        // resolved through the Metadata File — the real Blu-ray content path.
        var hello = Assert.Single(vol.Entries, e => e.Path == "/HELLO.TXT");
        Assert.Equal(HelloText, Extract(image, vol, hello));

        // INSIDE.TXT is embedded in its File Entry (ICB ad_type 3).
        var inside = Assert.Single(vol.Entries, e => e.Path == "/DIR/INSIDE.TXT");
        Assert.Equal(InsideText, Extract(image, vol, inside));
    }
}
