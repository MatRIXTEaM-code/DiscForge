// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;

namespace DiscForge.Core.Media;

/// <summary>
/// Verifies a burned DVD/BD against its source image at the sector level —
/// ImgBurn's "Verify" does a sector compare / MD5, and this does that too, but
/// reports at DVD **ECC-block** (16-sector) granularity and is **layer-break
/// aware**: for a dual-layer disc it attributes every mismatch to L0 or L1 and
/// checks the layer break sits on a legal boundary. DVD/BD user sectors are 2048
/// cooked bytes with no sub-channel, so this is a straight, exact comparison —
/// the value over MD5 is telling you *where* and *how* a burn differs, not just
/// that it did.
/// </summary>
public static class DvdReadbackCompare
{
    public const int Sector = 2048;
    public const int EccBlock = 16;                 // sectors per DVD ECC block

    public enum Grade { Pass, Fail }

    public sealed record BlockDiff(long EccBlock, long FirstSector, int BadSectors, string Layer);

    public sealed record Report
    {
        public required Grade Result { get; init; }
        public required long SectorsCompared { get; init; }
        public required long MismatchedSectors { get; init; }
        public required long BadEccBlocks { get; init; }
        public required long FullyBadEccBlocks { get; init; }
        public long L0Mismatches { get; init; }
        public long L1Mismatches { get; init; }
        public long? LayerBreakLba { get; init; }
        public bool LayerBreakConsistent { get; init; } = true;
        public long MissingSectors { get; init; }   // source longer than read-back
        public long ExtraSectors { get; init; }      // read-back longer than source
        public long PaddingSectors { get; init; }    // trailing all-zero extra (benign)
        public string SourceMd5 { get; init; } = "";
        public string ReadbackMd5 { get; init; } = "";
        public bool Md5Match => SourceMd5.Length > 0 && SourceMd5 == ReadbackMd5;
        public required IReadOnlyList<BlockDiff> Examples { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

        public string Summary => Result == Grade.Pass
            ? $"PASS — all {SectorsCompared:N0} sectors match (MD5 {SourceMd5})."
            : $"FAIL — {MismatchedSectors:N0} sector(s) differ across {BadEccBlocks:N0} ECC block(s)" +
              (MissingSectors > 0 ? $", {MissingSectors:N0} missing from the read-back" : "") + ".";
    }

    private const int MaxExamples = 16;

    public static Report Compare(Stream source, Stream readback, long? layerBreakLba = null)
    {
        long srcSectors = source.Length / Sector;
        long rbSectors = readback.Length / Sector;
        long compare = Math.Min(srcSectors, rbSectors);

        var notes = new List<string>();
        bool lbConsistent = true;
        if (layerBreakLba is { } lb)
        {
            if (lb % EccBlock != 0)
            {
                lbConsistent = false;
                notes.Add($"Layer break LBA {lb:N0} is not on a 16-sector ECC-block boundary.");
            }
            if (lb > rbSectors)
            {
                lbConsistent = false;
                notes.Add($"The read-back ends at sector {rbSectors:N0}, before the layer break at {lb:N0} — L0 is incomplete.");
            }
        }

        long mismatched = 0, badBlocks = 0, fullyBad = 0, l0 = 0, l1 = 0;
        var examples = new List<BlockDiff>();

        using var srcMd5 = MD5.Create();
        using var rbMd5 = MD5.Create();
        var sBuf = new byte[EccBlock * Sector];
        var rBuf = new byte[EccBlock * Sector];
        source.Position = 0; readback.Position = 0;

        long sector = 0;
        while (sector < compare)
        {
            int wantSectors = (int)Math.Min(EccBlock, compare - sector);
            int wantBytes = wantSectors * Sector;
            ReadFully(source, sBuf, wantBytes);
            ReadFully(readback, rBuf, wantBytes);

            srcMd5.TransformBlock(sBuf, 0, wantBytes, null, 0);
            rbMd5.TransformBlock(rBuf, 0, wantBytes, null, 0);

            int badInBlock = 0;
            for (int i = 0; i < wantSectors; i++)
            {
                if (!sBuf.AsSpan(i * Sector, Sector).SequenceEqual(rBuf.AsSpan(i * Sector, Sector)))
                {
                    badInBlock++;
                    long abs = sector + i;
                    if (layerBreakLba is { } lbk) { if (abs < lbk) l0++; else l1++; }
                }
            }
            if (badInBlock > 0)
            {
                mismatched += badInBlock;
                badBlocks++;
                if (badInBlock == wantSectors && wantSectors == EccBlock) fullyBad++;
                if (examples.Count < MaxExamples)
                {
                    string layer = layerBreakLba is { } lbk2
                        ? (sector < lbk2 ? "L0" : "L1") : "-";
                    examples.Add(new BlockDiff(sector / EccBlock, sector, badInBlock, layer));
                }
            }
            sector += wantSectors;
        }

        srcMd5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        rbMd5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        // Length handling: trailing extra sectors in the read-back that are all
        // zero are benign padding; anything else is missing/extra content.
        long missing = srcSectors > rbSectors ? srcSectors - rbSectors : 0;
        long extra = rbSectors > srcSectors ? rbSectors - srcSectors : 0;
        long padding = 0;
        if (extra > 0)
        {
            padding = CountTrailingBlankSectors(readback, compare, rbSectors);
            if (padding == extra) notes.Add($"The read-back has {extra:N0} trailing blank (padding) sector(s) — benign.");
            else notes.Add($"The read-back has {extra:N0} extra sector(s), {extra - padding:N0} of them non-blank.");
        }
        if (missing > 0) notes.Add($"The read-back is missing {missing:N0} sector(s) the source has.");

        bool nonPadExtra = extra - padding > 0;
        var grade = (mismatched > 0 || missing > 0 || nonPadExtra || !lbConsistent) ? Grade.Fail : Grade.Pass;

        return new Report
        {
            Result = grade,
            SectorsCompared = compare,
            MismatchedSectors = mismatched,
            BadEccBlocks = badBlocks,
            FullyBadEccBlocks = fullyBad,
            L0Mismatches = l0,
            L1Mismatches = l1,
            LayerBreakLba = layerBreakLba,
            LayerBreakConsistent = lbConsistent,
            MissingSectors = missing,
            ExtraSectors = extra,
            PaddingSectors = padding,
            SourceMd5 = System.Convert.ToHexString(srcMd5.Hash!).ToLowerInvariant(),
            ReadbackMd5 = System.Convert.ToHexString(rbMd5.Hash!).ToLowerInvariant(),
            Examples = examples,
            Notes = notes,
        };
    }

    private static void ReadFully(Stream s, byte[] buf, int count)
    {
        int off = 0;
        while (off < count)
        {
            int n = s.Read(buf, off, count - off);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of stream while comparing.");
            off += n;
        }
    }

    private static long CountTrailingBlankSectors(Stream readback, long from, long total)
    {
        var buf = new byte[Sector];
        long blank = 0;
        for (long s = total - 1; s >= from; s--)
        {
            readback.Position = s * Sector;
            ReadFully(readback, buf, Sector);
            if (buf.AsSpan().IndexOfAnyExcept((byte)0) >= 0) break;   // non-blank: stop
            blank++;
        }
        return blank;
    }
}
