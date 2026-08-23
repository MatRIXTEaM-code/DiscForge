// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>One measured trait of a pressing, human-readable and hashable.</summary>
public sealed record PressingTrait(string Name, string Value);

/// <summary>
/// A disc's Pressing DNA. <see cref="DiscGenome"/> deliberately ignores
/// everything that varies BETWEEN pressings of one title (offsets, geometry
/// nudges, plant metadata) to answer "same disc content?". This fingerprint
/// keeps exactly those discarded details, because they are how two pressings
/// of the same title are told apart: exact track geometry and pregap lengths,
/// where the audio actually starts and ends inside its tracks (write-offset
/// artifacts land here), and the subcode identity the plant stamped (MCN,
/// ISRCs). The logical cousin of Redump's physical ring codes — answerable
/// offline, from the rip alone.
/// </summary>
public sealed record PressingFingerprint
{
    /// <summary>The offset-invariant content identity (which TITLE).</summary>
    public required GenomeFingerprint Genome { get; init; }
    /// <summary>The offset-sensitive traits (which PRESSING), in canonical order.</summary>
    public required IReadOnlyList<PressingTrait> Traits { get; init; }

    /// <summary>Hash over the canonical trait list — equal iff every measured
    /// pressing trait is equal.</summary>
    public string PressingId
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var t in Traits) sb.Append(t.Name).Append('=').Append(t.Value).Append('\n');
            return System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16]
                .ToLowerInvariant();
        }
    }

    public string ContentId => Genome.ShortId;
}

/// <summary>How two pressing fingerprints relate.</summary>
public sealed record PressingMatch
{
    public required bool SameContent { get; init; }
    public required bool SamePressing { get; init; }
    /// <summary>Trait-by-trait differences ("name: a-value ≠ b-value"), plus a
    /// synthesized line when audio edges disagree by one constant shift — the
    /// classic mastering write-offset signature.</summary>
    public required IReadOnlyList<string> Differences { get; init; }
    public GenomeMatch? GenomeMatch { get; init; }

    public string Verdict =>
        SamePressing ? "SAME PRESSING — every measured trait agrees"
        : SameContent ? "same title, DIFFERENT PRESSING — see trait differences"
        : "different discs";
}

public static class PressingDna
{
    /// <summary>
    /// Compute a fingerprint from split tracks. <paramref name="pregapSectors"/>
    /// maps track number → pregap length in sectors when a cue supplies it;
    /// <paramref name="mcn"/>/<paramref name="isrcs"/> carry subcode identity
    /// when captured. Audio content must be raw 2352-byte sectors.
    /// </summary>
    public static PressingFingerprint Compute(IReadOnlyList<GenomeTrack> tracks,
        IReadOnlyDictionary<int, int>? pregapSectors = null,
        string? mcn = null,
        IReadOnlyDictionary<int, string>? isrcs = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var traits = new List<PressingTrait>();
        var ordered = tracks.OrderBy(t => t.Number).ToList();

        int dataCount = ordered.Count(t => t.IsData);
        traits.Add(new("tracks", $"{ordered.Count} ({dataCount} data, {ordered.Count - dataCount} audio)"));

        foreach (var t in ordered)
        {
            long sectors = t.Content.Length / 2352;
            string pregap = pregapSectors is not null && pregapSectors.TryGetValue(t.Number, out int pg)
                ? pg.ToString() : "-";
            traits.Add(new($"track {t.Number:00}",
                $"{(t.IsData ? "data" : "audio")} len={sectors} pregap={pregap}"));

            if (t.IsData)
            {
                traits.Add(new($"track {t.Number:00} sha256",
                    System.Convert.ToHexString(SHA256.HashData(t.Content)).ToLowerInvariant()));
            }
            else
            {
                // Where the sound actually sits inside the track, in 16-bit
                // samples. Two pressings mastered with different write offsets
                // shift BOTH edges of EVERY audio track by the same constant —
                // the comparator recognizes that pattern by name.
                var (first, last) = AudioEdges(t.Content);
                traits.Add(new($"track {t.Number:00} audio-edges",
                    first < 0 ? "silent" : $"first={first} last={last}"));
            }
        }

        if (!string.IsNullOrEmpty(mcn)) traits.Add(new("mcn", mcn));
        if (isrcs is not null)
            foreach (var (trk, isrc) in isrcs.OrderBy(kv => kv.Key))
                traits.Add(new($"isrc {trk:00}", isrc));

        return new PressingFingerprint { Genome = DiscGenome.Compute(tracks), Traits = traits };
    }

