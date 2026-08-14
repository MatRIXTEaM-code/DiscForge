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
/// sheet is a list of 8-byte entries describing the lead-in (POINT A0/A1/A2), every track and
/// index, and the lead-out; the drive uses it to lay down the TOC while we stream the raw
/// sectors.
///
/// SCOPE / HONESTY: the 8-byte entry layout is built from the public MMC cue-sheet definition,
/// but a few field encodings (the Data Form value for data tracks; whether the drive wants a
/// per-track INDEX 00 entry) vary between drives and are <b>pending validation against real
/// hardware</b>. That validation is cheap and non-destructive: SEND CUE SHEET is issued on its
/// own and the drive returns a CHECK CONDITION with sense data if the cue sheet is malformed —
/// no disc is written. `burn-raw --engine spti --test-cue` does exactly that. Structure (entry
/// count, ordering, MSF math, control bits) is unit-tested here; exact byte acceptance is the
/// drive's to confirm.
///
/// Entry (8 bytes): CTL/ADR · Track · Index-or-POINT · Data Form · SCMS · M · S · F (M/S/F BCD).
/// </summary>
public static class DaoCueSheet
{
    // Data Form (main-channel kind for the section). Audio is well established; the data value
    // is the tentative one that --test-cue confirms.
    private const byte DataFormAudio = 0x00;
    private const byte DataFormData = 0x10;

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

        int first = tracks[0].Number, last = tracks[^1].Number;
        var entries = new List<DaoCueEntry>();

        byte CtlAdr(RawTrack t) => (byte)(((byte)t.Control << 4) | 0x01);   // control nibble + ADR=1
        byte Form(RawTrack t) => t.Mode == RawTrackMode.Audio ? DataFormAudio : DataFormData;
        byte firstCtl = CtlAdr(tracks[0]);

        // ── Lead-in: the three mandatory POINT entries ────────────────────────────────────
        // A0: first track number (in M), disc application format (in S), 0 (F).
        entries.Add(new DaoCueEntry(firstCtl, 0x00, 0xA0, DataFormAudio, 0x00,
            Bcd.From(first), layout.DiscType, 0x00));
        // A1: last track number (in M).
        entries.Add(new DaoCueEntry(CtlAdr(tracks[^1]), 0x00, 0xA1, DataFormAudio, 0x00,
            Bcd.From(last), 0x00, 0x00));
        // A2: lead-out start (MSF).
        entries.Add(new DaoCueEntry(firstCtl, 0x00, 0xA2, DataFormAudio, 0x00,
            Bcd.From(leadOut.Minutes), Bcd.From(leadOut.Seconds), Bcd.From(leadOut.Frames)));

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
        entries.Add(new DaoCueEntry(firstCtl, 0xAA, 0x01, Form(tracks[^1]), 0x00,
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
