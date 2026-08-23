// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;
using DiscForge.Core.Preservation;
using DiscForge.Core.Raw;
using DiscForge.Core.Util;

namespace DiscForge.Core.Forensics;

/// <summary>
/// Disc MRI — the per-sector evidence map drawn on the PHYSICAL disc.
///
/// <see cref="DiscHealthMap"/> shows health on a linear grid: good for
/// spotting that damage exists, useless for seeing what it IS. This renderer
/// places every sector where it actually lives on the polycarbonate, by
/// unwinding the real recording spiral (Red Book geometry: program area
/// starting at radius 25 mm, 1.6 µm track pitch, constant linear velocity
/// ≈1.2 m/s ⇒ ~16 mm of track per sector). On that map, physical causes have
/// physical shapes: a radial scratch crosses many spiral turns at one angle
/// and appears as a radial streak; a pressing defect follows one turn and
/// draws a ring; rot blooms outward from the hub; a drive that muted the back
/// half of a disc paints a solid outer annulus. Diagnosis becomes something
/// you can see with your eyes.
///
/// Rendering is per-PIXEL, not per-sector: each pixel inverts the spiral to
/// find every sector passing under it (~60 turns per pixel at typical sizes)
/// and takes the WORST evidence among them, so a single damaged sector is
/// never averaged away. The honesty rule, applied to pictures.
/// </summary>
public static class DiscMri
{
    /// <summary>What we can prove about one sector, ordered by severity —
    /// higher values win when many sectors share a pixel.</summary>
    public enum Evidence : byte
    {
        /// <summary>Nothing known (outside the dump, or unclassified).</summary>
        Untested = 0,
        /// <summary>Audio sector, entirely zero — legitimate digital silence.</summary>
        AudioSilence = 1,
        /// <summary>Audio sector with content (or sync-less content in an
        /// unlabelled image, where audio and void cannot be told apart).</summary>
        Audio = 2,
        /// <summary>Structured data sector with nothing checkable (formless Mode 2).</summary>
        NoEdc = 3,
        /// <summary>Data sector, sync present, EDC validates — proven.</summary>
        DataGood = 4,
        /// <summary>Track-boundary transition sector — geometry, not damage.</summary>
        Boundary = 5,
        /// <summary>Data sector whose EDC fails — damaged content.</summary>
        EdcFailed = 6,
        /// <summary>Sector in a KNOWN data span with no sync pattern — a void
        /// (the muted-drive signature) or foreign content.</summary>
        SynclessVoid = 7,
        /// <summary>Recorded unreadable in the dump's bad-sector sidecar.</summary>
        Unreadable = 8,
    }

    // ---- Red Book physical geometry ----------------------------------------

    private const double InnerRadius = 0.025;      // program area starts at 25 mm
    private const double TrackPitch = 1.6e-6;      // 1.6 µm between spiral turns
    private const double SectorLength = 0.0160;    // 1.2 m/s CLV / 75 sectors/s = 16 mm
    private const double DiscRadius = 0.060;       // 120 mm disc
    private const double HoleRadius = 0.0075;      // 15 mm centre hole

    /// <summary>Physical location of a sector: radius in metres and total
    /// unwound spiral angle in radians (angle mod 2π is the angular position).</summary>
    public static (double Radius, double Theta) Locate(long sector)
    {
        // Archimedean spiral r(θ) = r0 + (p/2π)θ; arc length s(θ) = r0·θ + (p/4π)θ².
        // Invert s = sector·Ls for θ (quadratic, positive root).
        double s = sector * SectorLength;
        double a = TrackPitch / (4 * Math.PI);
        double theta = (-InnerRadius + Math.Sqrt(InnerRadius * InnerRadius + 4 * a * s)) / (2 * a);
        return (InnerRadius + TrackPitch * theta / (2 * Math.PI), theta);
    }

    /// <summary>The sector whose spiral pass lies nearest (radius, angle).
    /// <paramref name="angle"/> in [0, 2π). Returns -1 outside the program spiral.</summary>
    public static long SectorAt(double radius, double angle, long totalSectors)
    {
        if (radius < InnerRadius) return -1;
        double turns = (radius - InnerRadius) / TrackPitch;
        double frac = angle / (2 * Math.PI);
        long k = (long)Math.Round(turns - frac);
        if (k < 0) k = 0;
        double t = k + frac;                                   // total turns at this pass
        double s = 2 * Math.PI * InnerRadius * t + Math.PI * TrackPitch * t * t;
        long sector = (long)(s / SectorLength);
        return sector >= 0 && sector < totalSectors ? sector : -1;
    }

    // ---- classification ----------------------------------------------------

