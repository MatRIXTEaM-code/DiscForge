// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Forensics;

/// <summary>One run of non-zero bytes on the disc that no catalogued file and no
/// standard ISO 9660 structure explains.</summary>
public sealed record OrphanRegion
{
    /// <summary>Byte offset of the first non-zero byte.</summary>
    public required long Offset { get; init; }
    /// <summary>Span from the first to the last non-zero byte (inclusive of the
    /// short zero gaps that were coalesced within it).</summary>
    public required long Length { get; init; }
    /// <summary>How many of those bytes are actually non-zero.</summary>
    public required long NonZeroBytes { get; init; }
    /// <summary>Shannon entropy of a sample, in bits/byte (0–8).</summary>
    public required double Entropy { get; init; }
    /// <summary>Fraction of the sample that is printable ASCII (0–1).</summary>
    public required double PrintableRatio { get; init; }
    /// <summary>Where on the disc it sits: "system-area", "between-structures" or
    /// "past-volume-end".</summary>
    public required string Zone { get; init; }
    /// <summary>A guess at what it is: "text-like", "high-entropy" or "binary".</summary>
    public required string Kind { get; init; }
    /// <summary>The first bytes of the region, for eyeballing.</summary>
    public required byte[] Sample { get; init; }

    /// <summary>Starting sector (2048-byte LBA) of the region.</summary>
    public long Lba => Offset / 2048;

    public string HexSample =>
        System.Convert.ToHexString(Sample).ToLowerInvariant();

    public string AsciiSample
    {
        get
        {
            var c = new char[Sample.Length];
            for (int i = 0; i < Sample.Length; i++)
                c[i] = Sample[i] is >= 32 and < 127 ? (char)Sample[i] : '.';
            return new string(c);
        }
    }

    /// <summary>A one-line reading of what and where this is.</summary>
    public string Note()
    {
        string where = Zone switch
        {
            "system-area" => "in the ISO system area (sectors 0–15, ahead of the volume descriptors)",
            "past-volume-end" => "past the declared end of the volume — classic hidden / appended data",
            _ => "in a gap between catalogued structures — leftover, deleted-but-present, or hidden content",
        };
        string what = Kind switch
        {
            "text-like" => "reads as text",
            "high-entropy" => "high-entropy (compressed, encrypted or media-like)",
            _ => "binary",
        };
        return $"{where}; {what}.";
    }
}

/// <summary>The result of an archaeology sweep over one image.</summary>
public sealed record ArchaeologyReport
{
    public required long ImageLength { get; init; }
    /// <summary>Volume size the PVD declares, in bytes (0 if unreadable).</summary>
    public required long DeclaredVolumeBytes { get; init; }
    /// <summary>Bytes covered by the system area, descriptors, path tables,
    /// directory records and catalogued files.</summary>
    public required long KnownStructureBytes { get; init; }
    /// <summary>Bytes that are zero and outside every known structure — ordinary padding.</summary>
    public required long ZeroPaddingBytes { get; init; }
    /// <summary>Total span of all orphan regions.</summary>
    public required long OrphanBytes { get; init; }
    public required IReadOnlyList<OrphanRegion> Orphans { get; init; }

    public bool FoundAnything => Orphans.Count > 0;

    public string Summary()
    {
        if (!FoundAnything)
            return $"Clean: every non-zero byte of {ImageLength:N0} is accounted for by a file or a " +
                   "standard ISO structure. Nothing hidden.";
        long past = Orphans.Count(o => o.Zone == "past-volume-end");
        long sys = Orphans.Count(o => o.Zone == "system-area");
        return $"{Orphans.Count:N0} orphan region(s), {OrphanBytes:N0} byte(s) of data no file or " +
               $"structure references" +
               (past > 0 ? $" — {past} past the volume end" : "") +
               (sys > 0 ? $"{(past > 0 ? "," : " —")} {sys} in the system area" : "") + ".";
    }
}

/// <summary>
/// Disc archaeology: report every non-zero byte on a disc that no file references
/// and no standard ISO 9660 structure explains — leftover mastering data, files
/// deleted from the directory but never overwritten, payloads tucked in the system
/// area or past the declared volume, and the odd protection artefact.
///
/// The method is subtractive and reuses the same idea as the re-master cover: build
/// the complete map of what is <i>legitimately</i> here — the 16-sector system area,
/// every volume descriptor, both path tables, the directory records of both the
/// ISO 9660 and Joliet hierarchies, and every catalogued file's sectors — then look
/// at what is left. Anything non-zero outside that map is data normal extraction
/// silently discards. This only <i>surfaces</i> what is already on the owner's disc;
/// it decodes nothing and defeats nothing.
///
/// Expects a cooked ISO 9660 image (2048-byte sectors), the same input the
/// re-master takes — extract the data track from a raw/CDI dump first.
/// </summary>
public static class DiscArchaeology
{
    private const int SS = 2048;
    private const int SystemAreaSectors = 16;
    private const int SampleForStats = 4096;
    private const int SampleForDisplay = 64;