    /// <summary>Compare two fingerprints: content identity by genome match
    /// (offset-tolerant), pressing identity by exact traits.</summary>
    public static PressingMatch Compare(PressingFingerprint a, PressingFingerprint b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var genome = DiscGenome.Compare(a.Genome, b.Genome);

        var diffs = new List<string>();
        var bByName = b.Traits.ToDictionary(t => t.Name, t => t.Value);
        var aByName = a.Traits.ToDictionary(t => t.Name, t => t.Value);
        foreach (var t in a.Traits)
        {
            if (!bByName.TryGetValue(t.Name, out var bv)) diffs.Add($"{t.Name}: only in first ({t.Value})");
            else if (bv != t.Value) diffs.Add($"{t.Name}: {t.Value} ≠ {bv}");
        }
        foreach (var t in b.Traits)
            if (!aByName.ContainsKey(t.Name)) diffs.Add($"{t.Name}: only in second ({t.Value})");

        // The write-offset signature: every audio track's edges moved by one
        // constant sample count.
        var shifts = new List<long>();
        bool allEdgePairsShift = true;
        foreach (var t in a.Traits.Where(t => t.Name.EndsWith("audio-edges", StringComparison.Ordinal)))
        {
            if (!bByName.TryGetValue(t.Name, out var bv)) { allEdgePairsShift = false; break; }
            var pa = ParseEdges(t.Value);
            var pb = ParseEdges(bv);
            if (pa is null || pb is null) { allEdgePairsShift = false; break; }
            long dFirst = pb.Value.First - pa.Value.First;
            long dLast = pb.Value.Last - pa.Value.Last;
            if (dFirst != dLast) { allEdgePairsShift = false; break; }
            shifts.Add(dFirst);
        }
        if (allEdgePairsShift && shifts.Count > 0 && shifts.Distinct().Count() == 1 && shifts[0] != 0)
            diffs.Add($"write-offset signature: all audio shifted by {shifts[0]:+#;-#} sample(s) — " +
                      "different mastering offset, classic pressing tell");

        return new PressingMatch
        {
            SameContent = genome.SameDisc,
            SamePressing = a.PressingId == b.PressingId,
            Differences = diffs,
            GenomeMatch = genome,
        };
    }

    /// <summary>First and last non-zero 16-bit sample positions in an audio
    /// track's raw bytes; (-1, -1) for digital silence throughout.</summary>
    internal static (long First, long Last) AudioEdges(ReadOnlySpan<byte> content)
    {
        long samples = content.Length / 2;
        long first = -1, last = -1;
        for (long i = 0; i < samples; i++)
            if (content[(int)(i * 2)] != 0 || content[(int)(i * 2 + 1)] != 0) { first = i; break; }
        if (first < 0) return (-1, -1);
        for (long i = samples - 1; i >= first; i--)
            if (content[(int)(i * 2)] != 0 || content[(int)(i * 2 + 1)] != 0) { last = i; break; }
        return (first, last);
    }

    private static (long First, long Last)? ParseEdges(string v)
    {
        // "first=NNN last=NNN" | "silent"
        if (v == "silent") return null;
        var parts = v.Split(' ');
        if (parts.Length != 2) return null;
        if (!long.TryParse(parts[0].Split('=').ElementAtOrDefault(1), out long f)) return null;
        if (!long.TryParse(parts[1].Split('=').ElementAtOrDefault(1), out long l)) return null;
        return (f, l);
    }
}
