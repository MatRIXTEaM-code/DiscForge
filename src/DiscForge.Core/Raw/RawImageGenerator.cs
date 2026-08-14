// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;

namespace DiscForge.Core.Raw;

/// <summary>Which physical sub-channel layout to emit per sector.</summary>
public enum RawSubcodeForm
{
    /// <summary>2368 bytes/sector: main + formatted P-Q. Most widely supported;
    /// cannot carry CD-TEXT (no R-W symbols).</summary>
    Pq16,
    /// <summary>2448: main + packed (de-interleaved) P-W. IMAPI2's default.</summary>
    Packed96,
    /// <summary>2448: main + fully interleaved raw P-W.</summary>
    Interleaved96,
}

/// <summary>
/// Generates a complete disc-at-once raw image — the exact byte stream a
/// DAO-96/DAO-16 write consumes: 22,500 lead-in sectors (from MSF 95:00:00,
/// carrying the TOC in Q and CD-TEXT in R-W) followed by the program area
/// (from MSF 00:00:00; track 1's pregap, every track, every index, every gap,
/// exactly as the layout says). The drive appends the lead-out itself.
///
/// This is where the CUE semantics that TAO can't honour become real: index
/// points land at their exact sectors, gaps are the length the sheet says,
/// ISRC and MCN ride the Q channel, and data sectors go out scrambled with
/// their EDC/ECC intact (or freshly computed when the source stored 2048s).
///
/// The generator is pure and streaming: any Stream in, image out, no
/// hardware — which is what makes the whole RAW pipeline unit-testable on
/// any platform.
/// </summary>
public static class RawImageGenerator
{
    public const int LeadInSectors = 22_500;                 // default: MSF 95:00:00 → 00:00:00
    private const int MsfWrapSectors = 450_000;              // 100:00:00 (99:59:74 + 1)

    public static int SectorSize(RawSubcodeForm form)
        => form == RawSubcodeForm.Pq16 ? 2368 : 2448;

    /// <summary>Total sectors the image will contain (lead-in + program). The lead-in length
    /// defaults to 22,500 but can be sized to the drive's ATIP-derived lead-in start (NWA) so the
    /// program area lands at physical LBA 0.</summary>
    public static long TotalSectors(DiscLayout layout, int leadInSectors = LeadInSectors)
        => leadInSectors + ProgramSectors(layout);

    public static long ProgramSectors(DiscLayout layout)
        => layout.Tracks.Sum(t => (long)t.TotalSectors);

