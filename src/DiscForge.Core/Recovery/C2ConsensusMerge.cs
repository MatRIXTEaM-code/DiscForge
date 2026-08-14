// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Raw;

namespace DiscForge.Core.Recovery;

/// <summary>How one sector of a C2-guided merge turned out.</summary>
public enum C2Outcome
{
    /// <summary>Every read was byte-identical.</summary>
    Agreed,
    /// <summary>Reassembled from the C2-good bytes of several reads and the result passes its EDC.</summary>
    Recovered,
    /// <summary>Reassembled, but with no EDC to confirm (CD-DA audio / Mode 2 Form 2).</summary>
    BestEffort,
    /// <summary>Still fails its EDC after using every read's good bytes.</summary>
    Unrecovered,
}

public sealed record C2MergeReport
{
    public required int Reads { get; init; }
    public required int Sectors { get; init; }
    public int Agreed { get; set; }
    public int Recovered { get; set; }
    public int BestEffort { get; set; }
    public int Unrecovered { get; set; }
    /// <summary>Sectors that passed EDC only AFTER the byte-level merge — no single read validated on its own.
    /// This is the win C2 byte-consensus buys over a sector-level merge.</summary>
    public int RescuedFromFragments { get; set; }
    /// <summary>Sectors that byte-voting alone left failing EDC, then the sector's OWN Reed-Solomon
    /// Product-Code ECC finished the repair (voting narrows the errors to the RSPC's capacity).</summary>
    public int EccRecovered { get; set; }
    public List<int> UnrecoveredSectors { get; } = new();

    public bool FullyRecovered => Unrecovered == 0;

    public string Summary() =>
        $"{Sectors:N0} sector(s) from {Reads} C2-flagged read(s): {Agreed:N0} agreed, {Recovered:N0} recovered, " +
        $"{BestEffort:N0} best-effort, {Unrecovered:N0} unrecovered" +
        (RescuedFromFragments > 0 ? $"; {RescuedFromFragments:N0} reassembled from fragments no single read held whole" : "") +
        (EccRecovered > 0 ? $"; {EccRecovered:N0} finished by the sector's own RSPC ECC after voting" : "") + ".";
}

public sealed record C2MergeResult(byte[] Image, C2MergeReport Report);

/// <summary>
/// Byte-level C2 consensus recovery. A drive's C2 pointers say WHICH bytes of a raw 2352-byte sector it could
/// not correct; across several reads the uncorrectable bytes move, so a sector no single read got whole can be
/// reassembled from each read's C2-good bytes. This is the recovery redumper and DiscImageCreator do that a
/// sector-level merge cannot — a sector where every read is partly bad, but the union of their good bytes is
/// complete, comes back clean (and its EDC proves it). For each byte position the value is taken from a read
/// whose C2 marks it good (majority when several do); a position no read marks good falls back to a plain
/// majority and the sector is confirmed, or not, by its EDC. When byte-voting alone still fails EDC on a data
/// sector, the residual errors are handed to the sector's OWN Reed-Solomon Product Code (with the no-vouch
/// positions as erasures) — voting narrows the damage into the RSPC's budget, so the two stages together
/// rescue sectors neither manages on its own. Pure read-side recovery; it defeats nothing.
/// </summary>
public static class C2ConsensusMerge
{
    public const int SectorBytes = 2352;
    private const int C2BytesPerSector = 294;
    private const int MaxListed = 4096;

    /// <summary>
    /// Merge several full-image reads using a per-sector C2 stream for each (294 bytes/sector, as redumper/DIC
    /// emit in a <c>.c2</c> file). A null C2 stream means "no pointers for this read" — its bytes are treated as
    /// good but carry no more weight than any other read's.
    /// </summary>
    public static C2MergeResult Merge(IReadOnlyList<byte[]> images, IReadOnlyList<byte[]?> c2Streams)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(c2Streams);
        if (images.Count == 0) throw new ArgumentException("Provide at least one read.", nameof(images));
        if (c2Streams.Count != images.Count)
            throw new ArgumentException("Supply one C2 stream per image (null for a read with no C2).", nameof(c2Streams));

        int len = images[0].Length;
        for (int i = 1; i < images.Count; i++)
            if (images[i].Length != len)
                throw new ArgumentException($"All reads must be the same length; read 1 is {len:N0} bytes, read {i + 1} is {images[i].Length:N0}.");
        if (len % SectorBytes != 0)
            throw new ArgumentException($"Image length {len:N0} is not a whole number of {SectorBytes}-byte sectors.");

        int sectors = len / SectorBytes;
        for (int k = 0; k < c2Streams.Count; k++)
            if (c2Streams[k] is { } s && s.Length != sectors * C2BytesPerSector)
                throw new ArgumentException(
                    $"C2 stream {k + 1} is {s.Length:N0} bytes; expected {(long)sectors * C2BytesPerSector:N0} ({C2BytesPerSector}/sector).");

        var outp = new byte[len];
        var report = new C2MergeReport { Reads = images.Count, Sectors = sectors };
        var maps = new C2ErrorMap[images.Count];

