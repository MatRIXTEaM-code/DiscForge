// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Audio;

namespace DiscForge.Core.PlayStation;

/// <summary>The submode bits of a CD-ROM XA Mode 2 subheader (byte 2 of the 8-byte subheader).</summary>
[Flags]
public enum XaSubmode : byte
{
    None = 0,
    EndOfRecord = 0x01,   // EOR — last sector of a record
    Video = 0x02,
    Audio = 0x04,
    Data = 0x08,
    Trigger = 0x10,
    Form2 = 0x20,         // 2324-byte data area (no EDC); clear = Form 1 (2048-byte + EDC)
    RealTime = 0x40,      // RT — real-time sector, decode as it streams
    EndOfFile = 0x80,     // EOF — last sector of a file
}

/// <summary>The traffic on one (file, channel) stream: how its sectors break down by kind.</summary>
public sealed record XaChannelStat(int File, int Channel)
{
    public int Sectors { get; set; }
    public int VideoSectors { get; set; }
    public int AudioSectors { get; set; }
    public int DataSectors { get; set; }
    public int Form2Sectors { get; set; }
    public int Records { get; set; }         // EOR count
    public bool EndsFile { get; set; }       // saw EOF
    /// <summary>Coding of the first audio sector seen (sample rate / stereo / bit depth), if any.</summary>
    public XaAdpcm.CodingInfo? Audio { get; set; }

    public string Kind =>
        VideoSectors > 0 && AudioSectors > 0 ? "A/V" :
        VideoSectors > 0 ? "video" :
        AudioSectors > 0 ? "audio" :
        DataSectors > 0 ? "data" : "other";
}

/// <summary>The whole disc's XA multimedia structure.</summary>
public sealed record XaStreamReport
{
    public required int Mode2Sectors { get; init; }
    public required int Form1Sectors { get; init; }
    public required int Form2Sectors { get; init; }
    public required int VideoSectors { get; init; }
    public required int AudioSectors { get; init; }
    public required IReadOnlyList<XaChannelStat> Channels { get; init; }
    /// <summary>How many times the active (file,channel) changed sector-to-sector — a high count relative
    /// to the stream count means the streams are finely interleaved for real-time playback.</summary>
    public required int InterleaveSwitches { get; init; }

    public bool IsXa => Mode2Sectors > 0 && Channels.Count > 0;

    public string Summary()
    {
        if (!IsXa) return "No CD-ROM XA Mode 2 form-2/audio-video structure found.";
        return $"XA: {Mode2Sectors:N0} Mode 2 sectors ({Form1Sectors:N0} Form 1, {Form2Sectors:N0} Form 2), " +
               $"{VideoSectors:N0} video, {AudioSectors:N0} audio across {Channels.Count} stream(s); " +
               $"{InterleaveSwitches:N0} interleave switch(es).";
    }
}

/// <summary>
/// xa-map — the multimedia read of a CD-ROM XA disc, the structure behind PlayStation FMV, Video CD, and
/// CD-i. Every Mode 2 sector carries an 8-byte subheader — file, channel, submode, coding — and the
/// submode bits split the sector into video, audio, or data, mark Form 1 (2048-byte, with EDC) versus
/// Form 2 (2324-byte, no EDC), and flag the end of each record (EOR) and file (EOF). Real-time titles
/// finely interleave several (file, channel) streams so the drive can feed video and audio together in
/// one pass. This walks the sectors, tallies each stream by kind, reads the first audio coding it sees
/// (sample rate, mono/stereo, bit depth), and measures how tightly the streams interleave — turning a
/// raw image into a map of what plays and how it is laid out. Read-only; it parses and reports.
/// </summary>
public static class XaStreamMap
{
    /// <summary>Analyse a raw image. <paramref name="layout"/> selects the sector geometry (2352 raw or
    /// 2336 Mode 2); default is raw 2352.</summary>
    public static XaStreamReport Analyze(ReadOnlySpan<byte> image, XaExtract.SectorLayout? layout = null)
    {
        var lay = layout ?? XaExtract.SectorLayout.Raw2352;
        int ss = lay.SectorSize;
        if (ss <= 0 || image.Length < ss)
            return Empty();

        int sectors = image.Length / ss;
        var stats = new Dictionary<(int, int), XaChannelStat>();
        int mode2 = 0, form1 = 0, form2 = 0, video = 0, audio = 0, switches = 0;
        (int, int)? prevKey = null;

        for (int s = 0; s < sectors; s++)
        {
            var sec = image.Slice(s * ss, ss);

            // For raw 2352, only Mode 2 sectors carry an XA subheader (header byte 3 == 0x02).
            if (lay == XaExtract.SectorLayout.Raw2352)
            {
                if (!HasSync(sec)) continue;
                if (sec[15] != 0x02) continue;      // not Mode 2 → no XA subheader
            }
            mode2++;

            int sh = lay.SubheaderOffset;
            int file = sec[sh];
            int channel = sec[sh + 1];
            var submode = (XaSubmode)sec[sh + 2];
            byte coding = sec[sh + 3];

            bool isForm2 = submode.HasFlag(XaSubmode.Form2);
            if (isForm2) form2++; else form1++;

            var key = (file, channel);
            if (prevKey != null && prevKey.Value != key) switches++;
            prevKey = key;

            if (!stats.TryGetValue(key, out var st))
                stats[key] = st = new XaChannelStat(file, channel);
            st.Sectors++;
            if (isForm2) st.Form2Sectors++;
            if (submode.HasFlag(XaSubmode.EndOfRecord)) st.Records++;
            if (submode.HasFlag(XaSubmode.EndOfFile)) st.EndsFile = true;

            if (submode.HasFlag(XaSubmode.Video)) { st.VideoSectors++; video++; }
            if (submode.HasFlag(XaSubmode.Audio))
            {
                st.AudioSectors++; audio++;
                st.Audio ??= XaAdpcm.CodingInfo.Parse(coding);
            }
            if (submode.HasFlag(XaSubmode.Data)) st.DataSectors++;
        }

        var channels = stats.Values
            .OrderBy(c => c.File).ThenBy(c => c.Channel)
            .ToList();

        return new XaStreamReport
        {
            Mode2Sectors = mode2, Form1Sectors = form1, Form2Sectors = form2,
            VideoSectors = video, AudioSectors = audio,
            Channels = channels, InterleaveSwitches = switches,
        };
    }

    public static string Render(XaStreamReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var c in r.Channels.Take(40))
        {
            string a = c.Audio is { } ci ? $", {ci.SampleRate}Hz {(ci.Stereo ? "stereo" : "mono")} {(ci.EightBit ? "8" : "4")}-bit" : "";
            sb.AppendLine($"  file {c.File} ch {c.Channel}: {c.Kind}, {c.Sectors} sector(s) " +
                          $"(v{c.VideoSectors}/a{c.AudioSectors}/d{c.DataSectors}), {c.Records} record(s)" +
                          $"{(c.EndsFile ? ", EOF" : "")}{a}");
        }
        if (r.Channels.Count > 40) sb.AppendLine($"  … and {r.Channels.Count - 40} more stream(s)");
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    private static XaStreamReport Empty() => new()
    {
        Mode2Sectors = 0, Form1Sectors = 0, Form2Sectors = 0, VideoSectors = 0, AudioSectors = 0,
        Channels = Array.Empty<XaChannelStat>(), InterleaveSwitches = 0,
    };

    private static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s.Length < 16 || s[0] != 0x00 || s[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }
}
