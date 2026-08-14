// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.BluRay;

/// <summary>
/// Raised when a BDMV metadata file (.mpls playlist / .clpi clip-info) is too
/// short or structurally invalid. It exists so callers can distinguish "this is
/// not a valid BDMV file" from a genuine bug: every bounds check and every magic
/// check in the readers funnels through here, so a truncated or corrupt file
/// surfaces as this domain exception rather than an IndexOutOfRange/overflow.
/// </summary>
public sealed class BluRayFormatException(string message) : Exception(message);

/// <summary>
/// Blu-ray presentation times are counted in 45 kHz ticks (the 90 kHz PTS clock
/// halved) — a PlayItem's IN/OUT time and every chapter mark are stored this way.
/// This centralises the single conversion (45000 ticks == one second) so the
/// readers and the CLI agree on how a raw tick count becomes a wall-clock time.
/// </summary>
public static class BdmvTime
{
    /// <summary>Ticks per second on the Blu-ray 45 kHz presentation clock.</summary>
    public const long TicksPerSecond = 45000;

    /// <summary>Convert a 45 kHz tick count to a <see cref="TimeSpan"/>.</summary>
    public static TimeSpan ToTimeSpan(long ticks) =>
        TimeSpan.FromMilliseconds(ticks * 1000.0 / TicksPerSecond);

    /// <summary>Format a 45 kHz tick count as HH:MM:SS.mmm.</summary>
    public static string Format(long ticks)
    {
        var t = ToTimeSpan(ticks);
        long hours = (long)t.TotalHours;
        return $"{hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }
}

/// <summary>
/// The classes of elementary stream a Blu-ray title can carry. Which class a
/// stream belongs to is decided by its position in the STN table (primary vs
/// secondary section) rather than by its coding type alone, so the reader passes
/// the section in explicitly.
/// </summary>
public enum StreamKind
{
    Video,
    Audio,
    PresentationGraphics,   // PG — the subtitle/caption plane
    InteractiveGraphics,    // IG — the menu plane
    SecondaryAudio,
    SecondaryVideo,
    TextSubtitle,
    Unknown,
}

/// <summary>
/// The stream_coding_type byte values defined by the BDMV format, and their
/// human-readable names. These are shared between the MPLS STN table and the
/// CLPI ProgramInfo, which use the same coding-type namespace.
/// </summary>
public static class BdmvCoding
{
    public const byte Mpeg1Video = 0x01;
    public const byte Mpeg2Video = 0x02;
    public const byte Avc = 0x1B;         // H.264/AVC
    public const byte Mvc = 0x20;         // H.264 MVC (3D dependent view)
    public const byte Hevc = 0x24;        // H.265/HEVC
    public const byte Vc1 = 0xEA;

    public const byte Mpeg1Audio = 0x03;
    public const byte Mpeg2Audio = 0x04;
    public const byte Lpcm = 0x80;
    public const byte Ac3 = 0x81;         // Dolby Digital
    public const byte Dts = 0x82;
    public const byte TrueHd = 0x83;      // Dolby TrueHD
    public const byte Eac3 = 0x84;        // Dolby Digital Plus
    public const byte DtsHdHr = 0x85;     // DTS-HD High Resolution
    public const byte DtsHdMa = 0x86;     // DTS-HD Master Audio
    public const byte Eac3Secondary = 0xA1;
    public const byte DtsHdSecondary = 0xA2;

    public const byte PresentationGraphics = 0x90;   // PG (subtitles)
    public const byte InteractiveGraphics = 0x91;    // IG (menus)
    public const byte TextSubtitle = 0x92;

    /// <summary>Friendly name for a coding-type byte; unknown values render as hex.</summary>
    public static string Name(byte coding) => coding switch
    {
        Mpeg1Video => "MPEG-1 Video",
        Mpeg2Video => "MPEG-2 Video",
        Avc => "H.264/AVC",
        Mvc => "H.264 MVC",
        Hevc => "H.265/HEVC",
        Vc1 => "VC-1",
        Mpeg1Audio => "MPEG-1 Audio",
        Mpeg2Audio => "MPEG-2 Audio",
        Lpcm => "LPCM",
        Ac3 => "Dolby Digital (AC-3)",
        Dts => "DTS",
        TrueHd => "Dolby TrueHD",
        Eac3 => "Dolby Digital Plus (E-AC-3)",
        DtsHdHr => "DTS-HD High Resolution",
        DtsHdMa => "DTS-HD Master Audio",
        Eac3Secondary => "Dolby Digital Plus (secondary)",
        DtsHdSecondary => "DTS-HD (secondary)",
        PresentationGraphics => "Presentation Graphics",
        InteractiveGraphics => "Interactive Graphics",
        TextSubtitle => "Text Subtitle",
        _ => $"Unknown (0x{coding:X2})",
    };