    /// <summary>Generate the full image to <paramref name="output"/>. <paramref name="leadInSectors"/>
    /// sizes the lead-in: the default 22,500 starts the running Q at MSF 95:00:00, while a shorter
    /// drive-dictated length (from the disc's ATIP / the drive's raw-mode NWA) starts it later so
    /// the lead-in still ends at MSF 00:00:00 (LBA −150) and the program lands at LBA 0.</summary>
    public static void Generate(DiscLayout layout, RawSubcodeForm form, Stream output,
                                IProgress<double>? progress = null, int leadInSectors = LeadInSectors)
    {
        if (layout.Tracks.Count == 0)
            throw new InvalidDataException("The layout has no tracks.");
        if (layout.Tracks.Count > 99)
            throw new InvalidDataException("A CD holds at most 99 tracks.");
        if (leadInSectors < 1 || leadInSectors >= MsfWrapSectors)
            throw new ArgumentOutOfRangeException(nameof(leadInSectors),
                $"lead-in length must be 1..{MsfWrapSectors - 1} sectors.");

        int subSize = SectorSize(form) - 2352;
        long program = ProgramSectors(layout);
        long total = leadInSectors + program;

        // Derive the lead-in start MSF from its length so the last lead-in sector is 99:59:74 and
        // the program's first pregap sector is 00:00:00 (LBA −150): start = 100:00:00 − length.
        var leadInStart = Msf.FromSectors(MsfWrapSectors - leadInSectors);

        // Track start positions in program sectors (index 1 positions).
        var index1Start = new int[layout.Tracks.Count];
        {
            int p = 0;
            for (int i = 0; i < layout.Tracks.Count; i++)
            {
                p += layout.Tracks[i].PregapTotalSectors;
                index1Start[i] = p;
                p += layout.Tracks[i].LengthSectors + layout.Tracks[i].PostgapSectors;
            }
        }
        var leadOutStart = ProgramMsf(program);

        var cdTextPacks = form == RawSubcodeForm.Pq16
            ? Array.Empty<byte[]>()
            : CdTextBuilder.BuildPacks(layout.CdText,
                layout.Tracks[0].Number, layout.Tracks[^1].Number);

        var main = new byte[2352];
        var stored = new byte[2352];
        var sub = new byte[96];
        var frame = new SubcodeFrame();
        long written = 0;

        // ---- lead-in -------------------------------------------------------

        var toc = BuildTocCycle(layout, leadOutStart, index1Start);
        for (int s = 0; s < leadInSectors; s++)
        {
            Array.Clear(main);
            var running = Msf.FromSectors(leadInStart.ToSectors() + s);
            var entry = toc[s % toc.Count];
            frame.P = false;
            frame.Q = entry(running);
            if (cdTextPacks.Length > 0) CdTextBuilder.FillSectorRw(cdTextPacks, s, frame.Rw);
            else Array.Clear(frame.Rw);

            Emit(output, main, frame, form, sub, subSize);
            if ((++written & 0x3FF) == 0) progress?.Report(written / (double)total);
        }

        // ---- program area --------------------------------------------------

        long p2 = 0;                                        // program sector counter
        for (int ti = 0; ti < layout.Tracks.Count; ti++)
        {
            var t = layout.Tracks[ti];

            // Pregap: generated silence first, then any stored pregap sectors.
            int pregap = t.PregapTotalSectors;
            for (int g = 0; g < pregap; g++, p2++)
            {
                var abs = ProgramMsf(p2);
                bool storedPart = g >= t.PregapGeneratedSectors;
                if (storedPart)
                    ReadStored(t, g - t.PregapGeneratedSectors, stored, main, abs);
                else
                    FillGenerated(t, main, abs);

                // Countdown: N..1 over the pregap, 0 at index 1's first sector.
                frame.P = true;
                frame.Q = SubQ.Position(t.Control, t.Number, index: 0,
                    relative: Msf.FromSectors(pregap - g), absolute: abs);
                Array.Clear(frame.Rw);
                if (storedPart) FillRwFromSub(t, g - t.PregapGeneratedSectors, frame);
                Emit(output, main, frame, form, sub, subSize);
                if ((++written & 0x3FF) == 0) progress?.Report(written / (double)total);
            }

            // Index 1 onward: stored data, then postgap.
            int lengthAll = t.LengthSectors + t.PostgapSectors;
            for (int r = 0; r < lengthAll; r++, p2++)
            {
                var abs = ProgramMsf(p2);
                bool postgap = r >= t.LengthSectors;
                if (postgap) FillGenerated(t, main, abs);
                else ReadStored(t, t.PregapStoredSectors + r, stored, main, abs);

                int index = 1;
                foreach (var ix in t.ExtraIndexes)
                    if (r >= ix.OffsetSectors) index = ix.Number;

                // Verbatim mode: the source's own subcode frame is authoritative
                // (protection lives in its deliberately-corrupt Q). Emit it byte
                // for byte and skip our Q/P/R-W generation entirely.
                if (!postgap && t is { SubVerbatim: true, SubSource: not null })
                {
                    EmitVerbatim(output, main, t, t.PregapStoredSectors + r, form, sub, subSize);
                    if ((++written & 0x3FF) == 0) progress?.Report(written / (double)total);
                    continue;
                }

                frame.P = false;
                frame.Q = ChooseQ(layout, t, r, index, abs);
                Array.Clear(frame.Rw);
                if (!postgap) FillRwFromSub(t, t.PregapStoredSectors + r, frame);
                Emit(output, main, frame, form, sub, subSize);
                if ((++written & 0x3FF) == 0) progress?.Report(written / (double)total);
            }
        }

        progress?.Report(1.0);
    }

    // ---- pieces ------------------------------------------------------------

    /// <summary>Program sector p → absolute MSF (p = 0 is MSF 00:00:00).</summary>
    private static Msf ProgramMsf(long p) => Msf.FromSectors(p);