    /// <summary>
    /// Classify every 2352-byte sector of a raw image. <paramref name="spans"/>
    /// (file-sector coordinates, inclusive ends) supplies per-range audio/data
    /// knowledge when a cue or TOC is available; without it, sync decides and
    /// sync-less content is honestly ambiguous (rendered as audio, never as
    /// proof). <paramref name="map"/> overlays the dump's recorded unreadable
    /// and boundary sectors; <paramref name="baseLba"/> is the LBA of file
    /// sector 0 (dumps starting at track 1 use 0).
    /// </summary>
    public static Evidence[] Classify(Stream image,
        IReadOnlyList<(long Start, long End, bool Audio)>? spans = null,
        BadSectorMap? map = null, long baseLba = 0)
    {
        long total = image.Length / 2352;
        var ev = new Evidence[total];
        var main = new byte[2352];

        for (long i = 0; i < total; i++)
        {
            image.Position = i * 2352;
            image.ReadExactly(main, 0, 2352);
            bool? audio = null;
            if (spans is not null)
                foreach (var sp in spans)
                    if (i >= sp.Start && i <= sp.End) { audio = sp.Audio; break; }

            bool zero = IsAllZero(main);
            bool sync = HasSync(main);
            ev[i] = audio switch
            {
                true => zero ? Evidence.AudioSilence : Evidence.Audio,
                false => !sync ? Evidence.SynclessVoid : CheckData(main),
                null => sync ? CheckData(main)
                             : zero ? Evidence.AudioSilence : Evidence.Audio,
            };
        }

        if (map is not null)
        {
            foreach (long lba in map.UnreadableLba)
            {
                long i = lba - baseLba;
                if (i >= 0 && i < total) ev[i] = Evidence.Unreadable;
            }
            foreach (long lba in map.BoundaryLba)
            {
                long i = lba - baseLba;
                if (i >= 0 && i < total) ev[i] = Evidence.Boundary;
            }
        }
        return ev;
    }

    private static Evidence CheckData(ReadOnlySpan<byte> main)
    {
        byte mode = main[15];
        if (mode == 1)
            return EdcEcc.VerifyMode1(main).EdcOk ? Evidence.DataGood : Evidence.EdcFailed;
        if (mode == 2)
        {
            if (main[16] != main[20] || main[17] != main[21] ||
                main[18] != main[22] || main[19] != main[23]) return Evidence.NoEdc;
            bool form2 = (main[18] & 0x20) != 0;
            if (!form2)
            {
                uint edc = EdcEcc.ComputeEdc(main[16..2072]);
                uint stored = (uint)main[2072] | ((uint)main[2073] << 8)
                            | ((uint)main[2074] << 16) | ((uint)main[2075] << 24);
                return edc == stored ? Evidence.DataGood : Evidence.EdcFailed;
            }
            uint edc2 = EdcEcc.ComputeEdc(main[16..2348]);
            uint stored2 = (uint)main[2348] | ((uint)main[2349] << 8)
                         | ((uint)main[2350] << 16) | ((uint)main[2351] << 24);
            if (stored2 == 0) return Evidence.NoEdc;
            return edc2 == stored2 ? Evidence.DataGood : Evidence.EdcFailed;
        }
        return Evidence.NoEdc;
    }

    // ---- rendering ---------------------------------------------------------

    /// <summary>Render the polar map as a PNG (RGBA). Worst evidence wins per pixel.</summary>
    public static byte[] RenderPng(IReadOnlyList<Evidence> evidence, int sizePx = 1200)
        => PngWriter.EncodeRgba(RenderRgba(evidence, ref sizePx), sizePx, sizePx);

    /// <summary>The raw square RGBA raster behind <see cref="RenderPng"/> —
    /// public so tests (and live views) can sample pixels without decoding PNG.
    /// <paramref name="sizePx"/> is clamped to a sane minimum in place.</summary>
    public static byte[] RenderRgba(IReadOnlyList<Evidence> evidence, ref int sizePx)
    {
        if (sizePx < 64) sizePx = 64;
        long total = evidence.Count;
        double outerR = total > 0 ? Locate(total - 1).Radius : InnerRadius;

        int margin = 8;
        double canvasR = sizePx / 2.0 - margin;
        double scale = DiscRadius / canvasR;                   // metres per pixel
        double cx = sizePx / 2.0, cy = sizePx / 2.0;
        // Half the number of spiral turns a pixel spans radially (+1 guard).
        int halfTurns = (int)Math.Ceiling(scale / 2 / TrackPitch) + 1;

        var rgba = new byte[(long)sizePx * sizePx * 4];
        var (bgR, bgG, bgB) = (0x0f, 0x11, 0x15);              // page background
        var (discR, discG, discB) = (0x1b, 0x1f, 0x27);        // unwritten polycarbonate
        var (hubR, hubG, hubB) = (0x2a, 0x2f, 0x3a);           // clamping/hub area

        for (int y = 0; y < sizePx; y++)
        for (int x = 0; x < sizePx; x++)
        {
            double dx = (x - cx) * scale, dy = (y - cy) * scale;
            double rr = Math.Sqrt(dx * dx + dy * dy);
            byte r, g, b;
            if (rr > DiscRadius || rr < HoleRadius)
                (r, g, b) = ((byte)bgR, (byte)bgG, (byte)bgB);
            else if (rr < InnerRadius)
                (r, g, b) = ((byte)hubR, (byte)hubG, (byte)hubB);
            else if (rr > outerR)
                (r, g, b) = ((byte)discR, (byte)discG, (byte)discB);
            else
            {
                double angle = Math.Atan2(dy, dx);
                if (angle < 0) angle += 2 * Math.PI;
                var worst = Evidence.Untested;
                bool hit = false;
                for (int k = -halfTurns; k <= halfTurns; k++)
                {
                    long sec = SectorAt(rr + k * TrackPitch, angle, total);
                    if (sec < 0) continue;
                    hit = true;
                    if (evidence[(int)sec] > worst) worst = evidence[(int)sec];
                }
                (r, g, b) = hit ? Colour(worst) : ((byte)discR, (byte)discG, (byte)discB);
            }
            long o = ((long)y * sizePx + x) * 4;
            rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = 255;
        }

        return rgba;
    }

