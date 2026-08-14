// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.GameAudio;

/// <summary>
/// The ID666 metadata carried by an SNES SPC700 sound-register dump. Only the
/// text fields are surfaced (no APU RAM, DSP registers or audio are interpreted).
/// </summary>
public sealed class SpcFile
{
    public required string SongTitle { get; init; }
    public required string GameTitle { get; init; }
    public required string DumperName { get; init; }
    public required string Comments { get; init; }
    public required string Artist { get; init; }
    public required string DumpDate { get; init; }

    /// <summary>True when the header flags an ID666 tag (byte 0x23 == 0x1A).</summary>
    public required bool HasId666 { get; init; }

    /// <summary>
    /// True when the ID666 block was read as the "text" sub-format (ASCII date /
    /// length fields) rather than the "binary" sub-format. Informational only.
    /// </summary>
    public required bool TextFormatTag { get; init; }
}

/// <summary>
/// Reads the header and ID666 tag of an SNES SPC700 sound file. The 33-byte
/// magic "SNES-SPC700 Sound File Data v0.30" sits at 0x00, 0x21/0x22 are 0x1A,
/// 0x23 is the has-ID666 flag (0x1A = tag present). The ID666 block at 0x2E has
/// two sub-formats that differ only in the date/length area and the artist
/// offset (0xB1 text vs 0xB0 binary); the sub-format is detected heuristically
/// from the date field. String fields (title/game/dumper/comments/artist) are
/// read as Latin-1. No audio, APU RAM or DSP state is interpreted.
/// </summary>
public static class SpcReader
{
    /// <summary>The prefix every SPC file shares (the minor version may vary).</summary>
    public const string MagicPrefix = "SNES-SPC700 Sound File";

    // ID666 fixed field offsets (shared by both sub-formats).
    private const int SongTitleOff = 0x2E;   // 32
    private const int GameTitleOff = 0x4E;   // 32
    private const int DumperOff = 0x6E;      // 16
    private const int CommentsOff = 0x7E;    // 32
    private const int DateOff = 0x9E;        // 11 (text) / 4 + pad (binary)
    private const int ArtistTextOff = 0xB1;  // 32 (text sub-format)
    private const int ArtistBinaryOff = 0xB0;// 32 (binary sub-format)

    public static bool IsSpc(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return HasMagic(data);
    }

    public static bool IsSpc(Stream stream) => IsSpc(ReadHead(stream, 0x30));

    public static SpcFile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Read(ReadAll(stream));
    }

    public static SpcFile Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 0x2E)
            throw new GameAudioFormatException($"SPC file is only {data.Length} bytes — too short for the header.");
        if (!HasMagic(data))
            throw new GameAudioFormatException("Not an SPC file — missing the \"SNES-SPC700 Sound File\" magic.");

        bool hasId666 = data.Length > 0x23 && data[0x23] == 0x1A;

        // Detect the text vs binary ID666 sub-format from the 11-byte date field.
        bool text = LooksLikeTextDate(data);
        int artistOff = text ? ArtistTextOff : ArtistBinaryOff;

        string date = "";
        if (data.Length >= DateOff + 11)
            date = ReadString(data, DateOff, text ? 11 : 4);

        return new SpcFile
        {
            SongTitle = ReadString(data, SongTitleOff, 32),
            GameTitle = ReadString(data, GameTitleOff, 32),
            DumperName = ReadString(data, DumperOff, 16),
            Comments = ReadString(data, CommentsOff, 32),
            Artist = ReadString(data, artistOff, 32),
            DumpDate = date,
            HasId666 = hasId666,
            TextFormatTag = text,
        };
    }

    /// <summary>
    /// Heuristic: the ID666 "text" sub-format stores the dump date at 0x9E as an
    /// ASCII string (e.g. "05/23/2000"); the "binary" sub-format stores it as raw
    /// integer bytes. If the field's non-zero bytes are all ASCII digits, slashes,
    /// dashes, dots or spaces, treat it as the text sub-format. An all-zero /
    /// ambiguous field defaults to text (the more common tagged form).
    /// </summary>
    private static bool LooksLikeTextDate(byte[] data)
    {
        if (data.Length < DateOff + 11) return true;
        bool sawDigit = false;
        for (int i = DateOff; i < DateOff + 10; i++)
        {
            byte b = data[i];
            if (b == 0) continue;
            bool digit = b is >= (byte)'0' and <= (byte)'9';
            if (digit) sawDigit = true;
            else if (b is not ((byte)'/' or (byte)'-' or (byte)'.' or (byte)' '))
                return false;   // a non-date byte → binary sub-format
        }
        return sawDigit || AllZero(data, DateOff, 10);
    }

    private static bool AllZero(byte[] data, int at, int len)
    {
        for (int i = at; i < at + len && i < data.Length; i++)
            if (data[i] != 0) return false;
        return true;
    }

    private static bool HasMagic(byte[] data)
    {
        if (data.Length < MagicPrefix.Length) return false;
        for (int i = 0; i < MagicPrefix.Length; i++)
            if (data[i] != (byte)MagicPrefix[i]) return false;
        return true;
    }

    // ID666 strings are fixed-width, NUL-padded, Latin-1.
    private static string ReadString(byte[] data, int at, int maxLen)
    {
        if (at >= data.Length) return "";
        int end = Math.Min(at + maxLen, data.Length);
        int len = 0;
        while (at + len < end && data[at + len] != 0) len++;
        return Encoding.Latin1.GetString(data, at, len).TrimEnd();
    }

    private static byte[] ReadHead(Stream stream, int count)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buf = new byte[count];
        if (stream.CanSeek) stream.Seek(0, SeekOrigin.Begin);
        int n = stream.Read(buf, 0, count);
        return n == count ? buf : buf[..n];
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream ms) return ms.ToArray();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
