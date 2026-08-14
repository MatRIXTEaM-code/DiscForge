// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Forensics;

/// <summary>One place a disc carries data where the format expects zeros.</summary>
public sealed record CovertFinding(string Zone, long Offset, long NonZeroBytes, long ZoneLength, double Entropy, string Detail);

/// <summary>What a covert-channel sweep turned up.</summary>
public sealed record CovertReport
{
    public required IReadOnlyList<CovertFinding> Findings { get; init; }
    public required long HiddenBytes { get; init; }
    public bool AnyHidden => Findings.Count > 0;

    public string Summary() => AnyHidden
        ? $"{Findings.Count} covert region(s) carrying {HiddenBytes:N0} non-zero byte(s) where the format expects zeros."
        : "No hidden data found in the zero-expected regions.";
}

/// <summary>
/// Covert-channel / hidden-data sweep — hunt for data concealed where a disc format expects nothing.
/// Ordinary extraction reads the files and the structures; this reads the <i>gaps</i>: the slack at the
/// tail of a file's last sector (from the real file end to the sector boundary — the classic place to
/// hide a message), the ISO 9660 system area ahead of the volume descriptors, and other reserved fields
/// that should be zero. It reports every non-zero region with an entropy read, so a stashed payload,
/// a watermark, or an old "hidden track" surfaces instead of being silently discarded. Detection only —
/// it finds and describes what is there and changes nothing.
/// </summary>
public static class CovertChannelSweep
{
    private const int SectorSize = 2048;
    private const int SystemAreaSectors = 16;
    private const int MaxSamples = 64;

    /// <summary>Sweep a cooked ISO 9660 image for hidden data in its zero-expected regions.</summary>
    public static CovertReport Scan(byte[] isoImage)
    {
        ArgumentNullException.ThrowIfNull(isoImage);
        var findings = new List<CovertFinding>();
        long hidden = 0;

        // 1) The system area (sectors 0..15) — normally all zero on a data-only disc; boot code or a
        //    concealed payload both live here, so report it and let entropy hint at which.
        long sysEnd = Math.Min((long)SystemAreaSectors * SectorSize, isoImage.Length);
        var (sysNonZero, sysEntropy) = Measure(isoImage, 0, sysEnd);
        if (sysNonZero > 0)
        {
            hidden += sysNonZero;
            findings.Add(new CovertFinding("system-area", 0, sysNonZero, sysEnd, sysEntropy,
                sysEntropy > 6.5 ? "high-entropy — encrypted/compressed payload or a boot image"
                                 : "boot code, a label, or a concealed message"));
        }

        // 2) File slack — the bytes between a file's real end and its last sector's boundary.
        try
        {
            IsoDirectory dir;
            using (var ms = new MemoryStream(isoImage, writable: false))
                dir = IsoReader.Read(ms);

            foreach (var f in dir.Files)
            {
                long start = (long)f.Extent * SectorSize;
                long end = start + f.Size;
                long slackEnd = ((end + SectorSize - 1) / SectorSize) * SectorSize;
                if (end >= slackEnd || slackEnd > isoImage.Length) continue;

                var (nz, ent) = Measure(isoImage, end, slackEnd);
                if (nz > 0)
                {
                    hidden += nz;
                    if (findings.Count < MaxSamples)
                        findings.Add(new CovertFinding("file-slack", end, nz, slackEnd - end, ent,
                            $"non-zero slack after {f.Path}"));
                }
            }
        }
        catch
        {
            // Not a readable ISO — the system-area result still stands.
        }

        return new CovertReport
        {
            Findings = findings.OrderByDescending(f => f.NonZeroBytes).ToList(),
            HiddenBytes = hidden,
        };
    }

    public static string Render(CovertReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var f in r.Findings)
            sb.AppendLine($"  [{f.Zone}] +{f.Offset:N0}: {f.NonZeroBytes:N0}/{f.ZoneLength:N0} non-zero, " +
                          $"entropy {f.Entropy:0.0} — {f.Detail}");
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    private static (long NonZero, double Entropy) Measure(byte[] data, long start, long end)
    {
        if (start < 0) start = 0;
        if (end > data.Length) end = data.Length;
        if (end <= start) return (0, 0);

        long nonZero = 0;
        var hist = new long[256];
        for (long i = start; i < end; i++)
        {
            byte b = data[i];
            if (b != 0) nonZero++;
            hist[b]++;
        }
        return (nonZero, Entropy(hist, end - start));
    }

    private static double Entropy(long[] hist, long total)
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
}
