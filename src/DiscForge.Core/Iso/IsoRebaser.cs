// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.Iso;

/// <summary>
/// Rebases an ISO 9660 image's logical block addresses by a fixed offset — the
/// "ISO LBA Fix" a Dreamcast GD-ROM needs. A GD-ROM's game filesystem lives in
/// the high-density area, which begins at LBA 45000, and its ISO is authored so
/// every extent reference is based there. A normal ISO builder produces a
/// zero-based image; adding a constant to every stored LBA turns it into one the
/// GD-ROM (and a base-LBA reader) will follow.
///
/// The physical byte layout is not moved — the image still starts at byte zero,
/// the descriptors still sit at sector 16. Only the *stored* address fields
/// change: the root directory records in the PVD (and Joliet SVD), every
/// directory record's extent, and the path table pointers and their records.
/// That is precisely the set a reader resolves, so a rebased image reads back
/// identically once the reader is told the base.
/// </summary>
public static class IsoRebaser
{
    private const int SectorSize = 2048;

    /// <summary>Return a copy of an ISO image with every stored LBA increased by
    /// <paramref name="baseLba"/>. The input is not modified.</summary>
    public static byte[] Rebase(byte[] iso, long baseLba)
    {
        ArgumentNullException.ThrowIfNull(iso);
        if (baseLba < 0) throw new ArgumentOutOfRangeException(nameof(baseLba));
        if (baseLba == 0) return (byte[])iso.Clone();

        var img = (byte[])iso.Clone();
        uint add = checked((uint)baseLba);

        // Enumerate every directory (both hierarchies) from the *original* image,
        // whose zero-based addresses still describe where the data physically is.
        var iso9660Dirs = CollectDirectories(iso, IsoReader.NamePreference.Iso9660, out byte[]? pvd, out int pvdSector);
        List<(uint Extent, uint Size)> jolietDirs = new();
        byte[]? svd = null; int svdSector = -1;
        if (HasJoliet(iso))
            jolietDirs = CollectDirectories(iso, IsoReader.NamePreference.Joliet, out svd, out svdSector);

        // Patch each directory's records: the extent field lives at record+2 (LE).
        foreach (var (extent, size) in iso9660Dirs.Concat(jolietDirs))
            PatchDirectoryRecords(img, extent, size, add);

        // The root directory records inside the descriptors themselves.
        if (pvdSector >= 0) PatchDescriptorRoot(img, pvdSector, add);
        if (svdSector >= 0) PatchDescriptorRoot(img, svdSector, add);

        // Path tables: pointers in the descriptors, and the records they point to.
        if (pvdSector >= 0) PatchPathTables(img, pvdSector, add);
        if (svdSector >= 0) PatchPathTables(img, svdSector, add);

        return img;
    }

    // ---- directory enumeration ---------------------------------------------

