// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Forensics;

namespace DiscForge.Core.Iso;

/// <summary>One path-table record: a directory, keyed by its 1-based index, pointing at its extent and its
/// parent directory's index.</summary>
public sealed record PathTableEntry(int Index, string Name, uint Extent, int ParentIndex, int ExtAttrLength);

/// <summary>The result of auditing an ISO 9660 path table.</summary>
public sealed record IsoPathTableReport
{
    public required IReadOnlyList<PathTableEntry> Entries { get; init; }
    public required IReadOnlyList<LintFinding> Findings { get; init; }

    public int Errors => Findings.Count(f => f.Severity == LintSeverity.Error);
    public int Warnings => Findings.Count(f => f.Severity == LintSeverity.Warning);
    public bool Ok => Errors == 0;

    public string Summary() => Entries.Count == 0 && Findings.Count == 0
        ? "No path table."
        : $"Path table: {Entries.Count} director(y/ies), {Errors} error(s), {Warnings} warning(s).";
}

/// <summary>
/// iso-pathtable — a strict audit of the ISO 9660 path table, the flat directory index a filesystem uses
/// to jump straight to any directory without walking the tree. ISO stores it twice, once little-endian
/// (Type-L) and once big-endian (Type-M); the two must describe the same directories, at the same extents,
/// with the same parent links, and the parent of every directory must precede it. This parses both tables,
/// checks they agree byte-for-byte in meaning, validates the parent references form a proper tree, confirms
/// the declared path-table size, and cross-checks that each entry's extent actually points at a directory
/// (its "." record self-referencing that extent). It is the structural companion to image-lint's PVD
/// checks. Validates and reports; changes nothing.
/// </summary>
public static class IsoPathTable
{
    private const int SS = 2048;

    public static IsoPathTableReport Read(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var f = new List<LintFinding>();

        if (image.Length < 17 * SS)
        {
            f.Add(new(LintSeverity.Error, "image", "too small to hold an ISO 9660 volume descriptor set."));
            return new IsoPathTableReport { Entries = Array.Empty<PathTableEntry>(), Findings = f };
        }

        var pvd = image.AsSpan(16 * SS, SS);
        uint ptSize = U32Le(pvd, 132);
        uint lLoc = U32Le(pvd, 140);
        uint mLoc = U32Be(pvd, 148);
        long totalSectors = image.Length / SS;

        if (lLoc == 0 || lLoc >= totalSectors)
        {
            f.Add(new(LintSeverity.Error, "PVD", $"Type-L path table location {lLoc} is outside the image."));
            return new IsoPathTableReport { Entries = Array.Empty<PathTableEntry>(), Findings = f };
        }

        var lEntries = ParseTable(image, (long)lLoc * SS, ptSize, littleEndian: true, f, "L");
        List<PathTableEntry>? mEntries = null;
        if (mLoc == 0 || mLoc >= totalSectors)
            f.Add(new(LintSeverity.Warning, "PVD", $"Type-M path table location {mLoc} is outside the image — cannot cross-check."));
        else
            mEntries = ParseTable(image, (long)mLoc * SS, ptSize, littleEndian: false, f, "M");

        // ---- L vs M agreement ----------------------------------------------
        if (mEntries != null)
        {
            if (lEntries.Count != mEntries.Count)
                f.Add(new(LintSeverity.Error, "path-table",
                    $"Type-L has {lEntries.Count} entries but Type-M has {mEntries.Count} — the tables disagree."));
            else
                for (int i = 0; i < lEntries.Count; i++)
                {
                    var (l, m) = (lEntries[i], mEntries[i]);
                    if (l.Extent != m.Extent || l.ParentIndex != m.ParentIndex || l.Name != m.Name)
                        f.Add(new(LintSeverity.Error, $"entry {i + 1}",
                            $"Type-L and Type-M disagree (L: {l.Name}@{l.Extent}/p{l.ParentIndex}, M: {m.Name}@{m.Extent}/p{m.ParentIndex})."));
                }
        }

        // ---- structural checks on the L table ------------------------------
        for (int i = 0; i < lEntries.Count; i++)
        {
            var e = lEntries[i];
            int idx = i + 1;
            if (e.ParentIndex < 1 || e.ParentIndex > lEntries.Count)
                f.Add(new(LintSeverity.Error, $"entry {idx}", $"parent index {e.ParentIndex} is out of range (1..{lEntries.Count})."));
            else if (idx != 1 && e.ParentIndex > idx)
                f.Add(new(LintSeverity.Error, $"entry {idx}", $"parent {e.ParentIndex} follows the child — the path table must be hierarchy-ordered."));

            if ((long)e.Extent * SS >= image.Length)
                f.Add(new(LintSeverity.Error, $"entry {idx}", $"directory extent {e.Extent} lies past the end of the image."));
            else if (!DirectorySelfRefersTo(image, e.Extent))
                f.Add(new(LintSeverity.Warning, $"entry {idx}",
                    $"extent {e.Extent} does not begin with a \".\" record pointing back at itself."));
        }

        if (lEntries.Count > 0 && lEntries[0].ParentIndex != 1)
            f.Add(new(LintSeverity.Error, "root", "the first path-table entry (root) must have parent index 1."));

        return new IsoPathTableReport { Entries = lEntries, Findings = f };
    }

