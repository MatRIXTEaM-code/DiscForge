// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Mmc;

/// <summary>One 8-byte MMC cue-sheet entry (as consumed by SEND CUE SHEET for a DAO write).</summary>
public sealed record DaoCueEntry(
    byte CtlAdr, byte TrackNumber, byte IndexOrPoint, byte DataForm,
    byte Scms, byte Min, byte Sec, byte Frame)
{
    public byte[] ToBytes() => new[] { CtlAdr, TrackNumber, IndexOrPoint, DataForm, Scms, Min, Sec, Frame };
}

/// <summary>
/// Builds the binary MMC cue sheet that a disc-at-once write feeds to the drive via
/// SEND CUE SHEET — the piece that makes byte-faithful RAW DAO possible over direct SPTI
/// (the path that bypasses IMAPI2's raw-CD writer, which rejects hand-built images). The cue
/// sheet is a list of 8-byte entries: one generic lead-in entry, every track's pregap/index,
/// and one lead-out entry; the drive uses it to lay down the TOC while we stream the raw
/// sectors.
///
/// SCOPE / HONESTY: this structure and the Data Form byte values were both corrected against
/// cdrdao's GenericMMC::createCueSheet (dao/GenericMMC.cc) — a SEND-CUE-SHEET implementation
/// proven on real drives for two decades — after a first attempt (Red-Book-style POINT A0/A1/A2
/// lead-in entries, and a guessed Data Form byte) was rejected outright by a real drive with
/// ASC 0x26/0x00 ("invalid field in parameter list") via `burn-raw --engine spti --test-cue`.
/// That validation is cheap and non-destructive: SEND CUE SHEET is issued on its own and the
/// drive returns a CHECK CONDITION with sense data if the cue sheet is malformed — no disc is
/// written. Structure (entry count, ordering, MSF math, control bits) is unit-tested here;
/// exact byte acceptance is still the drive's to confirm — this is the second attempt, not a
/// guaranteed-correct one.
///
/// Entry (8 bytes): CTL/ADR · Track · Index-or-POINT · Data Form · SCMS · M · S · F (M/S/F BCD).
/// </summary>
public static class DaoCueSheet
{
    // Data Form (main-channel kind for the section). TWO wrong values were tried here before
    // this one, both worth recording so a future session doesn't repeat them:
    //   - 0x10 for every non-audio track (the original guess) — a real drive rejected it with
    //     ASC 0x26/0x00 ("invalid field in parameter list") via --test-cue.
    //   - 0x08 (from an MMC-spec PDF text extraction that turned out unreliable — this session
    //     independently caught the same summarizer inventing a redump.org hash match earlier)
    //     — STILL rejected by the drive with the identical sense code.
    // The values actually used here come from cdrdao's GenericMMC::createCueSheet
    // (dao/GenericMMC.cc), a cue-sheet-via-SEND-CUE-SHEET implementation that has burned real
    // discs on real drives for over two decades — audio = 0x00, Mode 1 = 0x10, Mode 2 (raw,
    // includes CD-XA) = 0x20. DiscForge's RawTrackMode only distinguishes Audio/Mode1/Mode2 (no
    // separate XA-form split), which maps directly onto cdrdao's own MODE2_RAW bucket ("assume
    // it contains XA sectors") for Mode2.
    private const byte DataFormAudio = 0x00;
    private const byte DataFormMode1 = 0x10;
    private const byte DataFormMode2 = 0x20;

    public static IReadOnlyList<DaoCueEntry> BuildEntries(DiscLayout layout)
    {
        var tracks = layout.Tracks;
        if (tracks.Count == 0) throw new InvalidDataException("Layout has no tracks.");

        // Index-1 (track start) positions in absolute sectors, and the lead-out position.
        var index1 = new int[tracks.Count];
        int p = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            p += tracks[i].PregapTotalSectors;
            index1[i] = p;
            p += tracks[i].LengthSectors + tracks[i].PostgapSectors;
        }
        int program = p;
        var leadOut = Msf.FromSectors(program);