    /// <summary>
    /// Sweep an image for orphan data.
    /// </summary>
    /// <param name="image">A cooked ISO 9660 image.</param>
    /// <param name="minOrphanBytes">Ignore non-zero runs shorter than this (default 32)
    /// so single stray bytes — bit-rot's department, not archaeology's — don't dominate.</param>
    /// <param name="coalesceZeroGap">Treat zero runs shorter than this as interior to a
    /// region rather than a boundary (default one sector), so a structure broken up by
    /// small zero fields reports as one find.</param>
    public static ArchaeologyReport FindOrphans(byte[] image, int minOrphanBytes = 32, int coalesceZeroGap = SS)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (minOrphanBytes < 1) minOrphanBytes = 1;
        if (coalesceZeroGap < 1) coalesceZeroGap = 1;
        if (image.Length < 17 * SS)
            throw new IsoFormatException("Image is too small to hold an ISO 9660 volume descriptor set.");

        var known = new List<(long Start, long End)>();
        void Cover(long startBytes, long lenBytes)
        {
            if (lenBytes <= 0) return;
            long end = Math.Min(image.Length, startBytes + RoundUpSector(lenBytes));
            long start = Math.Max(0, startBytes);
            if (end > start) known.Add((start, end));
        }

        // Note: the 16-sector system area ahead of the descriptors is deliberately
        // NOT covered. Standard ISO 9660 writes nothing there, so any non-zero
        // content — a PlayStation licence, boot code, or a protection payload — is
        // precisely what this sweep exists to surface (zone "system-area").