    /// <summary>
    /// The repeating lead-in TOC cycle: A0, A1, A2, then each track — every
    /// entry three sectors in a row, as players expect. Entries are functions
    /// of the running lead-in time, which differs per sector.
    /// </summary>
    private static List<Func<Msf, byte[]>> BuildTocCycle(
        DiscLayout layout, Msf leadOutStart, int[] index1Start)
    {
        var first = layout.Tracks[0];
        var last = layout.Tracks[^1];
        var entries = new List<Func<Msf, byte[]>>
        {
            run => SubQ.LeadInA0(first.Control, run, first.Number, layout.DiscType),
            // A1: PMIN carries the last track number.
            run => SubQ.LeadInToc(last.Control, 0xA1, run, new Msf(last.Number, 0, 0)),
            run => SubQ.LeadInToc(last.Control, 0xA2, run, leadOutStart),
        };
        for (int i = 0; i < layout.Tracks.Count; i++)
        {
            var t = layout.Tracks[i];
            var start = Msf.FromSectors(index1Start[i]);
            entries.Add(run => SubQ.LeadInToc(t.Control, Bcd.From(t.Number), run, start));
        }

        // Repeat each entry ×3 into the actual cycle.
        var cycle = new List<Func<Msf, byte[]>>(entries.Count * 3);
        foreach (var e in entries) { cycle.Add(e); cycle.Add(e); cycle.Add(e); }
        return cycle;
    }

    /// <summary>Program-area Q: position frames, with MCN and ISRC frames
    /// woven in on a fixed cadence (well inside the ≥1-per-100-sectors rule,
    /// and never in a track's first two sectors).</summary>
    private static byte[] ChooseQ(DiscLayout layout, RawTrack t, int rel, int index, Msf abs)
    {
        if (rel >= 2)
        {
            if (layout.Mcn is not null && abs.ToSectors() % 100 == 48)
                return SubQ.Mcn(t.Control, layout.Mcn, abs);
            if (t.Isrc is not null && abs.ToSectors() % 100 == 98)
                return SubQ.Isrc(t.Control, t.Isrc, abs);
        }
        return SubQ.Position(t.Control, t.Number, index, Msf.FromSectors(rel), abs);
    }

    /// <summary>Program-area R–W passthrough (CD+G): read the track's 96-byte
    /// sub frame and keep only the six R–W bit-planes — P and Q stay ours.</summary>
    private static void FillRwFromSub(RawTrack t, long storedIndex, SubcodeFrame frame)
    {
        if (t.SubSource is null) return;
        Span<byte> raw = stackalloc byte[96];
        t.SubSource.Seek(t.SubByteOffset + storedIndex * 96, SeekOrigin.Begin);
        t.SubSource.ReadExactly(raw);
        for (int i = 0; i < 96; i++) frame.Rw[i] = (byte)(raw[i] & 0x3F);
    }

    /// <summary>
    /// Emit a sector whose subcode is the source's VERBATIM 96-byte frame —
    /// the whole thing, P/Q/R–W, exactly as captured. This is what preserves
    /// LibCrypt-style protection: the deliberately-wrong Q goes to disc
    /// untouched. The captured form is always raw interleaved 96; we convert
    /// to the target layout (PQ-16 can't carry it faithfully and is refused
    /// upstream by negotiation).
    /// </summary>
    private static void EmitVerbatim(Stream output, byte[] main, RawTrack t, long storedIndex,
                                     RawSubcodeForm form, byte[] sub, int subSize)
    {
        Span<byte> raw = stackalloc byte[96];
        t.SubSource!.Seek(t.SubByteOffset + storedIndex * 96, SeekOrigin.Begin);
        t.SubSource.ReadExactly(raw);

        output.Write(main, 0, 2352);
        switch (form)
        {
            case RawSubcodeForm.Interleaved96:
                raw.CopyTo(sub);
                break;
            case RawSubcodeForm.Packed96:
                // De-interleave: channel c, byte b gets bit (7-k) from raw
                // frame bit c of symbol 8b+k. Preserves every bit, just
                // regrouped into the packed layout.
                Array.Clear(sub, 0, 96);
                for (int i = 0; i < 96; i++)
                    for (int c = 0; c < 8; c++)
                        if ((raw[i] & (0x80 >> c)) != 0)
                            sub[12 * c + (i >> 3)] |= (byte)(0x80 >> (i & 7));
                break;
            case RawSubcodeForm.Pq16:
                // PQ-16 can't carry R–W; verbatim mode should never reach here
                // (negotiation forces a 96-byte type). Emit Q + P as a
                // best-effort so the call is total.
                Span<byte> q = stackalloc byte[12];
                RawSubchannel.ExtractQ(raw, q);
                q.CopyTo(sub.AsSpan(0, 12));
                sub[12] = sub[13] = sub[14] = 0;
                sub[15] = (byte)((raw[0] & 0x80) != 0 ? 0x80 : 0x00);
                break;
        }
        output.Write(sub, 0, subSize);
    }

