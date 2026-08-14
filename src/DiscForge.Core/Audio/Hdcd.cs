// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Audio;

/// <summary>The result of scanning PCM audio for HDCD control codes.</summary>
public sealed record HdcdScanResult
{
    /// <summary>Count of Type-A control-code windows seen. NOTE: the Type-A pattern constrains only ~11 bits,
    /// so it occurs by chance roughly once every 2048 samples — treat this as a weak indicator, not proof.</summary>
    public required int PacketsTypeA { get; init; }
    /// <summary>Count of valid <b>Type-B</b> control packets — these carry a one's-complement self-check
    /// (~24 constrained bits), so a chance match in ordinary audio is astronomically unlikely.</summary>
    public required int PacketsTypeB { get; init; }
    /// <summary>Total 16-bit samples scanned across all channels.</summary>
    public required long SamplesScanned { get; init; }

    /// <summary>How many Type-A hits would be expected from random LSBs over this many samples (the noise floor).</summary>
    public double TypeANoiseFloor => SamplesScanned / 2048.0;
    /// <summary>Type-A hits are "significant" only when they run well above the random noise floor.</summary>
    public bool TypeASignificant => PacketsTypeA > TypeANoiseFloor * 4 && PacketsTypeA > 8;

    /// <summary>
    /// True when the audio carries HDCD encoding. Keyed on the self-checking Type-B packets (reliable), or a
    /// Type-A count far above the statistical noise floor. A disc that only ever emits Type-A codes at a low
    /// rate cannot be told apart from noise by LSB scanning alone — confirm against a known HDCD disc.
    /// </summary>
    public bool Detected => PacketsTypeB > 0 || TypeASignificant;
    public int TotalPackets => PacketsTypeA + PacketsTypeB;
}

/// <summary>
/// Detects <b>HDCD</b> (High Definition Compatible Digital) encoding in 16-bit PCM audio. HDCD hides a serial
/// control stream in the least-significant bit of the samples; on ordinary playback those bits are just noise,
/// but an HDCD decoder reads periodic control packets (peak-extend, low-level gain, transient filter) from
/// them. This scans the LSB stream of each channel for those packets using the recognised control-code
/// bit-patterns, so a rip can be flagged as HDCD-encoded — a preservation-relevant property a plain CRC never
/// reveals. It only <i>detects</i> the packets (reads and reports); it does not expand or alter the audio.
///
/// Detection is from the public HDCD control-code description (the libhdcd/ffmpeg constants). The pattern is a
/// ~28-bit sync, so false positives on ordinary audio are astronomically unlikely; the Type-B form additionally
/// carries a one's-complement self-check. Confirmation against a genuine HDCD-encoded disc is still advisable.
/// </summary>
public static class Hdcd
{
    /// <summary>True if a 32-bit LSB window matches a Type-A HDCD control code.</summary>
    public static bool IsTypeA(uint w) => (w & 0x0fa00500u) == 0x0fa00500u && (w & 0xc8u) == 0u;

    /// <summary>True if a 32-bit LSB window matches a Type-B HDCD control code (with its self-check).</summary>
    public static bool IsTypeB(uint w) =>
        (w & 0xa0060000u) == 0xa0060000u &&
        ((w ^ (((~w) >> 8) & 0xffu)) & 0xffff00ffu) == 0xa0060000u;

    /// <summary>
    /// Scan interleaved 16-bit PCM for HDCD control packets. Each channel's LSB stream is followed
    /// independently through a 32-bit sliding window.
    /// </summary>
    public static HdcdScanResult Scan(ReadOnlySpan<short> samples, int channels)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));

        Span<uint> window = channels <= 8 ? stackalloc uint[channels] : new uint[channels];
        int a = 0, b = 0;
        int ch = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            uint w = (window[ch] << 1) | (uint)(samples[i] & 1);
            window[ch] = w;
            if (IsTypeB(w)) b++;
            else if (IsTypeA(w)) a++;
            if (++ch == channels) ch = 0;
        }

        return new HdcdScanResult { PacketsTypeA = a, PacketsTypeB = b, SamplesScanned = samples.Length };
    }

    /// <summary>
    /// Scan a little-endian 16-bit PCM byte buffer (as found in a WAV data chunk or a raw CD-audio track).
    /// </summary>
    public static HdcdScanResult ScanPcmBytes(ReadOnlySpan<byte> pcm, int channels)
    {
        int n = pcm.Length / 2;
        var samples = new short[n];
        for (int i = 0; i < n; i++)
            samples[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
        return Scan(samples, channels);
    }
}