    /// <summary>Render as a standalone SVG: the polar PNG embedded, plus a
    /// title and a legend with true per-class counts.</summary>
    public static string RenderSvg(IReadOnlyList<Evidence> evidence, string title, int sizePx = 1200)
    {
        if (sizePx < 64) sizePx = 64;
        var png = RenderPng(evidence, sizePx);
        var totals = new long[9];
        foreach (var e in evidence) totals[(int)e]++;

        const int pad = 16, titleH = 30;
        int legendH = 26 + 16;
        int width = sizePx + pad * 2;
        int height = titleH + sizePx + legendH + pad * 2;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" " +
            $"viewBox=\"0 0 {width} {height}\" font-family=\"system-ui,Segoe UI,sans-serif\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{width}\" height=\"{height}\" fill=\"#0f1115\"/>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{pad}\" y=\"21\" fill=\"#e6e6e6\" font-size=\"15\" font-weight=\"600\">{Escape(title)}</text>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<image x=\"{pad}\" y=\"{titleH}\" width=\"{sizePx}\" height=\"{sizePx}\" " +
            $"href=\"data:image/png;base64,{System.Convert.ToBase64String(png)}\"/>\n");

        int lx = pad, ly = titleH + sizePx + 22;
        foreach (Evidence e in Enum.GetValues<Evidence>())
        {
            if (totals[(int)e] == 0) continue;
            var (r, g, b) = Colour(e);
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{lx}\" y=\"{ly - 10}\" width=\"11\" height=\"11\" fill=\"#{r:x2}{g:x2}{b:x2}\"/>\n");
            string label = $"{Label(e)} {totals[(int)e]:N0}";
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{lx + 15}\" y=\"{ly}\" fill=\"#c8c8c8\" font-size=\"11\">{Escape(label)}</text>\n");
            lx += 18 + label.Length * 7;
            if (lx > width - 150) { lx = pad; ly += 16; }
        }
        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{pad}\" y=\"{height - 6}\" fill=\"#7a7a7a\" font-size=\"10\">polar physical map — LBA 0 at the hub, spiral outward; worst evidence wins per pixel, so damage never hides. Radial streak = scratch; ring = pressing defect; bloom = rot.</text>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    // ---- palette -----------------------------------------------------------

    private static (byte, byte, byte) Colour(Evidence e) => e switch
    {
        Evidence.DataGood => (0x2e, 0x7d, 0x32),       // green — proven
        Evidence.Audio => (0x54, 0x6e, 0x7a),          // blue-grey — audio content
        Evidence.AudioSilence => (0x37, 0x47, 0x55),   // darker blue-grey — silence
        Evidence.NoEdc => (0x60, 0x7d, 0x8b),          // grey-teal — nothing checkable
        Evidence.Boundary => (0x7e, 0x57, 0xc2),       // violet — geometry
        Evidence.EdcFailed => (0xf9, 0xa8, 0x25),      // amber — damaged content
        Evidence.SynclessVoid => (0xc6, 0x28, 0x28),   // red — void in a data span
        Evidence.Unreadable => (0xb7, 0x1c, 0x1c),     // deep red — recorded hole
        _ => (0x37, 0x47, 0x4f),                       // untested
    };

    private static string Label(Evidence e) => e switch
    {
        Evidence.DataGood => "data proven",
        Evidence.Audio => "audio",
        Evidence.AudioSilence => "silence",
        Evidence.NoEdc => "no EDC",
        Evidence.Boundary => "boundary",
        Evidence.EdcFailed => "EDC failed",
        Evidence.SynclessVoid => "sync-less void",
        Evidence.Unreadable => "unreadable",
        _ => "untested",
    };

    private static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0 || s[11] != 0) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> s)
    {
        for (int i = 0; i < s.Length; i++) if (s[i] != 0) return false;
        return true;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