    /// <summary>A generated (silence / empty-data) sector for gaps.</summary>
    private static void FillGenerated(RawTrack t, byte[] main, Msf abs)
    {
        Array.Clear(main);
        switch (t.Mode)
        {
            case RawTrackMode.Audio:
                break;                                          // digital silence
            case RawTrackMode.Mode1:
                Span<byte> zero = stackalloc byte[2048];
                zero.Clear();
                RawSectorBuilder.BuildMode1(zero, abs, main);
                CdScrambler.ScrambleInPlace(main);
                break;
            case RawTrackMode.Mode2:
                RawSectorBuilder.WriteSync(main);
                RawSectorBuilder.WriteHeader(main, abs, mode: 2);
                CdScrambler.ScrambleInPlace(main);
                break;
        }
    }

    /// <summary>Read stored sector <paramref name="storedIndex"/> of a track
    /// and produce the raw (scrambled where applicable) 2352 in
    /// <paramref name="main"/>.</summary>
    private static void ReadStored(RawTrack t, long storedIndex, byte[] stored, byte[] main, Msf abs)
    {
        int size = t.StoredSectorSize;
        long pos = t.SourceByteOffset + storedIndex * size;
        t.Source.Seek(pos, SeekOrigin.Begin);
        t.Source.ReadExactly(stored, 0, size);

        switch (t.Mode)
        {
            case RawTrackMode.Audio:
                if (size != 2352)
                    throw new InvalidDataException(
                        $"Track {t.Number}: audio must be stored at 2352 bytes/sector.");
                Array.Copy(stored, main, 2352);
                return;

            case RawTrackMode.Mode1:
                if (size == 2048)
                {
                    RawSectorBuilder.BuildMode1(stored.AsSpan(0, 2048), abs, main);
                }
                else if (size == 2352)
                {
                    Array.Copy(stored, main, 2352);
                    // If this sector lands at a different absolute address than
                    // the image recorded (a topped-up pregap shifts everything),
                    // the header must be rewritten — and header bytes are under
                    // EDC/ECC, so both are recomputed. The normal case (offsets
                    // match) is a straight pass-through.
                    if (Bcd.To(main[12]) != abs.Minutes || Bcd.To(main[13]) != abs.Seconds ||
                        Bcd.To(main[14]) != abs.Frames)
                    {
                        RawSectorBuilder.WriteHeader(main, abs, main[15]);
                        EdcEcc.FillMode1(main);
                    }
                }
                else throw new InvalidDataException(
                    $"Track {t.Number}: Mode 1 stored at {size} bytes/sector is not supported.");
                CdScrambler.ScrambleInPlace(main);
                return;

            case RawTrackMode.Mode2:
                if (size == 2336)
                    RawSectorBuilder.BuildMode2(stored.AsSpan(0, 2336), abs, main);
                else if (size == 2352)
                {
                    Array.Copy(stored, main, 2352);
                    // Mode 2 headers are outside the XA error protection, so a
                    // relocation is a plain header rewrite.
                    RawSectorBuilder.WriteHeader(main, abs, main[15]);
                }
                else throw new InvalidDataException(
                    $"Track {t.Number}: Mode 2 stored at {size} bytes/sector is not supported.");
                CdScrambler.ScrambleInPlace(main);
                return;
        }
    }

    private static void Emit(Stream output, byte[] main, SubcodeFrame frame,
                             RawSubcodeForm form, byte[] sub, int subSize)
    {
        output.Write(main, 0, 2352);
        switch (form)
        {
            case RawSubcodeForm.Pq16: frame.EmitPq16(sub.AsSpan(0, 16)); break;
            case RawSubcodeForm.Packed96: frame.EmitPacked96(sub); break;
            case RawSubcodeForm.Interleaved96: frame.EmitInterleaved96(sub); break;
        }
        output.Write(sub, 0, subSize);
    }
}