    /// <summary>True for the video coding-type bytes.</summary>
    public static bool IsVideo(byte c) =>
        c is Mpeg1Video or Mpeg2Video or Avc or Mvc or Hevc or Vc1;

    /// <summary>True for the audio coding-type bytes.</summary>
    public static bool IsAudio(byte c) =>
        c is Mpeg1Audio or Mpeg2Audio or Lpcm or Ac3 or Dts or TrueHd or Eac3
          or DtsHdHr or DtsHdMa or Eac3Secondary or DtsHdSecondary;

    /// <summary>Best-effort stream class for a bare coding type (used by CLPI,
    /// which — unlike the MPLS STN table — carries no primary/secondary sections).</summary>
    public static StreamKind KindOf(byte c)
    {
        if (IsVideo(c)) return StreamKind.Video;
        if (IsAudio(c)) return StreamKind.Audio;
        return c switch
        {
            PresentationGraphics => StreamKind.PresentationGraphics,
            InteractiveGraphics => StreamKind.InteractiveGraphics,
            TextSubtitle => StreamKind.TextSubtitle,
            _ => StreamKind.Unknown,
        };
    }

    // Video/audio format and rate code tables, from the public BDMV description.
    // These decode the 4-bit nibbles StreamCodingInfo/stream_attributes carry.

    public static string VideoFormat(int code) => code switch
    {
        1 => "480i", 2 => "576i", 3 => "480p", 4 => "1080i",
        5 => "720p", 6 => "1080p", 7 => "576p", 8 => "2160p",
        _ => $"?({code})",
    };

    public static string FrameRate(int code) => code switch
    {
        1 => "23.976", 2 => "24", 3 => "25", 4 => "29.97",
        6 => "50", 7 => "59.94",
        _ => $"?({code})",
    };

    public static string AspectRatio(int code) => code switch
    {
        2 => "4:3", 3 => "16:9",
        _ => $"?({code})",
    };

    public static string AudioFormat(int code) => code switch
    {
        1 => "mono", 3 => "stereo", 6 => "multichannel", 12 => "stereo+multichannel",
        _ => $"?({code})",
    };

    public static string SampleRate(int code) => code switch
    {
        1 => "48kHz", 4 => "96kHz", 5 => "192kHz",
        12 => "48/192kHz", 14 => "48/96kHz",
        _ => $"?({code})",
    };
}

/// <summary>
/// A bounds-checked, big-endian cursor over a BDMV byte buffer. BDMV multi-byte
/// integers are big-endian (unusual on a PC and the classic source of absurd
/// results), so every read goes through <see cref="System.Buffers.Binary"/>.
/// Every read validates the range and throws <see cref="BluRayFormatException"/>
/// on overrun — that is what turns a truncated file into a clean domain error.
/// </summary>
internal sealed class BdmvReaderCursor
{
    private readonly byte[] _data;
    public int Position { get; private set; }

    public BdmvReaderCursor(byte[] data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public int Length => _data.Length;

    public void Seek(long position)
    {
        if (position < 0 || position > _data.Length)
            throw new BluRayFormatException(
                $"BDMV offset {position} is outside the {_data.Length}-byte file.");
        Position = (int)position;
    }

    private void Need(int count)
    {
        if (count < 0 || Position + count > _data.Length)
            throw new BluRayFormatException(
                $"BDMV file truncated: needed {count} byte(s) at offset {Position}, " +
                $"only {_data.Length - Position} remain.");
    }

    public byte ReadU8()
    {
        Need(1);
        return _data[Position++];
    }

    public ushort ReadU16()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Position));
        Position += 2;
        return v;
    }

    public uint ReadU32()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(Position));
        Position += 4;
        return v;
    }

    /// <summary>Read a fixed-width ASCII field, trimming trailing NULs/spaces.</summary>
    public string ReadAscii(int count)
    {
        Need(count);
        string s = Encoding.ASCII.GetString(_data, Position, count).TrimEnd('\0', ' ');
        Position += count;
        return s;
    }

    /// <summary>Read a 3-byte ISO-639 language code (empty when all-NUL/blank).</summary>
    public string ReadLanguage()
    {
        Need(3);
        string s = Encoding.ASCII.GetString(_data, Position, 3).TrimEnd('\0', ' ');
        Position += 3;
        return s;
    }

    public void Skip(int count)
    {
        Need(count);
        Position += count;
    }

    public void RequireMagic(string magic)
    {
        int at = Position;
        string got = ReadAscii(magic.Length);
        if (!string.Equals(got, magic, StringComparison.Ordinal))
            throw new BluRayFormatException(
                $"Expected BDMV magic \"{magic}\" at offset {at}, found \"{got}\".");
    }
}
