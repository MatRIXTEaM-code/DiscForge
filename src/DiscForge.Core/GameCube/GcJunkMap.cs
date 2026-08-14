// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.GameCube;

/// <summary>How a padding region's contents look.</summary>
public enum JunkClass
{
    /// <summary>High-entropy non-zero bytes — the deterministic disc "junk" a GameCube disc normally carries.</summary>
    Junk,
    /// <summary>Every sampled byte is zero — the junk has been removed (a scrubbed/trimmed image).</summary>
    Zeroed,
    /// <summary>Non-zero but low-entropy — not the junk pattern and not zeros; unexpected in padding.</summary>
    Structured,
}

/// <summary>One contiguous stretch of the disc that is padding rather than game data.</summary>
public sealed record GcJunkRegion
{
    public required long Start { get; init; }
    public required long Length { get; init; }
    /// <summary>What this region abuts before it (for the report: "after FST", "tail", ...).</summary>
    public required string After { get; init; }
    public required JunkClass Class { get; init; }
    /// <summary>Shannon entropy of the sampled bytes, in bits per byte (0..8).</summary>
    public required double EntropyBitsPerByte { get; init; }
    public long End => Start + Length;
}

/// <summary>The padding map of a GameCube image plus an overall verdict on whether its junk is intact.</summary>
public sealed record GcJunkMap
{
    public required long ImageLength { get; init; }
    public required IReadOnlyList<GcJunkRegion> Regions { get; init; }
    public required long TotalPaddingBytes { get; init; }
    /// <summary>Padding bytes in regions large enough to judge (≥ <see cref="GcJunkMapper.SignificantRegionBytes"/>).</summary>
    public required long SignificantPaddingBytes { get; init; }
    public required GcPaddingVerdict Verdict { get; init; }

    public string Summary() => Verdict switch
    {
        GcPaddingVerdict.JunkIntact =>
            $"Padding carries junk as expected across {TotalPaddingBytes:N0} bytes — consistent with an authentic, un-scrubbed dump.",
        GcPaddingVerdict.Scrubbed =>
            $"Padding is zeroed across {SignificantPaddingBytes:N0} significant bytes — the junk has been removed (scrubbed/trimmed). Reconstructable once the junk regenerator is confirmed.",
        GcPaddingVerdict.Mixed =>
            "Mixed padding — some regions carry junk, some are zeroed. Consistent with a partially-scrubbed image.",
        GcPaddingVerdict.Suspicious =>
            "Unexpected structured data sits in padding regions — not junk and not zeros. Worth a closer look (tampering or an unrecognised layout).",
        _ => "No padding regions were found.",
    };
}

/// <summary>Overall reading of a GameCube image's padding.</summary>
public enum GcPaddingVerdict { NoPadding, JunkIntact, Scrubbed, Mixed, Suspicious }

/// <summary>
/// gc-junk-map — map and classify the non-game padding of a GameCube disc image. A GameCube disc fills the
/// gaps between its boot header, apploader, DOL, FST and files — and the tail out to the disc size — with
/// deterministic pseudo-random "junk". This walks the disc's own structures (reusing the existing GCM/boot
/// readers), works out which byte ranges are padding rather than game data, and classifies each: junk present
/// (high entropy), zeroed (scrubbed away), or unexpectedly structured. It is the read-only foundation the junk
/// regenerator builds on — it locates exactly the regions the regenerator must reproduce, and on its own it
/// already tells you whether a dump's padding is intact, scrubbed, or tampered. It reads and classifies only;
/// it reconstructs nothing here and defeats no protection.
/// </summary>
public static class GcJunkMapper
{
    /// <summary>The boot header + bi2 area (boot.bin 0x440 + bi2.bin 0x2000) that always precedes the apploader.</summary>
    public const int BootAreaEnd = 0x2440;

    /// <summary>Regions at least this large drive the overall verdict; smaller alignment gaps are reported but not judged.</summary>
    public const int SignificantRegionBytes = 2048;

    /// <summary>Bytes sampled from the front of a region to classify it (bounds work on a large tail).</summary>
    public const int SampleBytes = 64 * 1024;

    /// <summary>At or above this entropy (bits/byte) a non-zero region reads as genuine junk.</summary>
    public const double JunkEntropyThreshold = 7.5;

