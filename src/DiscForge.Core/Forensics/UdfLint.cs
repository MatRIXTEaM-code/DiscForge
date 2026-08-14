// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Util;

namespace DiscForge.Core.Forensics;

/// <summary>The result of linting the UDF structures on a disc image.</summary>
public sealed record UdfLintReport
{
    public required IReadOnlyList<LintFinding> Findings { get; init; }
    /// <summary>False when the image carries no UDF Anchor at all — there is nothing to lint.</summary>
    public required bool HasUdf { get; init; }
    public int Errors => Findings.Count(f => f.Severity == LintSeverity.Error);
    public int Warnings => Findings.Count(f => f.Severity == LintSeverity.Warning);
    public bool Ok => Errors == 0;

    public string Summary() =>
        !HasUdf ? "UDF: no UDF filesystem present (no Anchor Volume Descriptor Pointer)."
        : Findings.Count == 0 ? "UDF: clean — no conformance issues."
        : $"UDF: {Errors} error(s), {Warnings} warning(s).";
}

/// <summary>
/// udf-lint — a strict conformance checker for the UDF (ECMA-167 / OSTA UDF) structures on a disc
/// image, the companion to <see cref="IsoLint"/>. It walks the volume the way a real UDF driver does —
/// Volume Recognition Sequence, Anchor at sector 256, Main Volume Descriptor Sequence, Partition and
/// Logical Volume descriptors, the Logical Volume Integrity Descriptor, the File Set Descriptor, the
/// root File Entry — and at every stop it checks the descriptor tag's checksum and CRC and, crucially,
/// that the tag location is recorded correctly: descriptors inside a partition must carry a
/// PARTITION-RELATIVE location, and it is exactly that field, wrong, that makes strict readers report a
/// File Set Descriptor as "not found". It validates and reports, and changes nothing on the disc.
/// </summary>
public static class UdfLint
{
    private const int SS = 2048;

    // ECMA-167 descriptor tag identifiers.
    private const ushort TagAnchor = 2;
    private const ushort TagPartition = 5;
    private const ushort TagLogicalVolume = 6;
    private const ushort TagLogicalVolumeIntegrity = 9;
    private const ushort TagFileSet = 256;
    private const ushort TagFileEntry = 261;
    private const ushort TagExtendedFileEntry = 266;

