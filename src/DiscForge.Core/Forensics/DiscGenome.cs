// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Security.Cryptography;
using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>One track's material for genome computation: cooked user data for a
/// data track, or 16-bit stereo LE PCM (2352 bytes/sector) for an audio track.</summary>
public sealed record GenomeTrack(int Number, bool IsData, byte[] Content);

/// <summary>
/// A disc's "genome" — a fingerprint built to be invariant to the things that do
/// not change a pressing's identity, above all the CD-DA <b>read offset</b>.
///
/// Three parts, each honest about what it guarantees:
///  • <see cref="LayoutHash"/> — a hash of the disc geometry (track count, and each
///    track's kind and length). Exact and fully offset-invariant: two correct rips
///    of the same pressing always share it, and a different pressing/region does not.
///  • <see cref="DataHash"/> — a hash of the addressed data-track content. Data is
///    read sector-aligned by the drive, so it is offset-invariant and exact.
///  • <see cref="AudioEnvelope"/> — a compact per-sector loudness envelope of the
///    audio, guard-trimmed at track edges. A read offset shifts *which* samples land
///    in each sector by a few dozen, which barely moves a 588-sample peak, so the
///    envelope is stable across offsets — but it is compared by similarity with a
///    small shift search, not by hash equality, because it is robust, not bit-exact.
/// </summary>
public sealed record GenomeFingerprint
{
    public required string LayoutHash { get; init; }
    public required string DataHash { get; init; }
    /// <summary>Quantised per-sector peak-loudness bytes across all audio tracks,
    /// with a guard band trimmed from each track's ends.</summary>
    public required byte[] AudioEnvelope { get; init; }
    public required int AudioTrackCount { get; init; }

    /// <summary>A short human-facing id: first 16 hex of a hash over the three parts.</summary>
    public string ShortId
    {
        get
        {
            var h = SHA256.HashData(Encoding.ASCII.GetBytes(LayoutHash + ":" + DataHash + ":" +
                System.Convert.ToHexString(SHA256.HashData(AudioEnvelope))));
            return System.Convert.ToHexString(h)[..16].ToLowerInvariant();
        }
    }
}

/// <summary>How two genomes relate.</summary>
public sealed record GenomeMatch
{
    public required bool LayoutMatch { get; init; }
    public required bool DataMatch { get; init; }
    /// <summary>Best envelope agreement over the shift search, 0–1.</summary>
    public required double AudioSimilarity { get; init; }
    /// <summary>The envelope shift (in sectors) that gave the best agreement.</summary>
    public required int BestShift { get; init; }
    /// <summary>Layout + data exact, audio at/above the similarity threshold.</summary>
    public required bool SameDisc { get; init; }

    public string Summary() => SameDisc
        ? $"Same disc (layout+data exact, audio {AudioSimilarity:P1} at shift {BestShift})."
        : LayoutMatch
            ? $"Same layout but content differs (data {(DataMatch ? "match" : "differ")}, " +
              $"audio {AudioSimilarity:P1})."
            : "Different disc (layout differs).";
}

public static class DiscGenome
{
    private const int SectorAudioBytes = 2352;   // one CD frame = 588 stereo samples
    private const int GuardSectors = 2;          // trim this many sectors per audio-track edge
    private const int DefaultShiftSearch = 4;    // ± envelope sectors to try when matching

    /// <summary>Compute a genome from a disc's tracks.</summary>
    public static GenomeFingerprint Compute(IReadOnlyList<GenomeTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        // Layout: track count, then each track's kind and length in its natural unit
        // (sectors for data, audio sectors for audio). Offset never changes these.
        var layout = new StringBuilder();
        layout.Append(tracks.Count).Append(';');
        foreach (var t in tracks.OrderBy(t => t.Number))
        {
            long unitLen = t.IsData ? (t.Content.Length + 2047) / 2048
                                    : (long)t.Content.Length / SectorAudioBytes;
            layout.Append(t.Number).Append(t.IsData ? 'D' : 'A').Append(unitLen).Append(';');
        }
        string layoutHash = Sha(Encoding.ASCII.GetBytes(layout.ToString()));

        // Data content: the ADDRESSED data-track bytes, hashed incrementally. A data
        // track that is followed by audio carries that audio track's pregap in its tail,
        // and the drive returns the pregap as CD-DA — which is read-offset sensitive. If
        // that went into the data hash, two faithful dumps from drives with different read
        // offsets would look like different discs. Real data sectors carry the CD sync
        // mark; the audio pregap does not, so we hash only up to the last sync-bearing
        // sector and drop the trailing pregap.
        using var dataSha = SHA256.Create();
        var ordered = tracks.OrderBy(t => t.Number).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            if (!t.IsData) continue;
            bool audioFollows = ordered.Skip(i + 1).Any(x => !x.IsData);
            int len = AddressedDataLength(t.Content, audioFollows);
            if (len > 0) dataSha.TransformBlock(t.Content, 0, len, null, 0);
        }
        dataSha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        string dataHash = System.Convert.ToHexString(dataSha.Hash!).ToLowerInvariant();