        for (int s = 0; s < sectors; s++)
        {
            int at = s * SectorBytes;
            for (int k = 0; k < images.Count; k++)
                maps[k] = c2Streams[k] is { } cs
                    ? C2ErrorMap.Parse(cs.AsSpan(s * C2BytesPerSector, C2BytesPerSector))
                    : C2ErrorMap.None();

            var outcome = MergeSector(images, maps, at, outp.AsSpan(at, SectorBytes), out bool rescued, out bool eccAssisted);
            switch (outcome)
            {
                case C2Outcome.Agreed: report.Agreed++; break;
                case C2Outcome.Recovered:
                    report.Recovered++;
                    if (rescued) report.RescuedFromFragments++;
                    if (eccAssisted) report.EccRecovered++;
                    break;
                case C2Outcome.BestEffort: report.BestEffort++; break;
                case C2Outcome.Unrecovered:
                    report.Unrecovered++;
                    if (report.UnrecoveredSectors.Count < MaxListed) report.UnrecoveredSectors.Add(s);
                    break;
            }
        }
        return new C2MergeResult(outp, report);
    }

    private static C2Outcome MergeSector(IReadOnlyList<byte[]> images, C2ErrorMap[] maps, int at,
                                         Span<byte> dest, out bool rescued, out bool eccAssisted)
    {
        rescued = false;
        eccAssisted = false;
        int n = images.Count;

        // Fast path: all reads identical.
        bool allSame = true;
        for (int k = 1; k < n && allSame; k++)
            allSame = images[0].AsSpan(at, SectorBytes).SequenceEqual(images[k].AsSpan(at, SectorBytes));
        if (allSame)
        {
            images[0].AsSpan(at, SectorBytes).CopyTo(dest);
            if (DumpMerge.Validate(dest) != false) return C2Outcome.Agreed;
            // Every read agrees on a sector that fails EDC (a systematic read error). If C2 flags
            // any bytes, the sector's own RSPC ECC may still fix it with those as erasures.
            var er = new List<int>();
            for (int i = 0; i < SectorBytes; i++)
                for (int k = 0; k < n; k++)
                    if (maps[k][i]) { er.Add(i); break; }
            if (er.Count > 0 && TryEccCorrect(dest, er)) { eccAssisted = true; return C2Outcome.Recovered; }
            return C2Outcome.Unrecovered;
        }

        // Byte-level assembly: each position from a read whose C2 marks it good (majority among those), else a
        // plain majority across all reads. Positions no read vouched for are the residual erasures.
        var erasures = new List<int>();
        int cap = Math.Min(n, 64);
        Span<byte> good = stackalloc byte[cap];
        Span<byte> all = stackalloc byte[cap];
        for (int i = 0; i < SectorBytes; i++)
        {
            int gc = 0, ac = 0;
            for (int k = 0; k < n; k++)
            {
                byte v = images[k][at + i];
                if (ac < all.Length) all[ac++] = v;
                if (!maps[k][i] && gc < good.Length) good[gc++] = v;
            }
            if (gc > 0) dest[i] = Majority(good[..gc]);
            else { dest[i] = Majority(all[..ac]); erasures.Add(i); }
        }

        bool? edc = DumpMerge.Validate(dest);
        if (edc is null) return C2Outcome.BestEffort;
        if (edc == false)
        {
            // Voting alone still fails EDC — hand the sector's residual errors to its OWN Reed-Solomon
            // Product Code, telling it exactly which bytes no read vouched for (the erasures). Voting has
            // usually narrowed the damage into the RSPC's correction budget, so this rescues sectors a
            // sector-level merge and byte-voting both give up on. Only data sectors carry ECC.
            if (!TryEccCorrect(dest, erasures)) return C2Outcome.Unrecovered;
            eccAssisted = true;
        }

        // EDC now passes (directly or via ECC). Did the merge beat every single read on its own?
        bool anySingleValid = false;
        for (int k = 0; k < n && !anySingleValid; k++)
            anySingleValid = DumpMerge.Validate(images[k].AsSpan(at, SectorBytes)) == true;
        rescued = !anySingleValid;
        return C2Outcome.Recovered;
    }

    /// <summary>After voting, finish a data sector with its own RSPC ECC using the erasure positions.
    /// Mode 1 and Mode 2 Form 1 carry ECC; audio and Mode 2 Form 2 do not (return false). Operates in
    /// place and only reports success when EDC validates afterwards.</summary>
    private static bool TryEccCorrect(Span<byte> sector, IReadOnlyList<int> erasures)
    {
        if (!HasSync(sector)) return false;
        int mode = sector[15];
        if (mode == 1)
            return EccCorrector.CorrectMode1(sector, erasures).EdcValid;
        if (mode == 2)
        {
            // Form 2 (submode bit 0x20) has no ECC; only Form 1 is correctable.
            if ((sector[18] & 0x20) != 0) return false;
            return EccCorrector.CorrectMode2Form1(sector, erasures).EdcValid;
        }
        return false;
    }

    private static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0x00 || s[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }

    private static byte Majority(ReadOnlySpan<byte> vals)
    {
        byte best = vals[0];
        int bestCount = 0;
        for (int a = 0; a < vals.Length; a++)
        {
            int c = 0;
            for (int b = 0; b < vals.Length; b++) if (vals[b] == vals[a]) c++;
            if (c > bestCount) { bestCount = c; best = vals[a]; }   // '>' keeps the earliest read on ties
        }
        return best;
    }
}
