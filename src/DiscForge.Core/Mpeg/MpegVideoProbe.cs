// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Mpeg;

/// <summary>One elementary stream a probe found, described for a listing.</summary>
public sealed record MpegStreamInfo(byte StreamId, MpegStreamKind Kind, string Codec, long Bytes);

/// <summary>What a probe read from an MPEG program stream: the video sequence-header facts (dimensions,
/// aspect, frame rate, bit rate, MPEG-1 vs -2) and the elementary streams present, including whether CRI
/// ADX audio — the mark of a Dreamcast Sofdec .sfd — is multiplexed in.</summary>
public sealed record MpegVideoSummary
{
    public required bool IsProgramStream { get; init; }
    public bool HasVideo => Width > 0 && Height > 0;
    public bool IsMpeg2 { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }
    public int AspectCode { get; init; }
    public int FrameRateCode { get; init; }
    public double Fps { get; init; }
    public long BitrateBps { get; init; }
    public bool VariableBitrate { get; init; }

    public required IReadOnlyList<MpegStreamInfo> Streams { get; init; }
    /// <summary>CRI ADX audio is present — this is very likely a Sofdec (.sfd) stream.</summary>
    public bool HasAdx { get; init; }

    public string Container => !IsProgramStream ? "not an MPEG program stream"
        : HasAdx ? "MPEG program stream (Sofdec — CRI ADX audio)"
        : IsMpeg2 ? "MPEG-2 program stream" : "MPEG-1 program stream";

    public string Summary()
    {
        if (!IsProgramStream) return "not an MPEG program stream (no pack start code 00 00 01 BA).";
        var sb = new StringBuilder(Container);
        if (HasVideo)
        {
            sb.Append($": {Width}×{Height}, {AspectName(AspectCode)}, ");
            sb.Append(Fps > 0 ? $"{Fps:0.###} fps" : $"frame-rate code {FrameRateCode}");
            sb.Append(VariableBitrate ? ", variable bit rate" :
                      BitrateBps > 0 ? $", {BitrateBps / 1000.0:0.#} kbps" : "");
        }
        else sb.Append(": no video sequence header found");
        sb.Append($"; {Streams.Count} elementary stream(s).");
        return sb.ToString();
    }

    private static string AspectName(int code) => code switch
    {
        1 => "1:1", 2 => "4:3", 3 => "16:9", 4 => "2.21:1",
        _ => $"aspect code {code}",
    };
}

/// <summary>
/// mpeg-info — read (never rewrite) an MPEG program stream (VCD/SVCD <c>.mpg</c>, DVD <c>.VOB</c>, or a
/// Dreamcast Sofdec <c>.sfd</c>) and describe it: the video sequence header (dimensions, aspect, frame
/// rate, bit rate, MPEG-1 vs -2) and the elementary streams present. It recognises CRI ADX audio, the
/// tell-tale of a Sofdec movie, so a Dreamcast dump's cutscene files can be catalogued and sanity-checked.
/// Purely descriptive: it reads the public MPEG syntax, decrypts nothing, and decodes no frames.
/// </summary>
public static class MpegVideoProbe
{
    public static MpegVideoSummary Probe(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // A program stream opens with a pack header: pack_start_code 0x000001BA.
        bool isPs = data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0xBA;
        if (!isPs)
            return new MpegVideoSummary { IsProgramStream = false, Streams = Array.Empty<MpegStreamInfo>() };

        var demux = MpegProgramStream.Demux(data);

        var infos = new List<MpegStreamInfo>();
        bool hasAdx = false;
        byte[]? videoEs = null;
        foreach (var s in demux.Streams)
        {
            string codec = Codec(s, out bool adx);
            if (adx) hasAdx = true;
            if (s.Kind == MpegStreamKind.Video && videoEs is null) videoEs = s.Data;
            infos.Add(new MpegStreamInfo(s.StreamId, s.Kind, codec, s.Data.LongLength));
        }

        var seq = videoEs is not null ? ParseSequenceHeader(videoEs) : default;

        return new MpegVideoSummary
        {
            IsProgramStream = true,
            IsMpeg2 = seq.IsMpeg2,
            Width = seq.Width,
            Height = seq.Height,
            AspectCode = seq.AspectCode,
            FrameRateCode = seq.FrameRateCode,
            Fps = seq.Fps,
            BitrateBps = seq.BitrateBps,
            VariableBitrate = seq.Variable,
            Streams = infos,
            HasAdx = hasAdx,
        };
    }

    public static MpegVideoSummary ProbeFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Probe(File.ReadAllBytes(path));
    }

    private static string Codec(MpegElementaryStream s, out bool adx)
    {
        adx = false;
        // CRI ADX begins with 0x80 0x00 (the ADX sync + copyright-offset marker).
        if (s.Kind is MpegStreamKind.Audio or MpegStreamKind.Private1 &&
            s.Data.Length >= 2 && s.Data[0] == 0x80 && s.Data[1] == 0x00)
        {
            adx = true;
            return "CRI ADX";
        }
        return s.Kind switch
        {
            MpegStreamKind.Video => "MPEG video",
            MpegStreamKind.Audio => "MPEG audio (Layer II)",
            MpegStreamKind.Private1 => "private (AC3/DTS/LPCM)",
            MpegStreamKind.Private2 => "navigation",
            _ => "other",
        };
    }

    private readonly record struct SeqHeader(
        int Width, int Height, int AspectCode, int FrameRateCode, double Fps,
        long BitrateBps, bool Variable, bool IsMpeg2);

    // Parse the MPEG video sequence_header (start code 00 00 01 B3) and detect the MPEG-2
    // sequence_extension (00 00 01 B5, ext id 1) that follows it on MPEG-2 video.
    private static SeqHeader ParseSequenceHeader(ReadOnlySpan<byte> es)
    {
        int at = FindStartCode(es, 0xB3, 0);
        if (at < 0 || at + 4 + 8 > es.Length) return default;
        var h = es[(at + 4)..];

        int width = (h[0] << 4) | (h[1] >> 4);
        int height = ((h[1] & 0x0F) << 8) | h[2];
        int aspect = h[3] >> 4;
        int frCode = h[3] & 0x0F;
        int bitrate = (h[4] << 10) | (h[5] << 2) | (h[6] >> 6);   // units of 400 bps
        bool variable = bitrate == 0x3FFFF;

        bool mpeg2 = false;
        int ext = FindStartCode(es, 0xB5, at + 4);
        if (ext >= 0 && ext + 4 < es.Length && (es[ext + 4] >> 4) == 0x1) mpeg2 = true;

        return new SeqHeader(width, height, aspect, frCode, FrameRate(frCode),
                             variable ? 0 : bitrate * 400L, variable, mpeg2);
    }

    private static double FrameRate(int code) => code switch
    {
        1 => 24000.0 / 1001, 2 => 24, 3 => 25, 4 => 30000.0 / 1001,
        5 => 30, 6 => 50, 7 => 60000.0 / 1001, 8 => 60, _ => 0,
    };

    // Find "00 00 01 <code>" at or after start.
    private static int FindStartCode(ReadOnlySpan<byte> s, byte code, int start)
    {
        for (int i = Math.Max(0, start); i + 4 <= s.Length; i++)
            if (s[i] == 0 && s[i + 1] == 0 && s[i + 2] == 1 && s[i + 3] == code) return i;
        return -1;
    }
}