        // The volume descriptor set, and what each descriptor points at.
        long declaredVolumeBytes = 0;
        bool sawPvd = false;
        for (long lba = SystemAreaSectors; lba < SystemAreaSectors + 512 && (lba + 1) * SS <= image.Length; lba++)
        {
            var sec = image.AsSpan((int)(lba * SS), SS);
            if (sec[1] != (byte)'C' || sec[2] != (byte)'D' || sec[3] != (byte)'0' ||
                sec[4] != (byte)'0' || sec[5] != (byte)'1')
                break;

            Cover(lba * SS, SS);
            byte type = sec[0];
            if (type == 0xFF) break;                       // set terminator — still covered above

            if (type is 1 or 2)
            {
                if (type == 1)
                {
                    sawPvd = true;
                    declaredVolumeBytes = (long)BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(80, 4)) * SS;
                }

                long ptSize = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(132, 4));
                long lPath = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(140, 4));
                long lPathOpt = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(144, 4));
                long mPath = BinaryPrimitives.ReadUInt32BigEndian(sec.Slice(148, 4));
                long mPathOpt = BinaryPrimitives.ReadUInt32BigEndian(sec.Slice(152, 4));
                foreach (long loc in new[] { lPath, lPathOpt, mPath, mPathOpt })
                    if (loc > 0) Cover(loc * SS, ptSize);

                // The root directory record lives at offset 156 of the descriptor.
                long rootExtent = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(156 + 2, 4));
                long rootSize = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(156 + 10, 4));
                if (rootExtent > 0) Cover(rootExtent * SS, rootSize);
            }
        }

        if (!sawPvd)
            throw new IsoFormatException(
                "No ISO 9660 primary volume descriptor at sector 16 — disc-anomalies needs a cooked " +
                "ISO 9660 data-track image. Extract the data track from a raw/CDI dump first.");

        // Every file and directory record, from both name hierarchies (Joliet's
        //    directory records are separate structure even though its files share
        //    the same extents), so neither is mistaken for orphan data.
        CoverTree(image, IsoReader.NamePreference.Iso9660, Cover);
        CoverTree(image, IsoReader.NamePreference.Joliet, Cover);

        // Merge the cover and walk the gaps.
        known.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(long Start, long End)>();
        foreach (var r in known)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End)
            {
                if (r.End > merged[^1].End) merged[^1] = (merged[^1].Start, r.End);
            }
            else merged.Add(r);
        }

        long knownBytes = merged.Sum(m => m.End - m.Start);
        var orphans = new List<OrphanRegion>();
        long orphanBytes = 0, zeroPadding = 0;

        long gapStart = 0;
        foreach (var m in merged)
        {
            if (m.Start > gapStart)
                ScanGap(image, gapStart, m.Start, minOrphanBytes, coalesceZeroGap,
                        declaredVolumeBytes, orphans, ref orphanBytes, ref zeroPadding);
            gapStart = Math.Max(gapStart, m.End);
        }
        if (gapStart < image.Length)
            ScanGap(image, gapStart, image.Length, minOrphanBytes, coalesceZeroGap,
                    declaredVolumeBytes, orphans, ref orphanBytes, ref zeroPadding);

        orphans.Sort((a, b) => b.Length.CompareTo(a.Length));

        return new ArchaeologyReport
        {
            ImageLength = image.Length,
            DeclaredVolumeBytes = declaredVolumeBytes,
            KnownStructureBytes = knownBytes,
            ZeroPaddingBytes = zeroPadding,
            OrphanBytes = orphanBytes,
            Orphans = orphans,
        };
    }

    // ---- internals ----------------------------------------------------------

    private static void CoverTree(byte[] image, IsoReader.NamePreference pref, Action<long, long> cover)
    {
        try
        {
            using var ms = new MemoryStream(image, writable: false);
            var dir = IsoReader.Read(ms, pref);
            foreach (var e in dir.Entries)
                cover((long)e.Extent * SS, e.Size == 0 ? SS : e.Size);   // whole-sector allocation
        }
        catch (IsoFormatException)
        {
            // That hierarchy is absent (e.g. no Joliet) — nothing to cover from it.
        }
    }

    private static void ScanGap(byte[] image, long gapStart, long gapEnd, int minOrphanBytes,
                                int coalesceZeroGap, long declaredVolumeBytes,
                                List<OrphanRegion> orphans, ref long orphanBytes, ref long zeroPadding)
    {
        long i = gapStart;
        while (i < gapEnd)
        {
            // Skip leading zeros (padding).
            long zStart = i;
            while (i < gapEnd && image[i] == 0) i++;
            zeroPadding += i - zStart;
            if (i >= gapEnd) break;

            long rStart = i;
            long lastNonZero = i;
            long nonZero = 0;
            int zeroGap = 0;
            long k = i;
            while (k < gapEnd)
            {
                if (image[k] != 0) { lastNonZero = k; nonZero++; zeroGap = 0; k++; }
                else
                {
                    zeroGap++;
                    if (zeroGap >= coalesceZeroGap) break;   // a real boundary
                    k++;
                }
            }

            long rEnd = lastNonZero + 1;
            long len = rEnd - rStart;
            // The zeros between the last non-zero byte and k are padding, not orphan.
            zeroPadding += (k - rStart) - len;

            if (len >= minOrphanBytes)
            {
                orphans.Add(Classify(image, rStart, len, nonZero, declaredVolumeBytes));
                orphanBytes += len;
            }
            else
            {
                zeroPadding += len;   // too small to count as a find; treat as noise
            }

            i = k;
        }
    }

    private static OrphanRegion Classify(byte[] image, long start, long len, long nonZero, long declaredVolumeBytes)
    {
        int statLen = (int)Math.Min(len, SampleForStats);
        var stat = image.AsSpan((int)start, statLen);

        Span<int> freq = stackalloc int[256];
        int printable = 0;
        foreach (byte b in stat)
        {
            freq[b]++;
            if (b is 9 or 10 or 13 || b is >= 32 and < 127) printable++;
        }
        double entropy = 0;
        foreach (int c in freq)
            if (c > 0) { double p = (double)c / statLen; entropy -= p * Math.Log2(p); }
        double printableRatio = statLen == 0 ? 0 : (double)printable / statLen;

        string kind = entropy > 7.2 ? "high-entropy"
                    : printableRatio > 0.85 ? "text-like"
                    : "binary";

        string zone = start < (long)SystemAreaSectors * SS ? "system-area"
                    : (declaredVolumeBytes > 0 && start >= declaredVolumeBytes) ? "past-volume-end"
                    : "between-structures";

        int sampleLen = (int)Math.Min(len, SampleForDisplay);
        var sample = image.AsSpan((int)start, sampleLen).ToArray();

        return new OrphanRegion
        {
            Offset = start,
            Length = len,
            NonZeroBytes = nonZero,
            Entropy = entropy,
            PrintableRatio = printableRatio,
            Zone = zone,
            Kind = kind,
            Sample = sample,
        };
    }

    private static long RoundUpSector(long n) => (n + SS - 1) / SS * SS;
}
