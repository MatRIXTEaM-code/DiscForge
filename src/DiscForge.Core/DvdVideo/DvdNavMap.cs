// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.DvdVideo;

/// <summary>One Program Chain in a title set, and how (if at all) it is reachable.</summary>
public sealed record PgcInfo(int Number, bool IsEntry, int TitleNumber, bool Referenced)
{
    public override string ToString()
    {
        string how = IsEntry ? $"entry (title {TitleNumber})" : Referenced ? "referenced by a title" : "UNREFERENCED";
        return $"PGC {Number}: {how}";
    }
}

/// <summary>The navigation map of a VTS: its program chains and which are reachable.</summary>
public sealed record DvdNavReport
{
    public required int PgcCount { get; init; }
    public required IReadOnlyList<PgcInfo> Pgcs { get; init; }
    public IReadOnlyList<int> HiddenPgcs => Pgcs.Where(p => !p.Referenced).Select(p => p.Number).ToList();
    public bool HasHidden => Pgcs.Any(p => !p.Referenced);

    public string Summary()
    {
        if (PgcCount == 0) return "No program chains found.";
        int hidden = HiddenPgcs.Count;
        return hidden == 0
            ? $"{PgcCount} program chain(s), all reachable."
            : $"{PgcCount} program chain(s) — {hidden} UNREFERENCED (content no title or menu points at).";
    }
}

/// <summary>
/// DVD navigation / hidden-PGC map — read the unencrypted navigation tables of a VTS IFO and find the
/// content nothing points at. Every playable segment is a Program Chain (PGC); titles reach their PGCs
/// through the part-of-title table, and some PGCs are marked as title entry points. A PGC that is in the
/// table but is neither an entry point nor referenced by any title is <i>unreferenced</i> — physically on
/// the disc yet unreachable by normal navigation: a hidden cut, a developer leftover, a region-gated
/// sequence. This maps every PGC and flags those orphans. It reads the IFO's table of contents (never
/// encrypted, never the content); it decodes and defeats nothing.
/// </summary>
public static class DvdNavMap
{
    private const int SectorSize = 2048;
    // VTS_MAT sector-pointer offsets (32-bit big-endian, relative to the VTS IFO start).
    private const int VtsPttSrptPtr = 0x80;   // part-of-title search pointer table
    private const int VtsPgcitPtr = 0xCC;     // program chain information table

    public static DvdNavReport Analyze(byte[] vtsIfo)
    {
        ArgumentNullException.ThrowIfNull(vtsIfo);
        if (vtsIfo.Length < 0x100 || !HasMagic(vtsIfo))
            throw new IfoFormatException("Not a VTS IFO (missing DVDVIDEO-VTS signature).");

        long pgcitStart = (long)U32(vtsIfo, VtsPgcitPtr) * SectorSize;
        if (pgcitStart <= 0 || pgcitStart + 8 > vtsIfo.Length)
            return new DvdNavReport { PgcCount = 0, Pgcs = System.Array.Empty<PgcInfo>() };

        int pgcCount = U16(vtsIfo, (int)pgcitStart);
        // Guard against a corrupt count: each search pointer is 8 bytes from +8.
        long srpBase = pgcitStart + 8;
        int maxFit = (int)Math.Max(0, (vtsIfo.Length - srpBase) / 8);
        if (pgcCount > maxFit) pgcCount = maxFit;

        var entryTitle = new int[pgcCount + 1];   // 1-based; 0 = not an entry PGC
        for (int i = 0; i < pgcCount; i++)
        {
            long srp = srpBase + (long)i * 8;
            byte entryId = vtsIfo[(int)srp];
            if ((entryId & 0x80) != 0) entryTitle[i + 1] = entryId & 0x7F;
        }

        var referenced = new HashSet<int>();
        for (int n = 1; n <= pgcCount; n++)
            if (entryTitle[n] != 0) referenced.Add(n);

        foreach (var pgcn in CollectPttPgcns(vtsIfo))
            if (pgcn >= 1 && pgcn <= pgcCount) referenced.Add(pgcn);

        var pgcs = new List<PgcInfo>(pgcCount);
        for (int n = 1; n <= pgcCount; n++)
            pgcs.Add(new PgcInfo(n, entryTitle[n] != 0, entryTitle[n], referenced.Contains(n)));

        return new DvdNavReport { PgcCount = pgcCount, Pgcs = pgcs };
    }

    public static string Render(DvdNavReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var p in r.Pgcs) sb.AppendLine($"  {p}");
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    // Every PGC number reachable through the part-of-title search pointer table.
    private static IEnumerable<int> CollectPttPgcns(byte[] vts)
    {
        long pttStart = (long)U32(vts, VtsPttSrptPtr) * SectorSize;
        if (pttStart <= 0 || pttStart + 8 > vts.Length) yield break;

        int titleCount = U16(vts, (int)pttStart);
        long ea = U32(vts, (int)pttStart + 4);                 // last byte address, relative to pttStart
        long tableEnd = Math.Min(vts.Length, pttStart + ea + 1);

        long offsetsBase = pttStart + 8;
        if (offsetsBase + (long)titleCount * 4 > vts.Length) yield break;

        for (int t = 0; t < titleCount; t++)
        {
            long thisOff = pttStart + U32(vts, (int)(offsetsBase + (long)t * 4));
            long nextOff = t + 1 < titleCount
                ? pttStart + U32(vts, (int)(offsetsBase + (long)(t + 1) * 4))
                : tableEnd;
            if (thisOff < pttStart || thisOff >= vts.Length) continue;
            long end = Math.Min(Math.Max(nextOff, thisOff), vts.Length);

            for (long e = thisOff; e + 4 <= end; e += 4)       // PTT entries: PGCN (BE16), PGN (BE16)
                yield return U16(vts, (int)e);
        }
    }

    private static bool HasMagic(byte[] b)
    {
        var sig = Encoding.ASCII.GetBytes("DVDVIDEO-VTS");
        if (b.Length < sig.Length) return false;
        for (int i = 0; i < sig.Length; i++) if (b[i] != sig[i]) return false;
        return true;
    }

    private static int U16(byte[] b, int o) =>
        o + 2 <= b.Length ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o)) : 0;

    private static uint U32(byte[] b, int o) =>
        o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o)) : 0;
}