        var entries = new List<DaoCueEntry>();

        byte CtlAdr(RawTrack t) => (byte)(((byte)t.Control << 4) | 0x01);   // control nibble + ADR=1
        byte Form(RawTrack t) => t.Mode switch
        {
            RawTrackMode.Audio => DataFormAudio,
            RawTrackMode.Mode1 => DataFormMode1,
            _ => DataFormMode2,   // RawTrackMode.Mode2 — raw 2352-byte sectors, includes CD-XA
        };
        // The lead-in/lead-out entries use a DIFFERENT Data Form mapping than program-area
        // tracks in cdrdao (audio=0x01, not 0x00; Mode1/Mode2 offset by 0x04) — kept distinct
        // rather than reusing Form() since that's what the reference implementation does.
        byte LeadInOutForm(RawTrackMode m) => m switch
        {
            RawTrackMode.Audio => 0x01,
            RawTrackMode.Mode1 => 0x14,
            _ => 0x24,   // Mode2 (raw, includes CD-XA)
        };

        // ── Lead-in: ONE generic entry — TNO=0, Index=0, MSF=00:00:00 ─────────────────────
        // The previous version of this file sent three Red-Book-style POINT entries here
        // (A0/A1/A2, listing first/last track and lead-out start) modeled on what a real
        // burned TOC's Q-channel carries. A real drive rejected the cue sheet outright with
        // ASC 0x26/0x00 ("invalid field in parameter list") even after the Data Form byte fix
        // below, which pointed at something more structural than a single wrong byte value.
        // cdrdao's GenericMMC::createCueSheet (dao/GenericMMC.cc) — a SEND-CUE-SHEET
        // implementation that has burned real discs on real drives for two decades — sends
        // exactly ONE lead-in entry, not three; the drive derives the rest (first/last track,
        // disc format) from the track list it's given, the way MODE SELECT's session format
        // and the program-area entries below already describe it. Matched here.
        entries.Add(new DaoCueEntry(CtlAdr(tracks[0]), 0x00, 0x00, LeadInOutForm(tracks[0].Mode), 0x00,
            0x00, 0x00, 0x00));

        // ── Program: each track's pregap (INDEX 00) then its start (INDEX 01) ─────────────
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t.PregapTotalSectors > 0)
            {
                var pg = Msf.FromSectors(index1[i] - t.PregapTotalSectors);
                entries.Add(new DaoCueEntry(CtlAdr(t), (byte)t.Number, 0x00, Form(t), 0x00,
                    Bcd.From(pg.Minutes), Bcd.From(pg.Seconds), Bcd.From(pg.Frames)));
            }
            var s = Msf.FromSectors(index1[i]);
            entries.Add(new DaoCueEntry(CtlAdr(t), (byte)t.Number, 0x01, Form(t), 0x00,
                Bcd.From(s.Minutes), Bcd.From(s.Seconds), Bcd.From(s.Frames)));
        }

        // ── Lead-out (track 0xAA) ─────────────────────────────────────────────────────────
        entries.Add(new DaoCueEntry(CtlAdr(tracks[^1]), 0xAA, 0x01, LeadInOutForm(tracks[^1].Mode), 0x00,
            Bcd.From(leadOut.Minutes), Bcd.From(leadOut.Seconds), Bcd.From(leadOut.Frames)));

        return entries;
    }

    /// <summary>The cue sheet as the raw byte payload SEND CUE SHEET carries.</summary>
    public static byte[] Build(DiscLayout layout)
    {
        var entries = BuildEntries(layout);
        var bytes = new byte[entries.Count * 8];
        for (int i = 0; i < entries.Count; i++) entries[i].ToBytes().CopyTo(bytes, i * 8);
        return bytes;
    }
}