    private static List<(uint Extent, uint Size)> CollectDirectories(
        byte[] iso, IsoReader.NamePreference prefer, out byte[]? descriptor, out int descriptorSector)
    {
        descriptor = null;
        descriptorSector = -1;

        // Locate the descriptor and its root directory extent.
        byte type = prefer == IsoReader.NamePreference.Joliet ? (byte)2 : (byte)1;
        for (int lba = 16; lba <= 100; lba++)
        {
            long off = (long)lba * SectorSize;
            if (off + SectorSize > iso.Length) break;
            if (iso[off + 1] == (byte)'C' && iso[off + 2] == (byte)'D' && iso[off + 3] == (byte)'0'
                && iso[off + 4] == (byte)'0' && iso[off + 5] == (byte)'1')
            {
                if (iso[off] == 0xFF) break;
                if (iso[off] == type) { descriptor = iso[(int)off..(int)(off + SectorSize)]; descriptorSector = lba; break; }
            }
        }

        var dirs = new List<(uint, uint)>();
        if (descriptor is null) return dirs;

        uint rootExtent = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(156 + 2, 4));
        uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(156 + 10, 4));
        dirs.Add((rootExtent, rootSize));

        // Walk the tree from root, collecting sub-directory extents.
        var seen = new HashSet<uint> { rootExtent };
        WalkDirs(iso, rootExtent, rootSize, dirs, seen);
        return dirs;
    }

    private static void WalkDirs(byte[] iso, uint extent, uint size,
                                 List<(uint, uint)> dirs, HashSet<uint> seen)
    {
        var data = ReadRange(iso, (long)extent * SectorSize, (int)size);
        int p = 0;
        while (p < data.Length)
        {
            int recLen = data[p];
            if (recLen == 0)
            {
                int next = (p / SectorSize + 1) * SectorSize;
                if (next <= p) break;
                p = next;
                continue;
            }
            if (recLen < 33 || p + recLen > data.Length) break;

            uint childExtent = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 2, 4));
            uint childSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 10, 4));
            byte flags = data[p + 25];
            int idLen = data[p + 32];

            bool isDir = (flags & 0x02) != 0;
            bool isDot = idLen == 1 && (data[p + 33] == 0x00 || data[p + 33] == 0x01);
            if (isDir && !isDot && seen.Add(childExtent))
            {
                dirs.Add((childExtent, childSize));
                WalkDirs(iso, childExtent, childSize, dirs, seen);
            }

            p += recLen;
        }
    }

    // ---- patching -----------------------------------------------------------

    private static void PatchDirectoryRecords(byte[] img, uint extent, uint size, uint add)
    {
        long baseOffset = (long)extent * SectorSize;
        int p = 0;
        while (p < size)
        {
            long recAt = baseOffset + p;
            if (recAt >= img.Length) break;
            int recLen = img[recAt];
            if (recLen == 0)
            {
                int next = (p / SectorSize + 1) * SectorSize;
                if (next <= p) break;
                p = next;
                continue;
            }
            if (recLen < 33) break;

            // Extent field at record+2 (LE). Its BE mirror is at record+6, which
            // ISO 9660 keeps in sync; update both for a valid image.
            uint old = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)(recAt + 2), 4));
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)(recAt + 2), 4), old + add);
            BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan((int)(recAt + 6), 4), old + add);

            p += recLen;
        }
    }

    private static void PatchDescriptorRoot(byte[] img, int descriptorSector, uint add)
    {
        long at = (long)descriptorSector * SectorSize + 156;
        uint old = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)(at + 2), 4));
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)(at + 2), 4), old + add);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan((int)(at + 6), 4), old + add);
    }

    private static void PatchPathTables(byte[] img, int descriptorSector, uint add)
    {
        long d = (long)descriptorSector * SectorSize;
        uint ptSize = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)(d + 132), 4));

        // Locations: type-L (LE) at 140 and its optional copy at 144; type-M (BE)
        // at 148 and its copy at 152. Read the old LBAs before shifting them.
        uint lLba = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)(d + 140), 4));
        uint lLba2 = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)(d + 144), 4));
        uint mLba = BinaryPrimitives.ReadUInt32BigEndian(img.AsSpan((int)(d + 148), 4));
        uint mLba2 = BinaryPrimitives.ReadUInt32BigEndian(img.AsSpan((int)(d + 152), 4));

        // Rebase the records in each table (the data stays put; addresses shift).
        if (lLba != 0) PatchPathTableRecords(img, lLba, ptSize, add, bigEndian: false);
        if (mLba != 0) PatchPathTableRecords(img, mLba, ptSize, add, bigEndian: true);

        // Rebase the stored pointers.
        if (lLba != 0) BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)(d + 140), 4), lLba + add);
        if (lLba2 != 0) BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)(d + 144), 4), lLba2 + add);
        if (mLba != 0) BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan((int)(d + 148), 4), mLba + add);
        if (mLba2 != 0) BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan((int)(d + 152), 4), mLba2 + add);
    }

    private static void PatchPathTableRecords(byte[] img, uint tableLba, uint tableSize, uint add, bool bigEndian)
    {
        long at = (long)tableLba * SectorSize;
        int p = 0;
        while (p + 8 <= tableSize && at + p + 8 <= img.Length)
        {
            int lenId = img[at + p];
            if (lenId == 0) break;
            var extentSpan = img.AsSpan((int)(at + p + 2), 4);
            uint old = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(extentSpan)
                : BinaryPrimitives.ReadUInt32LittleEndian(extentSpan);
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(extentSpan, old + add);
            else BinaryPrimitives.WriteUInt32LittleEndian(extentSpan, old + add);

            int recLen = 8 + lenId + (lenId & 1);   // padded to even
            p += recLen;
        }
    }

    // ---- helpers ------------------------------------------------------------

    private static bool HasJoliet(byte[] iso)
    {
        for (int lba = 16; lba <= 100; lba++)
        {
            long off = (long)lba * SectorSize;
            if (off + SectorSize > iso.Length) break;
            if (!(iso[off + 1] == 'C' && iso[off + 2] == 'D' && iso[off + 3] == '0'
                  && iso[off + 4] == '0' && iso[off + 5] == '1')) continue;
            if (iso[off] == 0xFF) break;
            if (iso[off] == 2) return true;
        }
        return false;
    }

    private static byte[] ReadRange(byte[] iso, long offset, int length)
    {
        if (offset < 0 || offset >= iso.Length) return Array.Empty<byte>();
        length = (int)Math.Min(length, iso.Length - offset);
        return iso.AsSpan((int)offset, length).ToArray();
    }
}