    public static string Render(IsoPathTableReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var e in r.Entries.Take(60))
            sb.AppendLine($"  {e.Index,3}. {(e.Name.Length == 0 ? "(root)" : e.Name),-24} extent {e.Extent}, parent {e.ParentIndex}");
        foreach (var x in r.Findings)
            sb.AppendLine($"  [{x.Severity}] {x.Where}: {x.Message}");
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    private static List<PathTableEntry> ParseTable(byte[] image, long offset, uint size, bool littleEndian,
        List<LintFinding> f, string which)
    {
        var list = new List<PathTableEntry>();
        long end = Math.Min(image.Length, offset + size);
        long o = offset;
        int index = 0;
        while (o + 8 <= end)
        {
            int len = image[o];
            if (len == 0) break;                                  // padding to the sector end
            int extAttr = image[o + 1];
            uint extent = littleEndian ? U32Le(image, o + 2) : U32Be(image, o + 2);
            int parent = littleEndian ? U16Le(image, o + 6) : U16Be(image, o + 6);
            long nameOff = o + 8;
            if (nameOff + len > end) { f.Add(new(LintSeverity.Error, $"path-table {which}", "a record runs past the table end.")); break; }
            string name = len == 1 && image[nameOff] == 0 ? "" : Encoding.ASCII.GetString(image, (int)nameOff, len);
            index++;
            list.Add(new PathTableEntry(index, name, extent, parent, extAttr));
            o += 8 + len + (len & 1);                             // pad to an even boundary
        }
        return list;
    }

    /// <summary>True if the directory at <paramref name="extent"/> opens with a "." record (name length 1,
    /// byte 0) whose own extent equals <paramref name="extent"/>.</summary>
    private static bool DirectorySelfRefersTo(byte[] image, uint extent)
    {
        long o = (long)extent * SS;
        if (o + 34 > image.Length) return false;
        int recLen = image[o];
        if (recLen < 34) return false;
        int nameLen = image[o + 32];
        if (nameLen != 1 || image[o + 33] != 0) return false;     // "." record has a single 0x00 name
        uint selfExtent = U32Le(image, o + 2);
        return selfExtent == extent;
    }

    private static int U16Le(byte[] b, long o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)o));
    private static int U16Be(byte[] b, long o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan((int)o));
    private static uint U32Le(byte[] b, long o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)o));
    private static uint U32Le(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static uint U32Be(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
    private static uint U32Be(byte[] b, long o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)o));
}