    public static UdfLintReport Check(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var f = new List<LintFinding>();

        if (image.Length % SS != 0)
            f.Add(new(LintSeverity.Warning, "image",
                $"length {image.Length:N0} is not a whole number of {SS}-byte sectors."));

        long totalSectors = image.Length / SS;
        if (totalSectors < 257)
        {
            // No room for an Anchor at sector 256 — treat as "no UDF" rather than an error.
            return new UdfLintReport { Findings = f, HasUdf = false };
        }

        // ---- Anchor Volume Descriptor Pointer at sector 256 -------------------
        if (TagIdAt(image, 256) != TagAnchor)
            return new UdfLintReport { Findings = f, HasUdf = false };

        // ---- Volume Recognition Sequence (BEA01 / NSR0x / TEA01) --------------
        CheckVrs(image, totalSectors, f);

        ValidateTag(image, 256, expectedLoc: 256, "Anchor(256)", f);
        var anc = image.AsSpan(256 * SS, SS);
        uint mvdsLen = BinaryPrimitives.ReadUInt32LittleEndian(anc.Slice(16, 4));
        uint mvdsLoc = BinaryPrimitives.ReadUInt32LittleEndian(anc.Slice(20, 4));

        // ---- backup Anchor at the last sector ---------------------------------
        if (TagIdAt(image, (uint)(totalSectors - 1)) != TagAnchor)
            f.Add(new(LintSeverity.Warning, "Anchor",
                $"no backup Anchor Volume Descriptor Pointer at the last sector ({totalSectors - 1}); " +
                "a single anchor survives no damage."));

        if (mvdsLoc == 0 || (long)mvdsLoc + mvdsLen / SS > totalSectors)
        {
            f.Add(new(LintSeverity.Error, "Anchor",
                $"Main Volume Descriptor Sequence extent (loc {mvdsLoc}, {mvdsLen:N0} bytes) lies outside the image."));
            return new UdfLintReport { Findings = f, HasUdf = true };
        }

        // ---- walk the Main Volume Descriptor Sequence -------------------------
        int partSector = -1; long partLen = 0;
        int lbSize = SS;
        uint fsdBlock = 0, fsdPartRef = 0, rootIcbBlock = 0;
        uint lvidLoc = 0; long lvidLen = 0;
        bool sawPartition = false, sawLvd = false;

        for (uint s = mvdsLoc; s < mvdsLoc + Math.Max(1, mvdsLen / SS) && s < totalSectors; s++)
        {
            ushort tag = TagIdAt(image, s);
            if (tag == 0 || tag == 8) break;               // unrecorded, or Terminating Descriptor
            ValidateTag(image, s, expectedLoc: s, $"MVDS({tag})@{s}", f);
            var d = image.AsSpan((int)s * SS, SS);

            if (tag == TagPartition)
            {
                sawPartition = true;
                partSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(188, 4));
                partLen = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(192, 4));
            }
            else if (tag == TagLogicalVolume)
            {
                sawLvd = true;
                lbSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(212, 4));
                if (lbSize != SS)
                    f.Add(new(LintSeverity.Warning, "LVD", $"logical block size is {lbSize}, not the usual {SS}."));
                // logicalVolumeContentsUse: a long_ad to the File Set Descriptor.
                fsdBlock = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(248 + 4, 4));
                fsdPartRef = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(248 + 8, 2));
                // integrity sequence extent.
                lvidLen = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(432, 4));
                lvidLoc = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(436, 4));
            }
        }

        if (!sawPartition) f.Add(new(LintSeverity.Error, "MVDS", "no Partition Descriptor in the Main Volume Descriptor Sequence."));
        if (!sawLvd) f.Add(new(LintSeverity.Error, "MVDS", "no Logical Volume Descriptor in the Main Volume Descriptor Sequence."));
        if (!sawPartition || !sawLvd)
            return new UdfLintReport { Findings = f, HasUdf = true };

        if (partSector < 0 || partSector + partLen > totalSectors)
            f.Add(new(LintSeverity.Error, "Partition",
                $"partition (start {partSector}, {partLen:N0} blocks) runs past the end of the image ({totalSectors:N0} sectors)."));

        // ---- Logical Volume Integrity Descriptor ------------------------------
        if (lvidLoc != 0 && lvidLoc < totalSectors && TagIdAt(image, lvidLoc) == TagLogicalVolumeIntegrity)
        {
            ValidateTag(image, lvidLoc, expectedLoc: lvidLoc, $"LVID@{lvidLoc}", f);
            var lv = image.AsSpan((int)lvidLoc * SS, SS);
            uint sizeTable0 = BinaryPrimitives.ReadUInt32LittleEndian(lv.Slice(84, 4));
            if (partLen != 0 && sizeTable0 != partLen)
                f.Add(new(LintSeverity.Warning, "LVID",
                    $"partition size in the integrity descriptor ({sizeTable0:N0}) disagrees with the Partition Descriptor ({partLen:N0})."));
        }
        else
        {
            f.Add(new(LintSeverity.Warning, "LVID",
                "no Logical Volume Integrity Descriptor where the Logical Volume Descriptor points; the volume's clean/dirty state is unknown."));
        }

        // ---- File Set Descriptor (the tag-location check that catches the classic bug) ----
        long fsdSector = (long)partSector + fsdBlock;
        if (fsdPartRef != 0)
            f.Add(new(LintSeverity.Info, "FSD", $"File Set Descriptor is in partition reference {fsdPartRef}."));

        if (fsdSector < 0 || fsdSector >= totalSectors || TagIdAt(image, (uint)fsdSector) != TagFileSet)
        {
            f.Add(new(LintSeverity.Error, "FSD",
                $"no File Set Descriptor at the location the Logical Volume Descriptor points to " +
                $"(partition block {fsdBlock}, sector {fsdSector})."));
            return new UdfLintReport { Findings = f, HasUdf = true };
        }

        ValidateTag(image, (uint)fsdSector, expectedLoc: fsdBlock, "FSD", f);   // partition-relative!
        var fsd = image.AsSpan((int)fsdSector * SS, SS);
        rootIcbBlock = BinaryPrimitives.ReadUInt32LittleEndian(fsd.Slice(400 + 4, 4));

        // ---- root directory File Entry ----------------------------------------
        long rootSector = (long)partSector + rootIcbBlock;
        if (rootSector < 0 || rootSector >= totalSectors)
            f.Add(new(LintSeverity.Error, "root", $"root directory ICB (partition block {rootIcbBlock}) lies outside the image."));
        else
        {
            ushort rt = TagIdAt(image, (uint)rootSector);
            if (rt != TagFileEntry && rt != TagExtendedFileEntry)
                f.Add(new(LintSeverity.Error, "root",
                    $"root directory ICB does not point at a File Entry (found tag {rt} at sector {rootSector})."));
            else
                ValidateTag(image, (uint)rootSector, expectedLoc: rootIcbBlock, "root FE", f);   // partition-relative
        }

        return new UdfLintReport { Findings = f, HasUdf = true };
    }

    public static string Render(UdfLintReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var x in r.Findings)
            sb.AppendLine($"  [{x.Severity}] {x.Where}: {x.Message}");
        return sb.ToString().TrimEnd();
    }

    // ---- descriptor-tag validation -----------------------------------------

    /// <summary>
    /// Validate a descriptor tag at <paramref name="sector"/>: its checksum, its CRC over the recorded
    /// length, and that its tag-location field equals <paramref name="expectedLoc"/> — which is the
    /// absolute sector for descriptors in volume space, but the PARTITION-RELATIVE block for descriptors
    /// recorded inside a partition (the File Set Descriptor, File Entries).
    /// </summary>
    private static void ValidateTag(byte[] image, uint sector, uint expectedLoc, string where, List<LintFinding> f)
    {
        var t = image.AsSpan((int)sector * SS, SS);

        byte storedChecksum = t[4];
        int sum = 0;
        for (int i = 0; i < 4; i++) sum += t[i];
        for (int i = 5; i < 16; i++) sum += t[i];
        if ((byte)sum != storedChecksum)
            f.Add(new(LintSeverity.Error, where, $"descriptor tag checksum is 0x{storedChecksum:X2}, computed 0x{(byte)sum:X2}."));

        ushort descVer = BinaryPrimitives.ReadUInt16LittleEndian(t.Slice(2, 2));
        if (descVer is not (2 or 3))
            f.Add(new(LintSeverity.Warning, where, $"descriptor version is {descVer}, expected 2 or 3."));

        ushort storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(t.Slice(8, 2));
        ushort crcLen = BinaryPrimitives.ReadUInt16LittleEndian(t.Slice(10, 2));
        if (16 + crcLen <= SS)
        {
            ushort calc = Crc16.Compute(t.Slice(16, crcLen));
            if (calc != storedCrc)
                f.Add(new(LintSeverity.Error, where, $"descriptor CRC is 0x{storedCrc:X4}, computed 0x{calc:X4} over {crcLen} bytes."));
        }
        else
        {
            f.Add(new(LintSeverity.Warning, where, $"descriptor CRC length {crcLen} overflows the sector."));
        }

        uint tagLoc = BinaryPrimitives.ReadUInt32LittleEndian(t.Slice(12, 4));
        if (tagLoc != expectedLoc)
            f.Add(new(LintSeverity.Error, where,
                $"tag location is {tagLoc} but must be {expectedLoc} " +
                (expectedLoc < sector
                    ? "(partition-relative — recording the absolute sector here makes strict readers reject the descriptor)."
                    : "(the sector the descriptor is recorded in).")));
    }

    private static ushort TagIdAt(byte[] image, uint sector)
    {
        if ((long)(sector + 1) * SS > image.Length) return 0;
        return BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)sector * SS, 2));
    }

    private static void CheckVrs(byte[] image, long totalSectors, List<LintFinding> f)
    {
        bool bea = false, nsr = false, tea = false;
        for (long s = 16; s < Math.Min(totalSectors, 16 + 64); s++)
        {
            var d = image.AsSpan((int)(s * SS), 8);
            string id = Encoding.ASCII.GetString(d.Slice(1, 5));
            if (id == "BEA01") bea = true;
            else if (id is "NSR02" or "NSR03") nsr = true;
            else if (id == "TEA01") { tea = true; break; }
            else if (id is "CD001") continue;              // ISO 9660 descriptor — a bridge; keep scanning
            else if (!bea) continue;                        // still in the ISO 9660 area before the Extended Area
            else break;                                     // ran off the end of the recognition sequence
        }
        if (!bea) f.Add(new(LintSeverity.Error, "VRS", "no UDF Extended Area (BEA01 descriptor) in the Volume Recognition Sequence."));
        if (!nsr) f.Add(new(LintSeverity.Error, "VRS", "no NSR02/NSR03 descriptor — the volume does not declare a UDF filesystem."));
        if (bea && !tea) f.Add(new(LintSeverity.Warning, "VRS", "no TEA01 terminator closing the Extended Area."));
    }
}