        // Audio envelope: per-sector quantised peak amplitude, guard-trimmed.
        var envelope = new List<byte>();
        int audioTracks = 0;
        foreach (var t in tracks.Where(t => !t.IsData).OrderBy(t => t.Number))
        {
            audioTracks++;
            int sectors = t.Content.Length / SectorAudioBytes;
            for (int s = GuardSectors; s < sectors - GuardSectors; s++)
                envelope.Add(SectorPeak(t.Content, s * SectorAudioBytes));
        }

        return new GenomeFingerprint
        {
            LayoutHash = layoutHash,
            DataHash = dataHash,
            AudioEnvelope = envelope.ToArray(),
            AudioTrackCount = audioTracks,
        };
    }

    /// <summary>Compare two genomes, tolerating a small audio shift.</summary>
    public static GenomeMatch Compare(GenomeFingerprint a, GenomeFingerprint b,
                                      double audioThreshold = 0.97, int shiftSearch = DefaultShiftSearch)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        bool layoutMatch = a.LayoutHash == b.LayoutHash;
        bool dataMatch = a.DataHash == b.DataHash;

        double best = 0; int bestShift = 0;
        if (a.AudioEnvelope.Length == 0 && b.AudioEnvelope.Length == 0)
        {
            best = 1.0;   // no audio on either — trivially in agreement
        }
        else
        {
            for (int d = -shiftSearch; d <= shiftSearch; d++)
            {
                double sim = EnvelopeSimilarity(a.AudioEnvelope, b.AudioEnvelope, d);
                if (sim > best) { best = sim; bestShift = d; }
            }
        }

        bool sameDisc = layoutMatch && dataMatch && best >= audioThreshold;
        return new GenomeMatch
        {
            LayoutMatch = layoutMatch,
            DataMatch = dataMatch,
            AudioSimilarity = best,
            BestShift = bestShift,
            SameDisc = sameDisc,
        };
    }

    // ---- internals ----------------------------------------------------------

    /// <summary>The Red Book pregap is 150 sectors; drives also disagree by a handful of
    /// sectors on exactly where they stop serving data at a data→audio boundary. This guard,
    /// measured from the track end, covers both so the drive-dependent transition zone never
    /// enters the data hash.</summary>
    private const int TransitionGuardSectors = 300;

    /// <summary>How many leading bytes of a data track count as addressed data. Starts from the
    /// leading contiguous run of sync-bearing sectors (stopping at the first sector without a
    /// sync mark — the start of any audio pregap). When the track is <paramref name="precedesAudio"/>,
    /// it additionally trims a fixed guard measured from the track END: the last sectors before
    /// audio are a drive-dependent transition zone (two drives disagree by a few sectors on where
    /// data stops), and because the track length is fixed by the TOC, trimming from the end gives
    /// every drive the identical boundary. Falls back to the whole buffer when the content isn't
    /// raw 2352-byte sectors, or its first sector has no sync (already cooked, or synthetic).</summary>
    private static int AddressedDataLength(byte[] content, bool precedesAudio)
    {
        if (content.Length == 0 || content.Length % SectorAudioBytes != 0) return content.Length;
        if (!HasSync(content, 0)) return content.Length;

        int sectors = content.Length / SectorAudioBytes;
        int run = 0;
        while (run < sectors && HasSync(content, run * SectorAudioBytes)) run++;

        if (precedesAudio)
            run = Math.Min(run, Math.Max(0, sectors - TransitionGuardSectors));

        return run * SectorAudioBytes;
    }

    /// <summary>The 12-byte CD sync mark that begins every data sector: 00 FF×10 00.</summary>
    private static bool HasSync(byte[] b, int off)
    {
        if (off + 12 > b.Length) return false;
        if (b[off] != 0x00 || b[off + 11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (b[off + i] != 0xFF) return false;
        return true;
    }

    /// <summary>Quantised peak amplitude over one audio sector — robust to the few
    /// samples a read offset shuffles across the sector boundary.</summary>
    private static byte SectorPeak(byte[] pcm, int offset)
    {
        int peak = 0;
        int end = offset + SectorAudioBytes;
        for (int i = offset; i + 1 < end; i += 2)
        {
            short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            int mag = sample == short.MinValue ? short.MaxValue : Math.Abs((int)sample);
            if (mag > peak) peak = mag;
        }
        // Coarse loudness bucket (0–31). Deliberately coarse: a read offset nudges a
        // 588-sample peak only slightly, so at this granularity the bucket almost
        // never moves — yet different material lands in very different buckets.
        return (byte)(peak >> 10);
    }

    /// <summary>Fraction of overlapping envelope entries that agree, when b is shifted
    /// by <paramref name="shift"/> sectors relative to a.</summary>
    private static double EnvelopeSimilarity(byte[] a, byte[] b, int shift)
    {
        long agree = 0, total = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int j = i + shift;
            if (j < 0 || j >= b.Length) continue;
            total++;
            // Allow a one-bucket wobble: quantisation can nudge a peak by ±1.
            if (Math.Abs(a[i] - b[j]) <= 1) agree++;
        }
        return total == 0 ? 0 : (double)agree / total;
    }

    private static string Sha(byte[] data) =>
        System.Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
