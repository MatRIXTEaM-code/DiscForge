// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>What kind of matter a block of a disc image is made of.</summary>
public enum MatterClass : byte
{
    Zero = 0,          // padding / empty
    Text = 1,          // printable ASCII
    Structured = 2,    // low-to-mid entropy binary (tables, headers, uncompressed assets)
    HighEntropy = 3,   // compressed or encrypted
}

/// <summary>One classified block of the image.</summary>
public sealed record MatterBlock(long Offset, int Length, MatterClass Class, double Entropy);

/// <summary>A map of what an image is made of, block by block.</summary>
public sealed record MatterMap
{
    public required IReadOnlyList<MatterBlock> Blocks { get; init; }
    public required int BlockSize { get; init; }
    public required IReadOnlyDictionary<MatterClass, long> Bytes { get; init; }

    public string Summary()
    {
        long total = Bytes.Values.Sum();
        if (total == 0) return "Empty image.";
        string Part(MatterClass c) => $"{c} {100.0 * Bytes.GetValueOrDefault(c) / total:0}%";
        return $"{total:N0} bytes: {Part(MatterClass.Zero)}, {Part(MatterClass.Text)}, " +
               $"{Part(MatterClass.Structured)}, {Part(MatterClass.HighEntropy)}.";
    }
}

/// <summary>
/// Sector "matter" map — classify what <i>kind</i> of data each region of an image holds, so a disc's
/// anatomy is visible at a glance. From each block's entropy and byte distribution it labels the region
/// as padding (zeros), text, structured binary (headers, tables, uncompressed assets), or high-entropy
/// (compressed or encrypted), and renders the whole disc as a coloured strip — the way the health map
/// shows damage, this shows composition. It tells you the shape of the disc without opening a single
/// file: "this 200 MB band is compressed/encrypted, this is text, this is structured data." It reads
/// and classifies; it never decrypts or decompresses anything.
/// </summary>
public static class SectorMatterMap
{
    private const int DefaultBlock = 2048;

    public static MatterMap Analyze(byte[] data, int blockSize = DefaultBlock)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (blockSize < 16) blockSize = 16;

        var blocks = new List<MatterBlock>();
        var bytes = new Dictionary<MatterClass, long>
        {
            [MatterClass.Zero] = 0, [MatterClass.Text] = 0,
            [MatterClass.Structured] = 0, [MatterClass.HighEntropy] = 0,
        };

        for (long off = 0; off < data.Length; off += blockSize)
        {
            int len = (int)Math.Min(blockSize, data.Length - off);
            var (cls, entropy) = Classify(data, off, len);
            blocks.Add(new MatterBlock(off, len, cls, entropy));
            bytes[cls] += len;
        }

        return new MatterMap { Blocks = blocks, BlockSize = blockSize, Bytes = bytes };
    }

    /// <summary>Render the map as a standalone SVG strip.</summary>
    public static string RenderSvg(MatterMap map, string title, int columns = 128, int cellPx = 6)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (columns < 1) columns = 1;
        if (cellPx < 1) cellPx = 1;

        int n = map.Blocks.Count;
        int rows = Math.Max(1, (n + columns - 1) / columns);
        const int pad = 16, titleH = 28, legendH = 24;
        int width = columns * cellPx + pad * 2;
        int height = titleH + rows * cellPx + legendH + pad * 2;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" " +
            $"viewBox=\"0 0 {width} {height}\" font-family=\"system-ui,sans-serif\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{width}\" height=\"{height}\" fill=\"#0f1115\"/>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{pad}\" y=\"20\" fill=\"#e6e6e6\" font-size=\"14\" font-weight=\"600\">{Escape(title)}</text>\n");

        int ox = pad, oy = titleH + pad;
        for (int i = 0; i < n; i++)
        {
            int cx = ox + (i % columns) * cellPx;
            int cy = oy + (i / columns) * cellPx;
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{cx}\" y=\"{cy}\" width=\"{cellPx}\" height=\"{cellPx}\" fill=\"{Colour(map.Blocks[i].Class)}\"/>\n");
        }

        int lx = pad, ly = oy + rows * cellPx + 16;
        foreach (MatterClass c in Enum.GetValues<MatterClass>())
        {
            string label = $"{c}";
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{lx}\" y=\"{ly - 10}\" width=\"11\" height=\"11\" fill=\"{Colour(c)}\"/>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{lx + 15}\" y=\"{ly}\" fill=\"#c8c8c8\" font-size=\"11\">{Escape(label)}</text>\n");
            lx += 20 + label.Length * 7;
        }

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    // ---- internals ----------------------------------------------------------

    private static (MatterClass, double) Classify(byte[] data, long off, int len)
    {
        var hist = new int[256];
        int printable = 0, zero = 0;
        for (int i = 0; i < len; i++)
        {
            byte b = data[off + i];
            hist[b]++;
            if (b == 0) zero++;
            if (b is (>= 0x20 and < 0x7F) or 0x09 or 0x0A or 0x0D) printable++;
        }

        double zeroRatio = zero / (double)len;
        double printRatio = printable / (double)len;
        double entropy = Entropy(hist, len);

        if (zeroRatio >= 0.99) return (MatterClass.Zero, entropy);
        if (printRatio >= 0.85 && entropy < 6.0) return (MatterClass.Text, entropy);
        if (entropy >= 7.5) return (MatterClass.HighEntropy, entropy);
        return (MatterClass.Structured, entropy);
    }

    private static double Entropy(int[] hist, int total)
    {
        if (total <= 0) return 0;
        double e = 0;
        foreach (var c in hist)
        {
            if (c == 0) continue;
            double p = c / (double)total;
            e -= p * Math.Log2(p);
        }
        return e;
    }

    private static string Colour(MatterClass c) => c switch
    {
        MatterClass.Zero => "#37474f",         // slate (padding)
        MatterClass.Text => "#2e7d32",         // green
        MatterClass.Structured => "#1565c0",   // blue
        MatterClass.HighEntropy => "#c62828",  // red (compressed/encrypted)
        _ => "#000000",
    };

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