    public static GcJunkMap Analyze(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long length = stream.Length;

        // ---- Collect the "used" (game-data) intervals from the disc's own structures. ----
        var used = new List<(long Start, long End)>
        {
            (0, BootAreaEnd), // boot header + bi2
        };

        var header = new byte[0x430];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(header, 0, header.Length);
        long dolOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x420));
        long fstOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x424));
        long fstSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x428));

        // Apploader (header at 0x2440: 0x20 header + code size + trailer size).
        TryAdd(used, () =>
        {
            var a = GcBoot.ReadApploader(stream);
            long total = 0x20 + a.Size + a.TrailerSize;
            return (BootAreaEnd, BootAreaEnd + total);
        });

        // DOL.
        if (dolOffset > 0 && dolOffset < length)
            TryAdd(used, () =>
            {
                var d = GcBoot.ReadDol(stream, dolOffset);
                return (dolOffset, dolOffset + d.TotalSize);
            });

        // FST.
        if (fstOffset > 0 && fstSize > 0 && fstOffset + fstSize <= length)
            used.Add((fstOffset, fstOffset + fstSize));

        // Files.
        GameCubeDisc? disc = null;
        try { stream.Seek(0, SeekOrigin.Begin); disc = GcmReader.Read(stream); } catch { /* keep what we have */ }
        if (disc is not null)
            foreach (var e in disc.Entries)
                if (!e.IsDirectory && e.Size > 0 && e.Offset >= 0 && e.Offset + e.Size <= length)
                    used.Add((e.Offset, e.Offset + e.Size));

        // ---- Complement of the merged used intervals within [0, length] = padding regions. ----
        var merged = Merge(used, length);
        var regions = new List<GcJunkRegion>();
        long cursor = 0;
        foreach (var (s, e) in merged)
        {
            if (s > cursor)
                regions.Add(Classify(stream, cursor, s - cursor, LabelFor(cursor, used)));
            cursor = Math.Max(cursor, e);
        }
        if (cursor < length)
            regions.Add(Classify(stream, cursor, length - cursor, "tail"));

        long totalPad = regions.Sum(r => r.Length);
        long sigPad = regions.Where(r => r.Length >= SignificantRegionBytes).Sum(r => r.Length);
        var verdict = Verdict(regions);

        return new GcJunkMap
        {
            ImageLength = length,
            Regions = regions,
            TotalPaddingBytes = totalPad,
            SignificantPaddingBytes = sigPad,
            Verdict = verdict,
        };
    }

    private static GcPaddingVerdict Verdict(List<GcJunkRegion> regions)
    {
        var sig = regions.Where(r => r.Length >= SignificantRegionBytes).ToList();
        if (sig.Count == 0) return GcPaddingVerdict.NoPadding;
        if (sig.Any(r => r.Class == JunkClass.Structured)) return GcPaddingVerdict.Suspicious;
        bool anyJunk = sig.Any(r => r.Class == JunkClass.Junk);
        bool anyZero = sig.Any(r => r.Class == JunkClass.Zeroed);
        if (anyJunk && anyZero) return GcPaddingVerdict.Mixed;
        return anyZero ? GcPaddingVerdict.Scrubbed : GcPaddingVerdict.JunkIntact;
    }

    private static GcJunkRegion Classify(Stream stream, long start, long length, string after)
    {
        int sample = (int)Math.Min(length, SampleBytes);
        var buf = new byte[sample];
        stream.Seek(start, SeekOrigin.Begin);
        stream.ReadExactly(buf, 0, sample);

        var counts = new int[256];
        bool allZero = true;
        foreach (byte b in buf) { counts[b]++; if (b != 0) allZero = false; }

        double entropy = 0;
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0) continue;
            double p = (double)counts[i] / sample;
            entropy -= p * Math.Log2(p);
        }

        JunkClass cls = allZero ? JunkClass.Zeroed
                      : entropy >= JunkEntropyThreshold ? JunkClass.Junk
                      : JunkClass.Structured;

        return new GcJunkRegion
        {
            Start = start, Length = length, After = after, Class = cls, EntropyBitsPerByte = entropy,
        };
    }

    private static void TryAdd(List<(long, long)> used, Func<(long, long)> f)
    {
        try { used.Add(f()); } catch { /* structure unreadable; skip it */ }
    }

    private static List<(long Start, long End)> Merge(List<(long Start, long End)> intervals, long clamp)
    {
        var clean = intervals
            .Select(i => (Start: Math.Max(0, i.Start), End: Math.Min(clamp, i.End)))
            .Where(i => i.End > i.Start)
            .OrderBy(i => i.Start)
            .ToList();

        var merged = new List<(long Start, long End)>();
        foreach (var i in clean)
        {
            if (merged.Count > 0 && i.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, i.End));
            else
                merged.Add(i);
        }
        return merged;
    }

    private static string LabelFor(long gapStart, List<(long Start, long End)> used)
    {
        // Name the region by the structure that ends nearest before it.
        long bestEnd = -1; string label = "padding";
        foreach (var (_, e) in used)
            if (e <= gapStart && e > bestEnd) bestEnd = e;
        if (bestEnd == GcJunkMapper.BootAreaEnd) label = "after boot/bi2";
        else if (bestEnd > 0) label = $"after data@0x{bestEnd:X}";
        return label;
    }
}
