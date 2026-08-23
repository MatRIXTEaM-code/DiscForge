// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Pressing DNA against the cases it exists for: identical rips share a
/// pressing id; a write-offset difference (the same audio shifted by a
/// constant number of samples) keeps the CONTENT identity but changes the
/// PRESSING identity, and the comparator names the shift; different geometry
/// or subcode identity likewise separates pressings; different content
/// separates discs entirely.
/// </summary>
public class PressingDnaTests
{
    private const int SectorBytes = 2352;

    /// <summary>Audio with real structure: silence, then a deterministic tone
    /// body, then silence — so edges are meaningful and an envelope exists.</summary>
    private static byte[] AudioTrack(int sectors, int leadSilenceSamples, int seed)
    {
        var b = new byte[sectors * SectorBytes];
        var rnd = new Random(seed);
        int totalSamples = b.Length / 2;
        int tailSilence = 2000;
        for (int s = leadSilenceSamples; s < totalSamples - tailSilence; s++)
        {
            short v = (short)rnd.Next(-2000, 2001);
            if (v == 0) v = 7;                      // keep the body strictly non-zero
            b[s * 2] = (byte)(v & 0xFF);
            b[s * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return b;
    }

    private static byte[] DataTrack(int sectors, int seed)
    {
        var b = new byte[sectors * SectorBytes];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static List<GenomeTrack> Disc(int audioSeed = 5, int leadSilence = 1000)
        => new()
        {
            new GenomeTrack(1, IsData: true, DataTrack(40, 1)),
            new GenomeTrack(2, IsData: false, AudioTrack(150, leadSilence, audioSeed)),
            new GenomeTrack(3, IsData: false, AudioTrack(120, leadSilence + 500, audioSeed + 1)),
        };

    [Fact]
    public void IdenticalRips_SharePressingAndContentIds()
    {
        var a = PressingDna.Compute(Disc(), new Dictionary<int, int> { [2] = 150 }, "1234567890123");
        var b = PressingDna.Compute(Disc(), new Dictionary<int, int> { [2] = 150 }, "1234567890123");

        Assert.Equal(a.PressingId, b.PressingId);
        Assert.Equal(a.ContentId, b.ContentId);
        var m = PressingDna.Compare(a, b);
        Assert.True(m.SamePressing);
        Assert.True(m.SameContent);
        Assert.Empty(m.Differences);
        Assert.StartsWith("SAME PRESSING", m.Verdict);
    }

    /// <summary>
    /// The signature move: identical audio mastered with a different write
    /// offset — every track's sound sits N samples later. Content identity
    /// survives; pressing identity does not; the shift is named.
    /// </summary>
    [Fact]
    public void ConstantAudioShift_IsSameTitleDifferentPressing_WithNamedOffset()
    {
        // A write offset delays the WHOLE sample stream: prepend N samples of
        // silence and drop N from the tail — both edges move by exactly N.
        static byte[] Shift(byte[] track, int samples)
        {
            var shifted = new byte[track.Length];
            Array.Copy(track, 0, shifted, samples * 2, track.Length - samples * 2);
            return shifted;
        }

        const int shift = 30;                       // samples, well inside genome tolerance
        var baseDisc = Disc(leadSilence: 1000);
        var a = PressingDna.Compute(baseDisc);
        var b = PressingDna.Compute(baseDisc
            .Select(t => t.IsData ? t : t with { Content = Shift(t.Content, shift) }).ToList());

        var m = PressingDna.Compare(a, b);
        Assert.True(m.SameContent, "a small constant shift must not change the content identity");
        Assert.False(m.SamePressing);
        Assert.Contains(m.Differences, d => d.Contains($"shifted by +{shift} sample"));
        Assert.Contains("DIFFERENT PRESSING", m.Verdict);
    }

    [Fact]
    public void DifferentPregap_SeparatesPressings()
    {
        var a = PressingDna.Compute(Disc(), new Dictionary<int, int> { [2] = 150 });
        var b = PressingDna.Compute(Disc(), new Dictionary<int, int> { [2] = 152 });

        var m = PressingDna.Compare(a, b);
        Assert.True(m.SameContent);
        Assert.False(m.SamePressing);
        Assert.Contains(m.Differences, d => d.StartsWith("track 02") && d.Contains("pregap"));
    }

    [Fact]
    public void DifferentMcn_SeparatesPressings()
    {
        var a = PressingDna.Compute(Disc(), mcn: "1234567890123");
        var b = PressingDna.Compute(Disc(), mcn: "9999999990123");

        var m = PressingDna.Compare(a, b);
        Assert.True(m.SameContent);
        Assert.False(m.SamePressing);
        Assert.Contains(m.Differences, d => d.StartsWith("mcn:"));
    }

    [Fact]
    public void DifferentContent_IsDifferentDiscs()
    {
        var a = PressingDna.Compute(Disc(audioSeed: 5));
        var b = PressingDna.Compute(new List<GenomeTrack>
        {
            new(1, IsData: true, DataTrack(40, 99)),            // different data
            new(2, IsData: false, AudioTrack(150, 1000, 77)),   // different audio
            new(3, IsData: false, AudioTrack(120, 1500, 78)),
        });

        var m = PressingDna.Compare(a, b);
        Assert.False(m.SameContent);
        Assert.False(m.SamePressing);
        Assert.Equal("different discs", m.Verdict);
    }

    [Fact]
    public void AudioEdges_FindFirstAndLastSound_AndSilence()
    {
        var t = AudioTrack(10, leadSilenceSamples: 500, seed: 3);
        var (first, last) = PressingDna.AudioEdges(t);
        Assert.Equal(500, first);
        Assert.Equal(t.Length / 2 - 2000 - 1, last);

        Assert.Equal((-1, -1), PressingDna.AudioEdges(new byte[5 * SectorBytes]));
    }
}
