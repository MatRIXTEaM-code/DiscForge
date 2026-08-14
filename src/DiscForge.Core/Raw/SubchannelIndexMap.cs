// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Util;

namespace DiscForge.Core.Raw;

/// <summary>The index layout of one track recovered from the subchannel Q: where its pregap and body begin, and the resulting pregap length.</summary>
public sealed record SubTrackIndexes
{
    public required int Track { get; init; }
    /// <summary>Absolute LBA where INDEX 00 (the pregap) begins, or null when the track has no pregap.</summary>
    public required long? Index00Lba { get; init; }
    /// <summary>Absolute LBA where INDEX 01 (the track body) begins.</summary>
    public required long Index01Lba { get; init; }
    /// <summary>Pregap length in sectors: INDEX 01 − INDEX 00 (0 when there is no pregap).</summary>
    public required int PregapSectors { get; init; }
    public required bool IsData { get; init; }
}

/// <summary>The per-track index map recovered from a subchannel sidecar.</summary>
public sealed record SubchannelIndexMap
{
    public required RawSubcodeForm Form { get; init; }
    public required int SectorsScanned { get; init; }
    public required int ValidQFrames { get; init; }
    public required IReadOnlyList<SubTrackIndexes> Tracks { get; init; }

    public string Summary()
    {
        int withPregap = Tracks.Count(t => t.PregapSectors > 0);
        return $"{Tracks.Count} track(s) from {ValidQFrames:N0} valid Q frames ({Form}); " +
               $"{withPregap} carry a pregap.";
    }
}

/// <summary>
/// subq-map — recover the true per-track index layout from a captured subchannel (.sub), the way Redump
/// determines a disc's pregaps. The subchannel Q carries a position frame for every sector: its track, its
/// index (00 = pregap, 01 = body) and its absolute address. Walking the CRC-valid frames pins exactly where
/// each track's pregap and body begin — so a mixed-mode disc's real pregaps (which vary: 2s, 3s, or none)
/// come from the disc itself rather than a guessed convention. This is the authoritative source for building a
/// Redump-accurate cue. Read-only analysis of an already-captured sidecar; it defeats no protection.
/// </summary>
public static class SubchannelIndexMapper
{
    private const int LeadInOffset = 150;

    /// <summary>Parse a subchannel sidecar into a per-track index map. When <paramref name="form"/> is null the 96-byte form is auto-detected by CRC validity.</summary>
    public static SubchannelIndexMap Parse(ReadOnlySpan<byte> sub, RawSubcodeForm? form = null)
    {
        RawSubcodeForm chosen = form ?? Detect(sub);
        int stride = chosen == RawSubcodeForm.Pq16 ? 16 : 96;
        int sectors = sub.Length / stride;

        // Per track: the smallest absolute LBA seen for index 0 and index 1, and the control bits.
        var idx0 = new Dictionary<int, long>();
        var idx1 = new Dictionary<int, long>();
        var control = new Dictionary<int, int>();
        int valid = 0;

        Span<byte> q = stackalloc byte[12];
        for (int s = 0; s < sectors; s++)
        {
            SubcodeFrame.ExtractQ(sub.Slice(s * stride, stride), chosen, q);
            if (!CrcOk(q)) continue;
            if ((q[0] & 0x0F) != 1) continue;            // ADR 1 = position frame

            int track = Bcd(q[1]);
            if (track is <= 0 or >= 0xAA) continue;       // skip lead-in (0) and lead-out (0xAA)
            int index = Bcd(q[2]);
            long abs = (Bcd(q[7]) * 60L + Bcd(q[8])) * 75 + Bcd(q[9]) - LeadInOffset;
            valid++;
            control[track] = q[0] >> 4;

            if (index == 0) { if (!idx0.TryGetValue(track, out var v) || abs < v) idx0[track] = abs; }
            else if (index == 1) { if (!idx1.TryGetValue(track, out var v) || abs < v) idx1[track] = abs; }
        }

        var tracks = new List<SubTrackIndexes>();
        foreach (var t in idx1.Keys.OrderBy(x => x))
        {
            long body = idx1[t];
            long? pre = idx0.TryGetValue(t, out var p) ? p : null;
            int pregap = pre is { } pv && pv <= body ? (int)(body - pv) : 0;
            tracks.Add(new SubTrackIndexes
            {
                Track = t,
                Index00Lba = pre,
                Index01Lba = body,
                PregapSectors = pregap,
                IsData = control.TryGetValue(t, out var c) && (c & 0x04) != 0,
            });
        }

        return new SubchannelIndexMap
        {
            Form = chosen,
            SectorsScanned = sectors,
            ValidQFrames = valid,
            Tracks = tracks,
        };
    }

    /// <summary>Pick the 96-byte form (packed vs interleaved) that yields the most CRC-valid Q frames over a sample.</summary>
    private static RawSubcodeForm Detect(ReadOnlySpan<byte> sub)
    {
        int packed = CountValid(sub, RawSubcodeForm.Packed96);
        int inter = CountValid(sub, RawSubcodeForm.Interleaved96);
        return inter > packed ? RawSubcodeForm.Interleaved96 : RawSubcodeForm.Packed96;
    }

    private static int CountValid(ReadOnlySpan<byte> sub, RawSubcodeForm form)
    {
        int sectors = Math.Min(sub.Length / 96, 8192);
        int ok = 0;
        Span<byte> q = stackalloc byte[12];
        for (int s = 0; s < sectors; s++)
        {
            SubcodeFrame.ExtractQ(sub.Slice(s * 96, 96), form, q);
            if (CrcOk(q)) ok++;
        }
        return ok;
    }

    private static bool CrcOk(ReadOnlySpan<byte> q)
    {
        ushort want = (ushort)((q[10] << 8) | q[11]);
        return Crc16.ComputeInverted(q[..10]) == want;
    }

    private static int Bcd(byte b) => ((b >> 4) & 0x0F) * 10 + (b & 0x0F);
}
