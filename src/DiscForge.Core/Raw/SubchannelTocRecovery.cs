// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Raw;

/// <summary>One track recovered from the program-area sub-channel.</summary>
public sealed record RecoveredTrack(int Number, long StartLba, bool IsData, int Control)
{
    public override string ToString()
        => $"Track {Number}: starts LBA {StartLba} ({(IsData ? "data" : "audio")})";
}

/// <summary>A table of contents rebuilt from the Q sub-channel alone.</summary>
public sealed record RecoveredToc
{
    public required IReadOnlyList<RecoveredTrack> Tracks { get; init; }
    public required long LeadOutLba { get; init; }
    public required int FramesUsed { get; init; }
    public required int FramesRejected { get; init; }

    public int FirstTrack => Tracks.Count == 0 ? 0 : Tracks.Min(t => t.Number);
    public int LastTrack => Tracks.Count == 0 ? 0 : Tracks.Max(t => t.Number);
    public bool Recovered => Tracks.Count > 0;

    public string Summary() => Recovered
        ? $"Recovered a {Tracks.Count}-track TOC from the sub-channel " +
          $"(tracks {FirstTrack}–{LastTrack}, lead-out LBA {LeadOutLba:N0}; " +
          $"{FramesUsed:N0} Q frames used, {FramesRejected:N0} rejected)."
        : "No usable Q position frames — could not rebuild the TOC.";
}

/// <summary>
/// Rebuild a disc's table of contents from the program-area Q sub-channel when the lead-in is
/// unreadable. The lead-in holds the TOC, but the very same addressing — track number, index, and
/// absolute time — is repeated in the Q sub-channel of every single sector across the whole disc. So a
/// disc whose lead-in is scratched to death, which a drive refuses to give a TOC for, can still be laid
/// out: walk the body's Q frames, keep the ones whose CRC checks out, and read off where each track's
/// index 1 begins and whether it is audio or data. A "won't-mount" disc becomes preservable. It reads
/// the sub-channel and reconstructs; it changes nothing.
/// </summary>
public static class SubchannelTocRecovery
{
    private const byte LeadOutTno = 0xAA;

    /// <summary>Rebuild the TOC from Q frames given in sector order across the program area (and, if
    /// present, the lead-out). Each entry is a 12-byte formatted Q frame.</summary>
    public static RecoveredToc Recover(IReadOnlyList<byte[]> qFrames)
    {
        ArgumentNullException.ThrowIfNull(qFrames);

        // Per track: votes for the computed start LBA (absolute − relative), and the control field.
        // The relative MSF counts up from zero at index 1, so absLba − relFrames pins the exact start
        // from ANY surviving index-1 frame; the modal vote shrugs off the odd bad frame.
        var startVotes = new Dictionary<int, Dictionary<long, int>>();
        var trackControl = new Dictionary<int, int>();
        long highestBodyLba = -1;
        long leadOut = -1;
        int used = 0, rejected = 0;

        foreach (var q in qFrames)
        {
            if (q is null || q.Length < 12 || !RawSubchannel.QCrcValid(q)) { rejected++; continue; }
            int adr = q[0] & 0x0F;
            if (adr != 1) { rejected++; continue; }        // only ADR-1 position frames carry the TOC data
            used++;

            int control = (q[0] >> 4) & 0x0F;
            long absLba = MsfToLba(Bcd.To(q[7]), Bcd.To(q[8]), Bcd.To(q[9]));

            if (q[1] == LeadOutTno)                         // lead-out (TNO 0xAA)
            {
                if (leadOut < 0 || absLba < leadOut) leadOut = absLba;
                continue;
            }

            int track = Bcd.To(q[1]);
            int index = Bcd.To(q[2]);
            if (track < 1 || track > 99) continue;
            if (absLba > highestBodyLba) highestBodyLba = absLba;

            if (index == 1)                                // index 1 marks the track body; its relative time is 0 there
            {
                long relFrames = (Bcd.To(q[3]) * 60L + Bcd.To(q[4])) * 75 + Bcd.To(q[5]);
                long start = absLba - relFrames;
                var votes = startVotes.TryGetValue(track, out var v) ? v : startVotes[track] = new();
                votes[start] = votes.GetValueOrDefault(start) + 1;
                trackControl[track] = control;             // control is stable within a track
            }
        }

        var tracks = startVotes.Keys.OrderBy(t => t)
            .Select(t => new RecoveredTrack(
                t, startVotes[t].OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key,
                IsData: (trackControl.GetValueOrDefault(t) & (int)QControl.Data) != 0,
                Control: trackControl.GetValueOrDefault(t)))
            .ToList();

        if (leadOut < 0) leadOut = highestBodyLba >= 0 ? highestBodyLba + 1 : 0;

        return new RecoveredToc
        {
            Tracks = tracks,
            LeadOutLba = leadOut,
            FramesUsed = used,
            FramesRejected = rejected,
        };
    }

    public static string Render(RecoveredToc toc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(toc.Summary());
        foreach (var t in toc.Tracks) sb.AppendLine($"  {t}");
        if (toc.Recovered) sb.AppendLine($"  lead-out: LBA {toc.LeadOutLba:N0}");
        return sb.ToString().TrimEnd();
    }

    private static long MsfToLba(int m, int s, int f) => (m * 60L + s) * 75 + f - 150;
}
